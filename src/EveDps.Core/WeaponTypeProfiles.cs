using System.Text.Json;

namespace EveDps.Core;

/// <summary>
/// The damage types EVE tracks. The fifth "type" Unknown collects damage from
/// weapons without a profile — attribute-wise a bucket, not a real type.
/// </summary>
public static class DamageTypes
{
    public const string Em = "EM";
    public const string Thermal = "Thermal";
    public const string Kinetic = "Kinetic";
    public const string Explosive = "Explosive";
    public const string Unknown = "Unknown";

    /// <summary>Fixed segment order used everywhere downstream (HUD bar, console).</summary>
    public static readonly string[] Attributed = [Em, Thermal, Kinetic, Explosive];

    /// <summary>All five buckets, including Unknown.</summary>
    public static readonly string[] All = [.. Attributed, Unknown];
}

/// <summary>
/// User-curated weapon → damage-type weight table (phase 4). EVE's gamelog
/// lines do not carry the damage type, so this is INFERRED data, never
/// measured — see docs/spec/phase-4-damage-types.md. Pointed at
/// %APPDATA%\EveDpsOverlay\type-profiles.json by the host apps; a missing or
/// broken file makes the feature inert (everything attributes to Unknown).
/// </summary>
public sealed class WeaponTypeProfiles
{
    private readonly Dictionary<string, double[]> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public static WeaponTypeProfiles Empty { get; } = new();

    private WeaponTypeProfiles() { }

    /// <summary>Names known to the table (useful for diagnostics/tests).</summary>
    public IReadOnlyCollection<string> Weapons => _profiles.Keys;

    public bool HasProfile(string weapon) =>
        !string.IsNullOrEmpty(weapon) && _profiles.ContainsKey(weapon);

    /// <summary>
    /// Weight vector for a weapon in fixed order EM/Thermal/Kinetic/Explosive,
    /// normalized to sum to 1; null when the weapon has no usable profile.
    /// Lookup is case-insensitive.
    /// </summary>
    public double[]? GetWeights(string weapon) =>
        string.IsNullOrEmpty(weapon) ? null
            : _profiles.TryGetValue(weapon.Trim(), out var w) ? w : null;

    // JSON shape:
    // { "profiles": { "Civilian Gatling Railgun": { "EM": 0.4, "Kinetic": 0.6 } } }
    private sealed class FileShape
    {
        public Dictionary<string, Dictionary<string, double>>? Profiles { get; set; }
    }

    /// <summary>Case-insensitive so hand-edited JSON ("profiles" vs "Profiles") just works.</summary>
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static WeaponTypeProfiles Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<FileShape>(json, Json);
            if (file?.Profiles is null || file.Profiles.Count == 0)
                return Empty;

            var store = new WeaponTypeProfiles();
            foreach (var (weapon, weights) in file.Profiles)
            {
                var normalized = Normalize(weights);
                if (normalized is not null)
                    store._profiles[weapon.Trim()] = normalized;
            }
            return store._profiles.Count > 0 ? store : Empty;
        }
        catch (Exception)
        {
            // Missing/corrupt file must never break the host app: all Unknown.
            return Empty;
        }
    }

    /// <summary>
    /// Builds the fixed-order EM/Thermal/Kinetic/Explosive vector: negative
    /// weights clamp to 0, missing types default to 0, the vector sums to 1.
    /// A row with no positive weight is unusable → null (weapon = Unknown).
    /// Shared with the attacker table (phase 10) — same file shape, same
    /// normalization, same inert rules.
    /// </summary>
    internal static double[]? Normalize(Dictionary<string, double> weights)
    {
        var vector = new double[DamageTypes.Attributed.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            weights.TryGetValue(DamageTypes.Attributed[i], out var value);
            vector[i] = Math.Max(0, value);
        }
        var total = vector.Sum();
        if (total <= 0) return null;
        for (var i = 0; i < vector.Length; i++)
            vector[i] /= total;
        return vector;
    }
}
