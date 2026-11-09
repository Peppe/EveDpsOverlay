using EveDps.Core;

namespace EveDps.Core.Tests;

public class AttackerTypeProfilesTests
{
    private static AttackerTypeProfiles LoadJson(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"eveDps-attackerProfiles-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json.ReplaceLineEndings());
            return AttackerTypeProfiles.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------- profile loading (mirrors the weapon table's inert rules) ----------

    [Fact]
    public void Missing_file_is_inert()
    {
        var profiles = AttackerTypeProfiles.Load("no-such-directory/no-such-file.json");
        Assert.Empty(profiles.Attackers);
        Assert.Null(profiles.GetWeights("Coreli Guard"));
    }

    [Fact]
    public void Corrupt_file_is_inert()
    {
        var profiles = LoadJson("{ not valid json");
        Assert.Empty(profiles.Attackers);
    }

    [Fact]
    public void Empty_table_is_inert()
    {
        var profiles = LoadJson("""{ "attackers": {} }""");
        Assert.Empty(profiles.Attackers);
    }

    [Fact]
    public void Zero_weight_row_is_unusable()
    {
        var profiles = LoadJson("""{ "attackers": { "Coreli Guard": { "EM": 0 } } }""");
        Assert.Empty(profiles.Attackers);
    }

    [Fact]
    public void Weights_normalize_to_sum_of_one()
    {
        var profiles = LoadJson("""{ "attackers": { "Coreli Guard": { "Explosive": 2, "Kinetic": 1 } } }""");
        var weights = profiles.GetWeights("Coreli Guard")!;
        Assert.Equal(2 / 3.0, weights[3], 6);
        Assert.Equal(1 / 3.0, weights[2], 6);
        Assert.Equal(1.0, weights.Sum(), 6);
    }

    [Fact]
    public void Root_key_is_case_insensitive()
    {
        var profiles = LoadJson("""{ "Attackers": { "Coreli Guard": { "EM": 1 } } }""");
        Assert.NotNull(profiles.GetWeights("Coreli Guard"));
    }

    // ---------- exact-name matching ----------

    [Fact]
    public void Exact_name_matches_case_insensitively_and_trimmed()
    {
        var profiles = LoadJson("""{ "attackers": { "Coreli Guard": { "EM": 1 } } }""");
        Assert.NotNull(profiles.GetWeights("  coreli guard  "));
        Assert.True(profiles.HasProfile("CORELI GUARD"));
    }

    [Fact]
    public void Null_or_empty_name_has_no_profile()
    {
        var profiles = LoadJson("""{ "attackers": { "Coreli Guard": { "EM": 1 } } }""");
        Assert.Null(profiles.GetWeights(""));
        Assert.Null(profiles.GetWeights(null!));
        Assert.False(profiles.HasProfile(""));
    }

    // ---------- prefix wildcards ----------

    [Fact]
    public void Wildcard_matches_any_prefix_case_insensitively()
    {
        var profiles = LoadJson("""{ "attackers": { "Coreli*": { "Kinetic": 1 } } }""");
        Assert.NotNull(profiles.GetWeights("Coreli Guard"));
        Assert.NotNull(profiles.GetWeights("CORELI INITIATE"));
        Assert.Null(profiles.GetWeights("Pithi Arrogator"));
    }

    [Fact]
    public void Longest_pattern_wins_over_shorter()
    {
        // Coreli Guard* (12 chars) must beat Coreli* (6 chars) even though
        // the shorter one sorts first alphabetically.
        var profiles = LoadJson("""
            { "attackers": {
                "Coreli*": { "Kinetic": 1 },
                "Coreli Guard*": { "EM": 1 }
            } }
            """);
        Assert.Equal(DamageTypes.Em, TypeIndex(profiles.GetWeights("Coreli Guard Scout")));
        Assert.Equal(DamageTypes.Kinetic, TypeIndex(profiles.GetWeights("Coreli Initiate")));
    }

    [Fact]
    public void Exact_name_beats_any_wildcard()
    {
        var profiles = LoadJson("""
            { "attackers": {
                "Coreli*": { "Kinetic": 1 },
                "Coreli Guard": { "Thermal": 1 }
            } }
            """);
        Assert.Equal(DamageTypes.Thermal, TypeIndex(profiles.GetWeights("Coreli Guard")));
        // Same family, non-exact name still takes the wildcard path.
        Assert.Equal(DamageTypes.Kinetic, TypeIndex(profiles.GetWeights("Coreli Guard Scout")));
    }

    [Fact]
    public void Equal_length_wildcard_tie_breaks_alphabetically()
    {
        // Both patterns are 7 chars ("Corp-*" style clash); alphabetical
        // pick must be deterministic: '"Coreli A*"' < '"Coreli B*"'.
        var profiles = LoadJson("""
            { "attackers": {
                "Coreli B*": { "Thermal": 1 },
                "Coreli A*": { "EM": 1 }
            } }
            """);
        Assert.Equal(DamageTypes.Em, TypeIndex(profiles.GetWeights("Coreli AB vagabond")));
    }

    [Fact]
    public void Bare_star_is_a_catch_all_wildcard()
    {
        var profiles = LoadJson("""{ "attackers": { "*": { "Kinetic": 1 } } }""");
        Assert.NotNull(profiles.GetWeights("Anything At All"));
        // Any real prefix wildcard is more specific than the catch-all.
        var specific = LoadJson("""
            { "attackers": {
                "*": { "Kinetic": 1 },
                "Coreli*": { "EM": 1 }
            } }
            """);
        Assert.Equal(DamageTypes.Em, TypeIndex(specific.GetWeights("Coreli Guard")));
    }

    private static string TypeIndex(double[]? weights)
    {
        Assert.NotNull(weights);
        Assert.Equal(1.0, weights!.Sum(), 6);
        return DamageTypes.Attributed[Array.FindIndex(weights, w => w > 0.5)];
    }
}
