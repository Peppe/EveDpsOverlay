using EveDps.Core;

namespace EveDps.Core.Tests;

/// <summary>
/// The built-in weapon table embedded in the assembly (2026-11-16 amendment,
/// phase 4): the main source so users never need to configure anything, with
/// the appdata file as a per-weapon override.
/// </summary>
public class BuiltinTypeProfilesTests
{
    private static WeaponTypeProfiles Defaults() => WeaponTypeProfiles.LoadDefaults();

    /// <summary>Asserts the single dominant type of a weapon's profile.</summary>
    private static void Dominant(WeaponTypeProfiles profiles, string weapon, string type)
    {
        var w = profiles.GetWeights(weapon);
        Assert.NotNull(w);
        Assert.Equal(type, DamageTypes.Attributed[Array.IndexOf(w!, w!.Max())]);
    }

    // ── drones by race (pure damage, per EVE's attribute data) ─────────

    [Theory]
    [InlineData("Hobgoblin I"), InlineData("Hobgoblin II"),
     InlineData("Hammerhead I"), InlineData("Hammerhead II"),
     InlineData("Ogre I"), InlineData("Ogre II")]
    public void Gallente_drones_are_thermal(string weapon) =>
        Dominant(Defaults(), weapon, DamageTypes.Thermal);

    [Theory]
    [InlineData("Hornet I"), InlineData("Vespa I"), InlineData("Vespa II"), InlineData("Wasp I")]
    public void Caldari_drones_are_kinetic(string weapon) =>
        Dominant(Defaults(), weapon, DamageTypes.Kinetic);

    [Theory]
    [InlineData("Acolyte I"), InlineData("Infiltrator I"), InlineData("Praetor II")]
    public void Amarr_drones_are_em(string weapon) =>
        Dominant(Defaults(), weapon, DamageTypes.Em);

    [Theory]
    [InlineData("Warrior I"), InlineData("Valkyrie I"), InlineData("Berserker II")]
    public void Minmatar_drones_are_explosive(string weapon) =>
        Dominant(Defaults(), weapon, DamageTypes.Explosive);

    [Theory]
    [InlineData("Garde I", DamageTypes.Thermal), InlineData("Warden I", DamageTypes.Kinetic),
     InlineData("Curator I", DamageTypes.Em), InlineData("Bouncer II", DamageTypes.Explosive)]
    public void Sentries_follow_their_race(string weapon, string type) =>
        Dominant(Defaults(), weapon, type);

    [Fact]
    public void Gecko_is_omni()
    {
        var gecko = Defaults().GetWeights("Gecko")!;
        Assert.All(gecko, w => Assert.Equal(0.25, w, 6));
    }

    [Fact]
    public void Integrated_variants_mix_primary_and_secondary()
    {
        var hobo = Defaults().GetWeights("Integrated Hobgoblin")!;
        Assert.Equal(0.5, hobo[1], 6); // Thermal
        Assert.Equal(0.5, hobo[2], 6); // Kinetic
    }

    // ── missile element rule (ammo name always carries its element) ────

    [Theory]
    [InlineData("Inferno Light Missile", DamageTypes.Thermal),
     InlineData("Mjolnir Precision Light Missile", DamageTypes.Em),
     InlineData("Scourge Heavy Missile", DamageTypes.Kinetic),
     InlineData("Nova Fury Heavy Missile", DamageTypes.Explosive),
     InlineData("Inferno Rage Torpedo", DamageTypes.Thermal),
     InlineData("scourge heavy missile", DamageTypes.Kinetic)]
    public void Missile_ammo_resolves_by_element(string ammo, string type) =>
        Dominant(Defaults(), ammo, type);

    [Fact]
    public void HasProfile_follows_the_missile_rule_too()
    {
        var defaults = Defaults();
        Assert.True(defaults.HasProfile("Nova Torpedo"));
        Assert.False(defaults.HasProfile("Railgun X"));
    }

    // ── file override semantics ────────────────────────────────────────

    [Fact]
    public void File_entry_overrides_the_built_in()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eveDps-builtin-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "profiles": { "Hobgoblin I": { "Kinetic": 1 } } }""");
            Dominant(WeaponTypeProfiles.Load(path), "Hobgoblin I", DamageTypes.Kinetic);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Empty_stays_fully_inert()
    {
        Assert.Null(WeaponTypeProfiles.Empty.GetWeights("Hobgoblin I"));
        Assert.Null(WeaponTypeProfiles.Empty.GetWeights("Scourge Heavy Missile"));
        Assert.Empty(WeaponTypeProfiles.Empty.Weapons);
    }
}
