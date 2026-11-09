using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace EveDps.Wpf;

/// <summary>
/// Notification-area icon and menu. A frameless window has no close button,
/// so this is the app's mandatory control surface (phase 3 spec). Uses the
/// System.Windows.Forms NotifyIcon interop — no third-party packages. The
/// icon bitmap is drawn in code; there is no .ico asset.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>One opacity ring shared with the HUD's cycle button (phase 6 amendment).</summary>
    private static readonly double[] OpacityChoices = [1.00, 0.85, 0.60, 0.40];

    private readonly NotifyIcon _notify;
    private readonly List<ToolStripMenuItem> _opacityItems = new();

    /// <summary>User asked to toggle HUD visibility. Raised on the UI thread.</summary>
    public event Action? ShowHideRequested;

    /// <summary>User picked an opacity from the menu (0.40 .. 1.00).</summary>
    public event Action<double>? OpacityRequested;

    /// <summary>User asked the app to exit.</summary>
    public event Action? ExitRequested;

    public TrayIcon(double initialOpacity)
    {
        _notify = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Eve DPS Overlay",
            Visible = true,
        };

        var menu = new ContextMenuStrip();

        var showHide = new ToolStripMenuItem("Show / Hide HUD");
        showHide.Click += (_, _) => ShowHideRequested?.Invoke();
        menu.Items.Add(showHide);

        var opacity = new ToolStripMenuItem("Opacity");
        foreach (var value in OpacityChoices)
        {
            var item = new ToolStripMenuItem($"{value * 100:0}%") { Tag = value };
            item.Click += (_, _) => OpacityRequested?.Invoke(value);
            _opacityItems.Add(item);
            opacity.DropDownItems.Add(item);
        }
        menu.Items.Add(opacity);

        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exit);

        _notify.ContextMenuStrip = menu;
        SetOpacityCheckmarks(initialOpacity);
    }

    public void SetOpacityCheckmarks(double current)
    {
        foreach (var item in _opacityItems)
            item.Checked = Math.Abs((double)item.Tag! - current) < 1e-6;
    }

    /// <summary>draws the icon: dark disc, cyan ring, red tick — matches the HUD colors.</summary>
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var disc = new SolidBrush(Color.FromArgb(16, 20, 24));
            g.FillEllipse(disc, 0, 0, 63, 63);
            using var ring = new Pen(Color.FromArgb(0x66, 0xd9, 0xff), 6f);
            g.DrawEllipse(ring, 10, 10, 43, 43);
            using var tick = new Pen(Color.FromArgb(0xff, 0x6b, 0x6b), 6f);
            g.DrawLine(tick, 22, 32, 30, 42);
            g.DrawLine(tick, 30, 42, 44, 22);
        }
        // GetHicon creates an unmanaged icon; the tray icon owns it for the
        // process lifetime, so the single allocation of a cleanup handle is fine.
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
    }
}
