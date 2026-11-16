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
/// Weapon → damage-type weight table (phase 4). EVE's gamelog lines do not
/// carry the damage type, so this is INFERRED data, never measured — see
/// docs/spec/phase-4-damage-types.md. The main source is a built-in table
/// embedded in this assembly (2026-11-16 amendment: drones by race, sentries,
/// Gecko; missile ammo names are covered by an element-keyword rule). The
/// optional file at %APPDATA%\EveDpsOverlay\type-profiles.json, pointed at
/// by the host apps, only OVERRIDES per-weapon entries — a missing or broken
/// file leaves the built-in table active.
/// </summary>
public sealed class WeaponTypeProfiles
{
    private readonly Dictionary<string, double[]> _profiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Missile ammo always carries its element in the name (Inferno
    /// = Thermal, Mjolnir = EM, Scourge = Kinetic, Nova = Explosive), so one
    /// keyword rule covers every size, T2 variant word and faction prefix.
    /// Only instances built from data have this enabled; <see cref="Empty"/>
    /// stays fully inert.</summary>
    private bool _missileRule;

    public static WeaponTypeProfiles Empty { get; } = new();

    private WeaponTypeProfiles() { }

    /// <summary>Names known to the table (useful for diagnostics/tests).</summary>
    public IReadOnlyCollection<string> Weapons => _profiles.Keys;

    public bool HasProfile(string weapon) =>
        !string.IsNullOrEmpty(weapon) &&
        (_profiles.ContainsKey(weapon) || MissileWeights(weapon) is not null);

    /// <summary>
    /// Weight vector for a weapon in fixed order EM/Thermal/Kinetic/Explosive,
    /// normalized to sum to 1; null when the weapon has no usable profile.
    /// Lookup is case-insensitive; falls back to the missile element rule.
    /// </summary>
    public double[]? GetWeights(string weapon) =>
        string.IsNullOrEmpty(weapon) ? null
            : _profiles.TryGetValue(weapon.Trim(), out var w) ? w
            : MissileWeights(weapon);

    private double[]? MissileWeights(string weapon) =>
        _missileRule ? weapon.Contains("Inferno", StringComparison.OrdinalIgnoreCase) ? MissileVectors.Thermal
            : weapon.Contains("Mjolnir", StringComparison.OrdinalIgnoreCase) ? MissileVectors.Em
            : weapon.Contains("Scourge", StringComparison.OrdinalIgnoreCase) ? MissileVectors.Kinetic
            : weapon.Contains("Nova", StringComparison.OrdinalIgnoreCase) ? MissileVectors.Explosive
            : null
        : null;

    /// <summary>Fixed-order vectors for the missile element rule.</summary>
    private static class MissileVectors
    {
        public static readonly double[] Em = [1, 0, 0, 0];
        public static readonly double[] Thermal = [0, 1, 0, 0];
        public static readonly double[] Kinetic = [0, 0, 1, 0];
        public static readonly double[] Explosive = [0, 0, 0, 1];
    }

    // JSON shape:
    // { "profiles": { "Civilian Gatling Railgun": { "EM": 0.4, "Kinetic": 0.6 } } }
    private sealed class FileShape
    {
        public Dictionary<string, Dictionary<string, double>>? Profiles { get; set; }
    }

    /// <summary>Case-insensitive so hand-edited JSON ("profiles" vs "Profiles") just works.</summary>
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Built-in defaults shipped inside the assembly (main source, 2026-11-16
    /// amendment): drones per race, sentries, Gecko. Missile ammo resolves via
    /// the element-keyword rule rather than per-name entries.
    /// </summary>
    public static WeaponTypeProfiles LoadDefaults() => FromJson(EmbeddedProfilesJson());

    /// <summary>
    /// Built-in defaults as the base, with the optional file at <paramref
    /// name="path"/> overriding per-weapon entries (file wins). Missing,
    /// empty or corrupt file → defaults only, never a crash.
    /// </summary>
    public static WeaponTypeProfiles Load(string path)
    {
        var merged = LoadDefaults();
        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<FileShape>(json, Json);
            if (file?.Profiles is null) return merged;

            foreach (var (weapon, weights) in file.Profiles)
            {
                var normalized = Normalize(weights);
                if (normalized is not null)
                    merged._profiles[weapon.Trim()] = normalized;
            }
            return merged;
        }
        catch (Exception)
        {
            // Missing/corrupt file must never break the host app: defaults stand.
            return merged;
        }
    }

    private static WeaponTypeProfiles FromJson(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<FileShape>(json, Json);
            if (file?.Profiles is null) return Empty;

            var store = new WeaponTypeProfiles { _missileRule = true };
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
            // A broken embedded table must never break the host app either.
            return Empty;
        }
    }

    private const string ResourceName = "EveDps.Core.Resources.weapon-type-profiles.json";

    private static string EmbeddedProfilesJson()
    {
        using var stream = typeof(WeaponTypeProfiles).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            throw new InvalidOperationException($"embedded resource {ResourceName} missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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
