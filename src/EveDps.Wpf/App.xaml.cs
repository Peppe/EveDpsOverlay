using System.Globalization;
using System.Windows;
using Application = System.Windows.Application;

namespace EveDps.Wpf;

public partial class App : Application
{
    /// <summary>
    /// Phase 9 replay-mode startup options, parsed in OnStartup:
    ///   EveDps.Wpf.exe --replay &lt;file&gt; [--speed N]
    /// Never persisted; live tailing is disabled while replaying.
    /// </summary>
    public static string? ReplayPath { get; internal set; }
    public static double ReplaySpeed { get; internal set; } = 60.0;

    protected override void OnStartup(StartupEventArgs e)
    {
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--replay" && i + 1 < e.Args.Length && !e.Args[i + 1].StartsWith("--"))
                ReplayPath = e.Args[++i];
            else if (e.Args[i] == "--speed" && i + 1 < e.Args.Length &&
                     double.TryParse(e.Args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                ReplaySpeed = s > 0 ? s : 60.0;
        }
        base.OnStartup(e);
    }
}
