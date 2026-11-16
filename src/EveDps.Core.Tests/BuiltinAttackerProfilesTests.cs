using EveDps.Core;

namespace EveDps.Core.Tests;

/// <summary>
/// The built-in attacker table embedded in the assembly (2026-11-16
/// amendment, phase 10): NPC factions keyed by rat-name prefix wildcards,
/// weights from EVE University's measured faction damage mixes. The
/// appdata file is a per-key override.
/// </summary>
public class BuiltinAttackerProfilesTests
{
    private static WeaponTypeProfiles None() => WeaponTypeProfiles.Empty;
    private static AttackerTypeProfiles Defaults() => AttackerTypeProfiles.LoadDefaults();

    private static void Dominant(AttackerTypeProfiles profiles, string attacker, string type)
    {
        var w = profiles.GetWeights(attacker);
        Assert.NotNull(w);
        Assert.Equal(type, DamageTypes.Attributed[Array.IndexOf(w!, w!.Max())]);
    }

    private static double[] LoadFile(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"eveDps-attackerBuiltin-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json.ReplaceLineEndings());
            return AttackerTypeProfiles.Load(path).GetWeights("Coreli Patroller")!;
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── factions by rat-name family ────────────────────────────────────

    [Theory]
    [InlineData("Coreli Patroller"), InlineData("Corelum Chief Watchman"),
     InlineData("Corelatis Squad Leader"), InlineData("Serpentis Corpum Arch Malord")]
    public void Serpentis_family_is_thermal_dominant(string attacker) =>
        Dominant(Defaults(), attacker, DamageTypes.Thermal);

    [Theory]
    [InlineData("Pithi Arrogator"), InlineData("Pithum Ascriber"), InlineData("Guristas Misbeliever")]
    public void Guristas_family_is_kinetic_dominant(string attacker) =>
        Dominant(Defaults(), attacker, DamageTypes.Kinetic);

    [Theory]
    [InlineData("Centii Ravener"), InlineData("Centati Annihilator"), InlineData("True Sansha Porthos")]
    public void Sansha_family_is_em_dominant(string attacker) =>
        Dominant(Defaults(), attacker, DamageTypes.Em);

    [Theory]
    [InlineData("Corpus Omni-Faria"), InlineData("Dark Blood Alpha Visionary")]
    public void Blood_raiders_are_em_dominant(string attacker) =>
        Dominant(Defaults(), attacker, DamageTypes.Em);

    [Theory]
    [InlineData("Gistii Haunter"), InlineData("Arch Gistum Centurion"), InlineData("Domination Dominator")]
    public void Angels_are_explosive_dominant(string attacker) =>
        Dominant(Defaults(), attacker, DamageTypes.Explosive);

    [Fact]
    public void Mercenaries_split_kinetic_and_thermal()
    {
        var w = Defaults().GetWeights("Mercenary Commander")!;
        Assert.True(w[1] > 0.4 && w[2] > 0.4); // Thermal + Kinetic mix
    }

    [Fact]
    public void Sleepers_are_omni()
    {
        var w = Defaults().GetWeights("Sleeper Sentinel")!;
        Assert.All(w, v => Assert.Equal(0.25, v, 6));
    }

    // ── file override semantics ────────────────────────────────────────

    [Fact]
    public void More_specific_file_wildcard_beats_the_builtin_family()
    {
        var w = LoadFile("""{ "attackers": { "Coreli*": { "Kinetic": 1 } } }""");
        Assert.Equal(DamageTypes.Kinetic, DamageTypes.Attributed[Array.IndexOf(w, w.Max())]);
    }

    [Fact]
    public void Same_prefix_file_wildcard_replaces_the_builtin()
    {
        var w = LoadFile("""{ "attackers": { "Core*": { "Explosive": 1 } } }""");
        Assert.Equal(DamageTypes.Explosive, DamageTypes.Attributed[Array.IndexOf(w, w.Max())]);
    }

    [Fact]
    public void Exact_file_name_beats_builtin_wildcard()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eveDps-attackerExact-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "attackers": { "Pithi Arrogator": { "EM": 1 } } }""");
            Dominant(AttackerTypeProfiles.Load(path), "Pithi Arrogator", DamageTypes.Em);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Empty_stays_fully_inert()
    {
        Assert.Null(AttackerTypeProfiles.Empty.GetWeights("Coreli Patroller"));
        Assert.Empty(AttackerTypeProfiles.Empty.Attackers);
    }
}
