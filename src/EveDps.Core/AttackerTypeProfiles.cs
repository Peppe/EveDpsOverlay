using System.Text.Json;

namespace EveDps.Core;

/// <summary>
/// User-curated attacker → damage-type weight table (phase 10). NPC rat
/// damage is mostly weaponless in the gamelog (turret hits have no weapon
/// name), so the second attribution tier infers types from WHO is shooting.
/// Same INFERRED-data caveat and same inert-on-missing-broken rules as the
/// weapon table (phase 4). Pointed at
/// %APPDATA%\EveDpsOverlay\attacker-profiles.json by the host apps.
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

    public static AttackerTypeProfiles Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<FileShape>(json, Json);
            if (file?.Attackers is null || file.Attackers.Count == 0)
                return Empty;

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
            return store._exact.Count + store._wildcards.Count > 0 ? store : Empty;
        }
        catch (Exception)
        {
            // Missing/corrupt file must never break the host app: all Unknown.
            return Empty;
        }
    }
}
