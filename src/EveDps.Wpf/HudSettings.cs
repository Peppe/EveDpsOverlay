using System.IO;
using System.Text.Json;

namespace EveDps.Wpf;

/// <summary>
/// Persisted HUD state: window position and opacity. Size is fixed, so it is
/// not stored. See docs/spec/phase-3-hud-polish.md.
/// </summary>
public sealed class HudSettings
{
    /// <summary>Window opacity, 0.40 .. 1.00 (default 0.85).</summary>
    public double Opacity { get; set; } = 0.85;

    /// <summary>Restored window X; NaN = "no saved position, use the default corner".</summary>
    public double X { get; set; } = double.NaN;

    public double Y { get; set; } = double.NaN;

    /// <summary>
    /// The HUD's displayed window length (5/15/60; default 15). Drives the big
    /// OUT/IN numbers and, in later phases, the type bar and attackers panel.
    /// </summary>
    public int PrimaryWindowSeconds { get; set; } = 15;

    /// <summary>Phase 12: source-panel layout, "horizontal" | "vertical".</summary>
    public string Layout { get; set; } = "horizontal";

    private static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EveDpsOverlay");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    public static HudSettings Load()
    {
        try
        {
            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<HudSettings>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (loaded is not null)
            {
                // Guard against corrupt/old files: clamp to sane values.
                loaded.Opacity = Math.Clamp(
                    double.IsNaN(loaded.Opacity) ? 0.85 : loaded.Opacity, 0.40, 1.00);
                if (loaded.PrimaryWindowSeconds is not (5 or 15 or 60))
                    loaded.PrimaryWindowSeconds = 15;
                // Phase 12: anything but the exact vertical keyword is horizontal.
                if (loaded.Layout != "vertical")
                    loaded.Layout = "horizontal";
                return loaded;
            }
        }
        catch (FileNotFoundException)
        {
            // First run: defaults below.
        }
        catch (Exception)
        {
            // A broken settings file must never stop the overlay from starting.
        }
        return new HudSettings();
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}
