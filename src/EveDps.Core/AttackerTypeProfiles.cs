using System.Text.Json;

namespace EveDps.Core;

/// <summary>
/// Attacker → damage-type weight table (phase 10). NPC rat damage is mostly
/// weaponless in the gamelog (turret hits have no weapon name), so the second
/// attribution tier infers types from WHO is shooting. Same INFERRED-data
/// caveat as the weapon table (phase 4). The main source is a built-in table
/// embedded in this assembly (2026-11-16 amendment: NPC factions by rat-name
/// prefix, weights from EVE University's measured faction damage mixes);
/// the optional file at %APPDATA%\EveDpsOverlay\attacker-profiles.json only
/// OVERRIDES per-key entries — a missing or broken file leaves the built-in
/// table active.
///
/// Keys support prefix wildcards: a key ending in '*' matches any name
/// starting with that prefix, case-insensitively ("Coreli*" covers the whole
/// Coreli spawn family). Matching is longest-pattern-wins; an exact
/// (non-wildcard) name always beats any wildcard; wildcard ties break
/// alphabetically for determinism.
/// </summary>
public sealed class AttackerTypeProfiles
{
    private readonly Dictionary<string, double[]> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Prefix, double[] Weights)> _wildcards = [];

    public static AttackerTypeProfiles Empty { get; } = new();

    private AttackerTypeProfiles() { }

    /// <summary>Names known to the table (useful for diagnostics/tests).</summary>
    public IReadOnlyCollection<string> Attackers =>
        [.. _exact.Keys, .. _wildcards.Select(w => w.Prefix + "*")];

    public bool HasProfile(string attacker)
    {
        if (string.IsNullOrEmpty(attacker)) return false;
        return _exact.ContainsKey(attacker.Trim())
            || _wildcards.Any(w => Matches(attacker.Trim(), w.Prefix));
    }

    /// <summary>
    /// Weight vector for an attacker in fixed order EM/Thermal/Kinetic/
    /// Explosive, normalized to sum to 1; null when nobody profiles them.
    /// Exact name beats any wildcard; wildcard is longest-prefix-wins with
    /// alphabetical tie-break. Lookup is case-insensitive.
    /// </summary>
    public double[]? GetWeights(string attacker)
    {
        if (string.IsNullOrEmpty(attacker)) return null;
        var name = attacker.Trim();

        if (_exact.TryGetValue(name, out var exact))
            return exact;

        (string Prefix, double[] Weights)? best = null;
        foreach (var wildcard in _wildcards)
        {
            if (!Matches(name, wildcard.Prefix)) continue;
            // Longer prefix wins; equal length → alphabetically first.
            if (best is null
                || wildcard.Prefix.Length > best.Value.Prefix.Length
                || (wildcard.Prefix.Length == best.Value.Prefix.Length
                    && string.Compare(wildcard.Prefix, best.Value.Prefix, StringComparison.OrdinalIgnoreCase) < 0))
                best = wildcard;
        }
        return best?.Weights;
    }

    private static bool Matches(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    // JSON shape:
    // { "attackers": { "Coreli*": { "Explosive": 0.7, "Kinetic": 0.3 } } }
    private sealed class FileShape
    {
        public Dictionary<string, Dictionary<string, double>>? Attackers { get; set; }
    }

    /// <summary>Case-insensitive so hand-edited JSON ("attackers" vs "Attackers") just works.</summary>
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Built-in defaults shipped inside the assembly (main source, 2026-11-16
    /// amendment): NPC factions keyed by rat-name prefix wildcards.
    /// </summary>
    public static AttackerTypeProfiles LoadDefaults() =>
        FromJson(EmbeddedProfilesJson()) ?? Empty;

    /// <summary>
    /// Built-in defaults as the base, with the optional file at <paramref
    /// name="path"/> overriding per-key entries (file wins). Missing, empty
    /// or corrupt file → defaults only, never a crash.
    /// </summary>
    public static AttackerTypeProfiles Load(string path)
    {
        var merged = LoadDefaults();
        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<FileShape>(json, Json);
            if (file?.Attackers is null) return merged;

            foreach (var (attacker, weights) in file.Attackers)
            {
                var normalized = WeaponTypeProfiles.Normalize(weights);
                if (normalized is null) continue;

                var key = attacker.Trim();
                if (key.EndsWith('*') && key.Length > 1)
                {
                    var prefix = key[..^1];
                    merged._wildcards.RemoveAll(w => w.Prefix == prefix); // file wins per key
                    merged._wildcards.Add((prefix, normalized));
                }
                else if (key == "*")
                {
                    merged._wildcards.RemoveAll(w => w.Prefix.Length == 0);
                    merged._wildcards.Add((string.Empty, normalized)); // catch-all wildcard
                }
                else if (key.Length > 0)
                {
                    merged._exact[key] = normalized;
                }
            }
            merged._wildcards.Sort(static (a, b) => string.Compare(a.Prefix, b.Prefix, StringComparison.OrdinalIgnoreCase));
            return merged;
        }
        catch (Exception)
        {
            // Missing/corrupt file must never break the host app: defaults stand.
            return merged;
        }
    }

    private static AttackerTypeProfiles? FromJson(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<FileShape>(json, Json);
            if (file?.Attackers is null) return null;

            var store = new AttackerTypeProfiles();
            foreach (var (attacker, weights) in file.Attackers)
            {
                var normalized = WeaponTypeProfiles.Normalize(weights);
                if (normalized is null) continue;

                var key = attacker.Trim();
                if (key.EndsWith('*') && key.Length > 1)
                    store._wildcards.Add((key[..^1], normalized));
                else if (key == "*")
                    store._wildcards.Add((string.Empty, normalized)); // catch-all wildcard
                else if (key.Length > 0)
                    store._exact[key] = normalized;
            }
            store._wildcards.Sort(static (a, b) => string.Compare(a.Prefix, b.Prefix, StringComparison.OrdinalIgnoreCase));
            return store._exact.Count + store._wildcards.Count > 0 ? store : null;
        }
        catch (Exception)
        {
            // A broken embedded table must never break the host app either.
            return null;
        }
    }

    private const string ResourceName = "EveDps.Core.Resources.attacker-type-profiles.json";

    private static string EmbeddedProfilesJson()
    {
        using var stream = typeof(AttackerTypeProfiles).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            throw new InvalidOperationException($"embedded resource {ResourceName} missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
