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

    /// <summary>Phase 8 row budget: five slots (4 names + Others when merging).</summary>
    private const int AttackerRowBudget = 5;
    private readonly RowVisual[] _attackerSlots = new RowVisual[AttackerRowBudget];
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

        // Phase 8: five attacker-row slots (reused every tick, no child churn).
        for (var i = 0; i < AttackerRowBudget; i++)
        {
            var slot = MakeAttackerSlot();
            _attackerSlots[i] = slot;
            AttackerRows.Children.Add(slot.Root);
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

        OutText.Text = $"OUT {primary.OutgoingDps:0.#} dps";
        // User amendment (2026-11-09): IN shows a numeric zero like OUT; the
        // old "IN —" dash style is retired.
        InText.Text = $"IN {primary.IncomingDps:0.#} dps";
        UpdateTypeBar(primary);
        RenderAttackers(primary);

        var others = string.Join(" · ", WindowChoices
            .Where(s => s != _primaryWindow)
            .Select(s => $"{s}s {WindowOf(snap.Windows, s).OutgoingDps:0.#}"));
        // After a replay finishes, its verdict line must survive — the tick
        // stops replacing the status line (big numbers keep updating).
        if (!_replayFinished)
            StatusText.Text =
                $"{others} · session {snap.Session.TotalOutgoing}/{snap.Session.TotalIncoming} · " +
                $"{snap.Session.Misses} misses";
    }

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

    // ── phase 8: incoming attackers panel ─────────────────────────────────

    /// <summary>One attacker row's UI parts (slot reused across ticks).</summary>
    private sealed record RowVisual(
        Grid Root, Border Bar, ColumnDefinition FillCol, ColumnDefinition RestCol,
        TextBlock Name, TextBlock Value);

    private static RowVisual MakeAttackerSlot()
    {
        // Two-column fill grid: stars carry the bar's proportional width so
        // WPF layout, not pixel math, keeps leader = 100 % exactly.
        var fill = new Grid();
        fill.ColumnDefinitions.Add(new ColumnDefinition());
        fill.ColumnDefinitions.Add(new ColumnDefinition());
        var bar = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0x6B, 0x6B)), // IN red, translucent
        };
        fill.Children.Add(bar);

        // Overlay text spans both fill columns: name left (with truncation),
        // value right; "small light text readable over the bar's fill".
        var overlay = new Grid();
        overlay.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        overlay.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock
        {
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 2, 0),
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
        return new RowVisual(root, bar, fill.ColumnDefinitions[0], fill.ColumnDefinitions[1], name, value);
    }

    /// <summary>
    /// Renders the phase-8 panel from the SAME primary-window snapshot the
    /// big IN number uses. The display model (ordering + budget merge into a
    /// trailing Others row) lives in Core's <see cref="AttackerPanel"/> so
    /// the rules are unit-testable; this method only draws. Leader (a name
    /// row or Others) is always 100 % wide; miss-only rows render dimmed with
    /// zero bar.
    /// </summary>
    private void RenderAttackers(DpsWindow primary)
    {
        var display = AttackerPanel.DisplayRows(primary.Attackers, AttackerRowBudget);

        var leader = display.Count == 0 ? 0 : display.Max(r => r.IncomingDps);

        for (var i = 0; i < _attackerSlots.Length; i++)
        {
            var slot = _attackerSlots[i];
            if (i >= display.Count)
            {
                slot.Root.Visibility = Visibility.Collapsed;
                continue;
            }
            var row = display[i];
            slot.Root.Visibility = Visibility.Visible;
            slot.Root.Opacity = row.Dimmed ? 0.55 : 1.0;
            slot.Name.Text = row.Name;
            slot.Value.Text = row.IncomingDps > 0
                ? row.IncomingDps.ToString("0.#")
                : row.Misses > 0 ? $"{row.Misses} misses" : "";

            if (leader > 0)
            {
                var share = Math.Min(1.0, row.IncomingDps / leader);
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

    // ── phase 6 cycle buttons ─────────────────────────────────────────

    private void CyclePrimaryWindow()
    {
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
            OutgoingTypes: [], IncomingTypes: [], Attackers: []);
}
