using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EveDps.Core;
using Application = System.Windows.Application;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;

namespace EveDps.Wpf;

/// <summary>
/// The overlay HUD: frameless, transparent, topmost. All disk I/O happens in
/// the tailer's own task; the UI thread only takes snapshots under a lock.
/// See docs/spec/phase-3-hud-polish.md.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "EVE", "logs", "Gamelogs");

    private readonly HudSettings _settings = HudSettings.Load();
    private readonly object _aggGate = new();
    private readonly DpsAggregator _agg = new(
        WeaponTypeProfiles.Load(ProfilePath()),
        AttackerTypeProfiles.Load(AttackerProfilePath()));
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly TrayIcon _tray;
    private GamelogTailer? _tailer;
    private ReplayFeed? _replay;
    private volatile bool _replayFinished;
    private Func<DateTimeOffset> _clock;
    private int _primaryWindow;

    /// <summary>Same file the console reads; user-curated weapon → type table.</summary>
    private static string ProfilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EveDpsOverlay", "type-profiles.json");

    /// <summary>Phase 10: user-curated attacker → type table (inferred tier).</summary>
    private static string AttackerProfilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EveDpsOverlay", "attacker-profiles.json");

    /// <summary>Segments in DamageTypes.All order, paired with their UI parts.</summary>
    private readonly (ColumnDefinition Col, Border Seg, TextBlock Label)[] _bar =
        new (ColumnDefinition, Border, TextBlock)[DamageTypes.All.Length];

    /// <summary>Phase 10: the IN bar's same-shape segment table.</summary>
    private readonly (ColumnDefinition Col, Border Seg, TextBlock Label)[] _barIn =
        new (ColumnDefinition, Border, TextBlock)[DamageTypes.All.Length];

    /// <summary>Phase 8 row budget: five slots (4 names + Others when merging); shared by both panels.</summary>
    private const int SourceRowBudget = 5;
    /// <summary>Phase 12 window widths: horizontal = the historical 300 px, vertical ~half.</summary>
    private const double HorizontalWidth = 300;
    private const double VerticalWidth = 156;
    private readonly RowVisual[] _attackerSlots = new RowVisual[SourceRowBudget];
    private readonly RowVisual[] _outSlots = new RowVisual[SourceRowBudget];
    /// <summary>True until the initial layout settles; SizeChanged growth
    /// anchoring must not fight the first render or window placement.</summary>
    private bool _layoutSettling = true;

    /// <summary>Ascending fixed window set; the primary is cycled through it.</summary>
    private static readonly int[] WindowChoices = [5, 15, 60];

    /// <summary>One ring shared by the cycle button and both menus (phase 6).</summary>
    private static readonly double[] OpacityChoices = [1.00, 0.85, 0.60, 0.40];

    public MainWindow()
    {
        InitializeComponent();
        Opacity = _settings.Opacity;
        _primaryWindow = _settings.PrimaryWindowSeconds;
        _clock = App.ReplayPath is not null
            ? () => _replay!.CurrentVirtualTime
            : () => DateTimeOffset.UtcNow;

        // Phase 5: fill the segment table in DamageTypes.All order (EM, TH,
        // KIN, EX, Unknown) so snapshot indices map straight onto UI parts.
        _bar[0] = (ColEm, SegEm, EmLabel);
        _bar[1] = (ColThermal, SegThermal, ThermalLabel);
        _bar[2] = (ColKinetic, SegKinetic, KineticLabel);
        _bar[3] = (ColExplosive, SegExplosive, ExplosiveLabel);
        _bar[4] = (ColUnknown, SegUnknown, UnknownLabel);

        // Phase 10: the IN bar's table, same order, same mapping.
        _barIn[0] = (InColEm, InSegEm, InEmLabel);
        _barIn[1] = (InColThermal, InSegThermal, InThermalLabel);
        _barIn[2] = (InColKinetic, InSegKinetic, InKineticLabel);
        _barIn[3] = (InColExplosive, InSegExplosive, InExplosiveLabel);
        _barIn[4] = (InColUnknown, InSegUnknown, InUnknownLabel);

        // Phase 8/11: five row slots per panel (reused every tick, no churn).
        // Phase 12: the IN panel mirrors the big numbers' alignment — its
        // rows read from the right edge (name right, dps/misses outermost).
        for (var i = 0; i < SourceRowBudget; i++)
        {
            var outSlot = MakeSourceSlot(nameRight: false);
            _outSlots[i] = outSlot;
            OutRows.Children.Add(outSlot.Root);
            var inSlot = MakeSourceSlot(nameRight: true);
            _attackerSlots[i] = inSlot;
            AttackerRows.Children.Add(inSlot.Root);
        }
        SizeChanged += OnWindowSizeChanged; // bottom-anchored growth

        _tray = new TrayIcon(_settings.Opacity);
        // Tray events arrive on the UI thread; BeginInvoke keeps ordering explicit.
        _tray.ShowHideRequested += () => Dispatcher.BeginInvoke(ToggleVisibility);
        _tray.OpacityRequested += value => Dispatcher.BeginInvoke(() => ApplyOpacity(value));
        _tray.ExitRequested += () => Dispatcher.BeginInvoke(ExitApp);

        PrimaryButton.Content = $"{_primaryWindow}s";
        OpacityButton.Content = $"{Math.Round(Opacity * 100)}%";
        PrimaryButton.Click += (_, _) => CyclePrimaryWindow();
        OpacityButton.Click += (_, _) => CycleOpacity();
        LayoutButton.Click += (_, _) => ToggleLayout();
        // Phase 12: geometry follows the saved layout BEFORE placement, so
        // PlaceWindow clamps the right shape into the virtual screen.
        ApplyLayout(_settings.Layout);

        _timer.Tick += (_, _) => UpdateHud();
        _timer.Start();

        Loaded += (_, _) =>
        {
            PlaceWindow();
            StartTailer();
            Dispatcher.BeginInvoke(new Action(() => _layoutSettling = false),
                DispatcherPriority.ApplicationIdle);
        };
    }

    // ── tailer / replay wiring (background task; no disk I/O on the UI thread) ──

    private void StartTailer()
    {
        // Phase 9: a --replay startup arg swaps the live tailer for a paced
        // replay feed; the real log directory is never touched in that mode.
        if (App.ReplayPath is not null)
        {
            if (!File.Exists(App.ReplayPath))
            {
                StatusText.Text = "replay file not found — following live logs";
                App.ReplayPath = null;
                _clock = () => DateTimeOffset.UtcNow;
            }
            else
            {
                _replay = new ReplayFeed(App.ReplayPath, App.ReplaySpeed);
                _replay.Line += OnTailedLine;
                _replay.Finished += () =>
                {
                    // Threads: set under the chain from the feed task; read
                    // on the UI thread. Freeze the status line: the 200 ms
                    // tick no longer overwrites it, and the final session
                    // totals go out with the verdict line.
                    _replayFinished = true;
                    Dispatcher.BeginInvoke(() =>
                    {
                        DpsSnapshot snap;
                        lock (_aggGate) snap = _agg.Take(_replay!.CurrentVirtualTime);
                        StatusText.Text =
                            $"replay finished · session {snap.Session.TotalOutgoing}/" +
                            $"{snap.Session.TotalIncoming} · {snap.Session.Misses} misses";
                    });
                };
                _replay.Start();
                StatusText.Text =
                    $"replaying {Path.GetFileName(App.ReplayPath)} (×{_replay.Speed:0.##})";
                return;
            }
        }

        _tailer = new GamelogTailer(LogDir, new GamelogTailerOptions
        {
            StartAtEnd = true, // a restart never replays history that was observed
        });
        _tailer.Line += OnTailedLine;
        _tailer.Following += path => Dispatcher.BeginInvoke(() =>
            StatusText.Text = $"following {Path.GetFileName(path)}");
        _tailer.PollError += _ => Dispatcher.BeginInvoke(() =>
            StatusText.Text = "log read error (recovering…)");
        _tailer.Start();
    }

    private void OnTailedLine(TailedLine line)
    {
        var ev = LogParser.ParseCombat(line.Text);
        if (ev is null) return;
        lock (_aggGate)
            _agg.Add(ev);
    }

    // ── HUD refresh: one snapshot per tick ─────────────────────────────

    private void UpdateHud()
    {
        // Phase 9 race fix: until the replay feed has seen its first
        // timestamped line, CurrentVirtualTime is uninitialized (epoch) and
        // window math on it overflows — skip this tick instead.
        if (_replay is { TimeKnown: false }) return;

        DpsSnapshot snap;
        lock (_aggGate)
            snap = _agg.Take(_clock());

        // Big numbers follow the primary window; the small line shows the rest.
        var primary = WindowOf(snap.Windows, _primaryWindow);

        var outLine = $"OUT {primary.OutgoingDps:0.#} dps";
        // User amendment (2026-11-09): IN shows a numeric zero like OUT; the
        // old "IN —" dash style is retired.
        var inLine = $"IN {primary.IncomingDps:0.#} dps";
        OutText.Text = outLine;
        InText.Text = inLine;
        // Phase 12: the vertical layout's stacked number pair shows the same
        // strings — pure presentation, one source of truth.
        OutTextV.Text = outLine;
        InTextV.Text = inLine;
        UpdateTypeBar(primary);
        // Phase 11: both source panels render from the same window snapshot.
        RenderPanel(_outSlots, SourcePanel.DisplayRows(primary.OutgoingSources, SourceRowBudget), OutPanel);
        RenderPanel(_attackerSlots, SourcePanel.DisplayRows(primary.Attackers, SourceRowBudget), InPanel);

        // After a replay finishes, its verdict line must survive — the tick
        // stops replacing the status line (big numbers keep updating).
        if (!_replayFinished)
        {
            // Phase 12: vertical is too narrow for the other-windows line —
            // the status abbreviates to session totals only.
            StatusText.Text = VerticalLayout
                ? $"session {snap.Session.TotalOutgoing}/{snap.Session.TotalIncoming} · " +
                  $"{snap.Session.Misses} misses"
                : $"{string.Join(" · ", WindowChoices
                    .Where(s => s != _primaryWindow)
                    .Select(s => $"{s}s {WindowOf(snap.Windows, s).OutgoingDps:0.#}"))}" +
                  $" · session {snap.Session.TotalOutgoing}/{snap.Session.TotalIncoming} · " +
                  $"{snap.Session.Misses} misses";
        }
    }

    /// <summary>Phase 12: the persisted layout as a plain flag.</summary>
    private bool VerticalLayout => _settings.Layout == "vertical";

    // ── phase 5 / phase 10: damage-type bars ─────────────────────────────

    /// <summary>
    /// Renders both bars from the SAME window snapshot the text uses. Stars
    /// are set to each type's dps share (sum of stars is the total, so
    /// segments always sum to exactly 100 % of bar width); a zero-dps type
    /// occupies zero width; a fully idle pass-through shows the neutral
    /// full-width gray bar. Phase 10 renders the IN bar from IncomingTypes
    /// with identical rules (the IN frame adds no logic — Core already
    /// guarantees the five buckets sum to the window's IncomingDps).
    /// </summary>
    private void UpdateTypeBar(DpsWindow primary)
    {
        SetBar(_bar, primary.OutgoingTypes, TypeBarEmpty, TypeBarSegs);
        SetBar(_barIn, primary.IncomingTypes, TypeBarInEmpty, TypeBarInSegs);
    }

    private static void SetBar(
        (ColumnDefinition Col, Border Seg, TextBlock Label)[] bar,
        IReadOnlyList<DpsType> types,
        UIElement emptyFallback,
        UIElement segments)
    {
        var total = types.Sum(t => t.Dps);
        if (total <= 0)
        {
            emptyFallback.Visibility = Visibility.Visible;
            segments.Visibility = Visibility.Collapsed;
            return;
        }

        emptyFallback.Visibility = Visibility.Collapsed;
        segments.Visibility = Visibility.Visible;

        // ActualWidth is 0 before first layout; numbers were hidden anyway.
        var px = ((FrameworkElement)segments).ActualWidth;
        for (var i = 0; i < bar.Length; i++)
        {
            var dps = types[i].Dps; // same fixed order as bar
            bar[i].Col.Width = dps > 0
                ? new GridLength(dps / total, GridUnitType.Star)
                : new GridLength(0, GridUnitType.Pixel);
            // Per-segment number only on segments ~30 logical px wide —
            // never overlap text between adjacent segments.
            bar[i].Label.Text =
                dps > 0 && Double.IsFinite(px) && px * (dps / total) >= 30
                    ? dps.ToString("0.#")
                    : "";
        }
    }

    /// <summary>Rounds off the bar's ends by clipping its segment grid.</summary>
    private void TypeBar_SizeChanged(object sender, SizeChangedEventArgs e) =>
        TypeBarSegs.Clip = new RectangleGeometry(
            new Rect(0, 0, TypeBarSegs.ActualWidth, TypeBarSegs.ActualHeight), 3, 3);

    private void TypeBarIn_SizeChanged(object sender, SizeChangedEventArgs e) =>
        TypeBarInSegs.Clip = new RectangleGeometry(
            new Rect(0, 0, TypeBarInSegs.ActualWidth, TypeBarInSegs.ActualHeight), 2, 2);

    // ── phase 8/11: source panels (attackers + outgoing weapons) ─────────

    /// <summary>
    /// Segment colors in DamageTypes.All order — the established type-bar
    /// palette, drawn translucent so row text stays readable (the panels'
    /// established look).
    /// </summary>
    private static readonly System.Windows.Media.Brush[] RowSegmentBrushes =
    [
        new SolidColorBrush(Color.FromArgb(0x59, 0x4d, 0x7c, 0xff)), // EM
        new SolidColorBrush(Color.FromArgb(0x59, 0xff, 0x5a, 0x4e)), // Thermal
        new SolidColorBrush(Color.FromArgb(0x59, 0x9a, 0xa3, 0xad)), // Kinetic
        new SolidColorBrush(Color.FromArgb(0x59, 0xff, 0x98, 0x54)), // Explosive
        new SolidColorBrush(Color.FromArgb(0x59, 0x56, 0x5d, 0x66)), // Unknown
    ];

    /// <summary>One source row's UI parts (slot reused across ticks).</summary>
    private sealed record RowVisual(
        Grid Root, ColumnDefinition FillCol, ColumnDefinition RestCol,
        Grid BarSegs, ColumnDefinition[] SegmentCols, TextBlock Name, TextBlock Value);

    /// <summary>
    /// One source row's UI parts (slot reused across ticks). Phase 12:
    /// <paramref name="nameRight"/> mirrors the IN panel to read from the
    /// window's right edge, like the big IN number.
    /// </summary>
    private static RowVisual MakeSourceSlot(bool nameRight)
    {
        // Two-column fill grid: stars carry the bar's proportional width so
        // WPF layout, not pixel math, keeps leader = 100 % exactly.
        var fill = new Grid();
        fill.ColumnDefinitions.Add(new ColumnDefinition());
        fill.ColumnDefinitions.Add(new ColumnDefinition());

        // Phase 11: the bar is segmented by the row's profiled type split
        // (same palette/order as the type bars). Rounded ends come from
        // clipping the segment grid, mirroring the type-bar approach.
        var barSegs = new Grid();
        var segmentCols = new ColumnDefinition[DamageTypes.All.Length];
        for (var i = 0; i < DamageTypes.All.Length; i++)
        {
            var col = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Pixel) };
            segmentCols[i] = col;
            barSegs.ColumnDefinitions.Add(col);
            var seg = new Border { Background = RowSegmentBrushes[i] };
            Grid.SetColumn(seg, i);
            barSegs.Children.Add(seg);
        }
        barSegs.SizeChanged += (s, _) =>
        {
            var g = (Grid)s!;
            g.Clip = new RectangleGeometry(
                new Rect(0, 0, g.ActualWidth, g.ActualHeight), 6, 6);
        };
        fill.Children.Add(barSegs);

        // Overlay text spans both fill columns: name on the anchored edge
        // (with truncation), value at the opposite outer edge; "small light
        // text readable over the bar's fill".
        var overlay = new Grid();
        overlay.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        overlay.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock
        {
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = nameRight ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left,
            Margin = nameRight ? new Thickness(2, 0, 2, 0) : new Thickness(5, 0, 2, 0),
        };
        var value = new TextBlock
        {
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 5, 0),
        };
        overlay.Children.Add(name);
        Grid.SetColumn(value, 1);
        overlay.Children.Add(value);

        var root = new Grid { Height = 13 };
        root.Children.Add(fill);
        root.Children.Add(overlay);
        return new RowVisual(root, fill.ColumnDefinitions[0], fill.ColumnDefinitions[1], barSegs, segmentCols, name, value);
    }

    /// <summary>
    /// Renders ONE panel from the SAME primary-window snapshot the big
    /// numbers use. The display model (ordering + budget merge into a
    /// trailing Others row) lives in Core's <see cref="SourcePanel"/> so the
    /// rules are unit-testable; this method only draws. Leader (a name row
    /// or Others) is always 100 % wide; miss-only rows render dimmed with
    /// zero bar; the row bar itself is segmented by the row's profiled type
    /// split (phase 11), with the unclaimed remainder as the Unknown slice.
    /// </summary>
    private void RenderPanel(RowVisual[] slots, IReadOnlyList<PanelRow> display, UIElement panelHost)
    {
        panelHost.Visibility = display.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var leader = display.Count == 0 ? 0 : display.Max(r => r.Dps);

        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            if (i >= display.Count)
            {
                slot.Root.Visibility = Visibility.Collapsed;
                continue;
            }
            var row = display[i];
            slot.Root.Visibility = Visibility.Visible;
            slot.Root.Opacity = row.Dimmed ? 0.55 : 1.0;
            slot.Name.Text = row.Name;
            slot.Value.Text = row.Dps > 0
                ? row.Dps.ToString("0.#")
                : row.Misses > 0 ? $"{row.Misses} misses" : "";

            if (leader > 0)
            {
                var share = Math.Min(1.0, row.Dps / leader);
                slot.FillCol.Width = new GridLength(share, GridUnitType.Star);
                slot.RestCol.Width = share < 1.0
                    ? new GridLength(1 - share, GridUnitType.Star)
                    : new GridLength(0, GridUnitType.Star);
            }
            else
            {
                // No leader: nothing is 100 % wide (all-miss-only window).
                slot.FillCol.Width = new GridLength(0, GridUnitType.Star);
                slot.RestCol.Width = new GridLength(0, GridUnitType.Star);
            }

            // Segment widths: each type's share OF THIS ROW's dps (the bar
            // already spans share × panel width). Unclaimed remainder =
            // Unknown slice; a fully unmapped source renders one gray bar.
            var rowDps = row.Dps;
            var slices = new[] { row.Em, row.Thermal, row.Kinetic, row.Explosive };
            var remainder = Math.Max(0, rowDps - slices.Sum());
            for (var s = 0; s < slot.SegmentCols.Length; s++)
            {
                var sliceDps = s < slices.Length ? slices[s] : remainder;
                slot.SegmentCols[s].Width = rowDps > 0 && sliceDps > 0
                    ? new GridLength(sliceDps / rowDps, GridUnitType.Star)
                    : new GridLength(0, GridUnitType.Pixel);
            }
        }
    }

    /// <summary>
    /// Bottom edge stays anchored while the panel grows upward (the default
    /// bottom-right placement never drifts off-screen; saved positions get
    /// clamped into the virtual screen just like PlaceWindow does).
    /// </summary>
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_layoutSettling || e.PreviousSize.Height <= 0 ||
            Math.Abs(e.PreviousSize.Height - e.NewSize.Height) < 0.5)
            return;

        Top += e.PreviousSize.Height - e.NewSize.Height;
        var virtualScreen =
            new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                     SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (Top + ActualHeight > virtualScreen.Bottom)
            Top = virtualScreen.Bottom - ActualHeight;
        if (Top < virtualScreen.Top)
            Top = virtualScreen.Top;
        SavePosition();
    }

    /// <summary>
    /// Opens the profiles file in the default editor; a missing file is
    /// seeded with an empty template first so the open always works. One-shot
    /// menu action — the small synchronous write is deliberate and local.
    /// </summary>
    private void OpenProfilesFile()
    {
        try
        {
            if (!File.Exists(ProfilePath()))
                File.WriteAllText(ProfilePath(), "{\n  \"profiles\": {\n  }\n}\n");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ProfilePath(),
                UseShellExecute = true,
            });
        }
        catch
        {
            // Never let menu actions kill the HUD.
        }
    }

    /// <summary>
    /// Phase 10: same seed-then-open mechanics for the attacker table. The
    /// seed notes the wildcard key form; the missing/corrupt file stays inert
    /// in Core either way.
    /// </summary>
    private void OpenAttackerProfilesFile()
    {
        try
        {
            if (!File.Exists(AttackerProfilePath()))
                File.WriteAllText(AttackerProfilePath(),
                    "{\n  \"attackers\": {\n    \"Coreli*\": { \"Explosive\": 0.7, \"Kinetic\": 0.3 }\n  }\n}\n");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AttackerProfilePath(),
                UseShellExecute = true,
            });
        }
        catch
        {
            // Never let menu actions kill the HUD.
        }
    }

    // ── position / opacity persistence ────────────────────────────────

    private void PlaceWindow()
    {
        var virtualScreen =
            new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                     SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var onScreen = !double.IsNaN(_settings.X) && !double.IsNaN(_settings.Y)
                       && virtualScreen.Contains(new Point(_settings.X, _settings.Y));

        if (onScreen)
        {
            Left = _settings.X;
            Top = _settings.Y;
        }
        else
        {
            // Default: bottom-right of the primary work area, clear of the taskbar.
            var work = SystemParameters.WorkArea;
            Left = work.Right - Width - 20;
            Top = work.Bottom - ActualHeight - 10; // ActualHeight: SizeToContent keeps Height unset
        }
    }

    private void SavePosition()
    {
        _settings.X = Left;
        _settings.Y = Top;
        _settings.Save();
    }

    private void ApplyOpacity(double value)
    {
        Opacity = value;
        _settings.Opacity = value;
        _tray.SetOpacityCheckmarks(value);
        OpacityButton.Content = $"{Math.Round(value * 100)}%";
        _settings.Save();
    }

    // ── phase 12: layout toggle (horizontal ↔ vertical) ───────────────

    /// <summary>Button click: flips the persisted layout and re-renders.</summary>
    private void ToggleLayout() =>
        ApplyLayout(VerticalLayout ? "horizontal" : "vertical");

    /// <summary>
    /// Applies a layout: chrome (title hidden, numbers stacked, status
    /// abbreviated — handled by UpdateHud + visibility here), panel
    /// restacking (columns vs rows inside PanelArea), and the right-edge
    /// anchored window width (the HUD lives bottom-right; the LEFT edge
    /// moves). Height growth stays bottom-anchored via OnWindowSizeChanged.
    /// </summary>
    private void ApplyLayout(string layout)
    {
        _settings.Layout = layout;
        _settings.Save();
        var vertical = layout == "vertical";

        // Chrome: the title text hides; the cycle buttons stay in one row
        // and tighten to near-icon size; the toggle's icon shows the layout
        // clicking it will switch TO (action-oriented, like the spec says).
        TitleText.Visibility = vertical ? Visibility.Collapsed : Visibility.Visible;
        NumberRow.Visibility = vertical ? Visibility.Collapsed : Visibility.Visible;
        NumberStack.Visibility = vertical ? Visibility.Visible : Visibility.Collapsed;
        var tight = vertical ? new Thickness(2, 0, 2, 0) : new Thickness(4, 0, 4, 0);
        PrimaryButton.Padding = tight;
        OpacityButton.Padding = tight;
        LayoutButton.Padding = tight;
        LayoutButton.Content = vertical ? "\u2194" : "\u2195";

        // Panels: horizontal = side-by-side halves (columns); vertical =
        // stacked at full width (rows). Rebuild the definitions; the panels'
        // attached indices point into whichever axis is active.
        PanelArea.ColumnDefinitions.Clear();
        PanelArea.RowDefinitions.Clear();
        if (vertical)
        {
            PanelArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PanelArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(OutPanel, 0);
            Grid.SetColumn(InPanel, 0);
            Grid.SetRow(OutPanel, 0);
            Grid.SetRow(InPanel, 1);
            OutPanel.Margin = new Thickness(0);
            InPanel.Margin = new Thickness(0, 4, 0, 0);
        }
        else
        {
            PanelArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            PanelArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(OutPanel, 0);
            Grid.SetRow(InPanel, 0);
            Grid.SetColumn(OutPanel, 0);
            Grid.SetColumn(InPanel, 1);
            OutPanel.Margin = new Thickness(0, 0, 4, 0);
            InPanel.Margin = new Thickness(4, 0, 0, 0);
        }

        // Right edge stays fixed on width change: move the left edge by the
        // width delta, then clamp into the virtual screen.
        var newWidth = vertical ? VerticalWidth : HorizontalWidth;
        if (Math.Abs(Width - newWidth) > 0.5)
        {
            Left += Width - newWidth;
            Width = newWidth;
        }
        var virtualScreen =
            new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                     SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (Left + ActualWidth > virtualScreen.Right)
            Left = virtualScreen.Right - ActualWidth;
        if (Left < virtualScreen.Left)
            Left = virtualScreen.Left;
        SavePosition();

        UpdateHud(); // both panels re-render synchronously, same tick
    }

    // ── phase 6 cycle buttons ─────────────────────────────────────────

    private void CyclePrimaryWindow()    {
        var index = Array.IndexOf(WindowChoices, _primaryWindow);
        ApplyPrimaryWindow(WindowChoices[(index + 1) % WindowChoices.Length]);
    }

    private void ApplyPrimaryWindow(int seconds)
    {
        _primaryWindow = seconds;
        _settings.PrimaryWindowSeconds = seconds;
        _settings.Save();
        PrimaryButton.Content = $"{seconds}s";
        UpdateHud(); // re-base everything now, not on the next tick
    }

    private void CycleOpacity()
    {
        // Next ring step strictly below the current value; wraps to 100 %.
        // Legal off-ring states (legacy settings like 0.80) round down to
        // the next step, per the phase-6 spec.
        var next = OpacityChoices.FirstOrDefault(v => v < Opacity - 1e-6);
        if (next == 0)
            next = OpacityChoices[0];
        ApplyOpacity(next);
    }

    // ── input & shell wiring ──────────────────────────────────────────

    private void ToggleVisibility()
    {
        if (Visibility == Visibility.Visible)
            Visibility = Visibility.Hidden;
        else
            Visibility = Visibility.Visible;
    }

    private void ExitApp()
    {
        _timer.Stop();
        SavePosition();
        _tailer?.Dispose();
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        DragMove();          // blocks until release
        SavePosition();      // remember where the user dragged it
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var menu = new ContextMenu();

        var toggle = new MenuItem { Header = "Hide HUD" };
        toggle.Click += (_, _) => ToggleVisibility();
        menu.Items.Add(toggle);

        var opacity = new MenuItem { Header = "Opacity" };
        foreach (var value in OpacityChoices)
        {
            var item = new MenuItem
            {
                Header = $"{value * 100:0}%",
                IsChecked = Math.Abs(Opacity - value) < 1e-6,
            };
            item.Click += (_, _) => ApplyOpacity(value);
            opacity.Items.Add(item);
        }
        menu.Items.Add(opacity);

        menu.Items.Add(new Separator());

        // Phase 5: one-click weapon → type mapping workflow.
        var profiles = new MenuItem { Header = "Open profiles file" };
        profiles.Click += (_, _) => OpenProfilesFile();
        menu.Items.Add(profiles);

        // Phase 10: the attacker table behind the IN bar's inferred tier.
        var attackerProfiles = new MenuItem { Header = "Open attacker profiles file" };
        attackerProfiles.Click += (_, _) => OpenAttackerProfilesFile();
        menu.Items.Add(attackerProfiles);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(exit);

        menu.IsOpen = true;
    }

    private static DpsWindow WindowOf(IReadOnlyList<DpsWindow> windows, double seconds) =>
        windows.FirstOrDefault(w => w.Seconds == seconds)
        ?? new DpsWindow(seconds, OutgoingDps: 0, IncomingDps: 0, Shots: 0,
            OutgoingTypes: [], IncomingTypes: [], OutgoingSources: [], Attackers: []);
}
