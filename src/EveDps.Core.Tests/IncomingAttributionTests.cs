using System.Text;
using EveDps.Core;

namespace EveDps.Core.Tests;

/// <summary>
/// Phase 10: incoming damage type attribution. Two tiers — the weapon table
/// for incoming lines that carry a weapon (missiles), the new attacker
/// table for weaponless turret hits; else Unknown. Fixtures stay untouched
/// and prove both tiers on real logs.
/// </summary>
public class IncomingAttributionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 20, 0, 0, TimeSpan.Zero);
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static string[] LoadFixture(string name) =>
        File.ReadAllLines(Path.Combine(FixturesDir, name), new UTF8Encoding(false, false));

    private static CombatEvent In(int damage, double secondsAfterT0, string attacker, string? weapon = null) =>
        new(T0.AddSeconds(secondsAfterT0), CombatDirection.Incoming, damage, attacker, weapon, "Hits", IsMiss: false);

    private static CombatEvent InMiss(double secondsAfterT0, string attacker) =>
        new(T0.AddSeconds(secondsAfterT0), CombatDirection.Incoming, 0, attacker, null, null, IsMiss: true);

    private static AttackerTypeProfiles AttackerProfiles(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"eveDps-attr-{Guid.NewGuid():N}.json");
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

    private static WeaponTypeProfiles WeaponProfiles(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"eveDps-attrW-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json.ReplaceLineEndings());
            return WeaponTypeProfiles.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static DpsWindow WindowOf(DpsSnapshot snapshot, double seconds) =>
        Assert.Single(snapshot.Windows, w => w.Seconds == seconds);

    private static DpsType TypeOf(DpsWindow window, string name) =>
        Assert.Single(window.IncomingTypes, t => t.Name == name);

    private static void AssertSumInvariant(DpsSnapshot snapshot)
    {
        foreach (var window in snapshot.Windows)
            Assert.Equal(window.IncomingDps, window.IncomingTypes.Sum(t => t.Dps), 6);
    }

    // ---------- tier priority ----------

    [Fact]
    public void Known_weapon_tier_claims_incoming_damage()
    {
        var weapons = WeaponProfiles("""{ "profiles": { "Scourge Heavy Missile": { "EM": 0.5, "Explosive": 0.5 } } }""");
        var attackers = AttackerProfiles("""{ "attackers": { "Mercenary Commander": { "Thermal": 1 } } }""");
        var agg = new DpsAggregator([60], weapons, attackers);
        agg.Add(In(100, 1, "Mercenary Commander", "Scourge Heavy Missile"));

        var snap = agg.Take(T0.AddSeconds(5));
        var w = WindowOf(snap, 60);

        Assert.Equal(50 / 60.0, TypeOf(w, DamageTypes.Em).Dps, 6);
        Assert.Equal(50 / 60.0, TypeOf(w, DamageTypes.Explosive).Dps, 6);
        Assert.Equal(0, TypeOf(w, DamageTypes.Thermal).Dps, 6); // attacker tier is NOT mixed in
        Assert.Equal(100 / 60.0, w.IncomingDps, 6);
        Assert.Empty(snap.UnmappedWeapons);
        Assert.Empty(snap.UnmappedAttackers);
    }

    [Fact]
    public void Unknown_weapon_falls_through_to_attacker_tier()
    {
        // Weapon name present but unknown; the attacker is profiled → the
        // attacker tier claims the whole event and the weapon is not
        // flagged (a tier DID claim it — flagging is for final Unknown).
        var weapons = WeaponProfiles("""{ "profiles": { "Civilian Gatling Railgun": { "EM": 1 } } }""");
        var attackers = AttackerProfiles("""{ "attackers": { "Mercenary Commander": { "Thermal": 1 } } }""");
        var agg = new DpsAggregator([60], weapons, attackers);
        agg.Add(In(120, 1, "Mercenary Commander", "Scourge Heavy Missile"));

        var snap = agg.Take(T0.AddSeconds(5));
        var w = WindowOf(snap, 60);

        Assert.Equal(120 / 60.0, TypeOf(w, DamageTypes.Thermal).Dps, 6);
        Assert.Equal(0, TypeOf(w, DamageTypes.Unknown).Dps, 6);
        // The final Unknown tier claimed the event (via the attacker), so no
        // weapon flag: UnmappedWeapons only lists weapons from events with
        // NO claiming tier at all (per the spec decision).
        Assert.Empty(snap.UnmappedWeapons);
        Assert.Empty(snap.UnmappedAttackers);
    }

    [Fact]
    public void No_tier_claims_when_both_names_are_unknown()
    {
        var agg = new DpsAggregator([60], WeaponProfiles("""{ "profiles": {} }"""), AttackerProfiles("""{ "attackers": {} }"""));
        agg.Add(In(80, 1, "Coreli Initiate", "Scourge Heavy Missile"));

        var snap = agg.Take(T0.AddSeconds(5));
        var w = WindowOf(snap, 60);

        Assert.Equal(80 / 60.0, TypeOf(w, DamageTypes.Unknown).Dps, 6);
        // Final Unknown feeds BOTH curation lists (spec decision).
        Assert.Equal(["Scourge Heavy Missile"], snap.UnmappedWeapons);
        Assert.Equal(["Coreli Initiate"], snap.UnmappedAttackers);
    }

    [Fact]
    public void Weaponless_incoming_uses_the_attacker_tier()
    {
        var attackers = AttackerProfiles("""
            { "attackers": {
                "Coreli*": { "Explosive": 0.7, "Kinetic": 0.3 },
                "Pithi*": { "Thermal": 1 }
            } }
            """);
        var agg = new DpsAggregator([60], WeaponTypeProfiles.Empty, attackers);
        agg.Add(In(100, 1, "Coreli Guard"));
        agg.Add(In(50, 2, "Pithi Arrogator"));
        agg.Add(In(60, 3, "Unmapped Rat"));

        var snap = agg.Take(T0.AddSeconds(5));
        var w = WindowOf(snap, 60);

        Assert.Equal(70 / 60.0, TypeOf(w, DamageTypes.Explosive).Dps, 6);
        Assert.Equal(30 / 60.0, TypeOf(w, DamageTypes.Kinetic).Dps, 6);
        Assert.Equal(50 / 60.0, TypeOf(w, DamageTypes.Thermal).Dps, 6);
        Assert.Equal(60 / 60.0, TypeOf(w, DamageTypes.Unknown).Dps, 6); // 100/60 - (70+30)/60
        AssertSumInvariant(snap);
        Assert.Equal(["Unmapped Rat"], snap.UnmappedAttackers);
    }

    [Fact]
    public void Two_profiled_attackers_with_different_profiles_separate()
    {
        var attackers = AttackerProfiles("""
            { "attackers": {
                "Coreli Guard": { "EM": 1 },
                "Coreli Initiate": { "Kinetic": 1 }
            } }
            """);
        var agg = new DpsAggregator(windowSeconds: [60], attackerProfiles: attackers);
        agg.Add(In(100, 1, "Coreli Guard"));
        agg.Add(In(60, 2, "Coreli Initiate"));

        var snap = agg.Take(T0.AddSeconds(5));
        var w = WindowOf(snap, 60);

        Assert.Equal(100 / 60.0, TypeOf(w, DamageTypes.Em).Dps, 6);
        Assert.Equal(60 / 60.0, TypeOf(w, DamageTypes.Kinetic).Dps, 6);
        Assert.Equal(160 / 60.0, w.IncomingDps, 6);
    }

    // ---------- non-attribution rules (phase 7 parity) ----------

    [Fact]
    public void Incoming_misses_never_attribute_nor_flag()
    {
        var agg = new DpsAggregator(60); // no profiles at all — everything inert
        agg.Add(InMiss(1, "Whiffing Rat"));
        agg.Add(In(40, 2, "Hitting Rat"));

        var snap = agg.Take(T0.AddSeconds(5));
        var w = WindowOf(snap, 60);

        Assert.Equal(40 / 60.0, w.IncomingDps, 6);
        Assert.Equal(40 / 60.0, TypeOf(w, DamageTypes.Unknown).Dps, 6);
        Assert.Equal(["Hitting Rat"], snap.UnmappedAttackers); // the whiffing rat is not curation work
        AssertSumInvariant(snap);
    }

    [Fact]
    public void Outgoing_behavior_is_unchanged_by_the_incoming_tier()
    {
        var weapons = WeaponProfiles("""{ "profiles": { "Civilian Gatling Railgun": { "EM": 0.34, "Kinetic": 0.66 } } }""");
        var attackers = AttackerProfiles("""{ "attackers": { "Rat": { "EM": 1 } } }""");
        var agg = new DpsAggregator([60], weapons, attackers);
        agg.Add(new(T0.AddSeconds(1), CombatDirection.Outgoing, 100, "Rat", "Civilian Gatling Railgun", "Hits", IsMiss: false));

        var snap = agg.Take(T0.AddSeconds(5));
        var w = WindowOf(snap, 60);

        Assert.Equal(34 / 60.0, Assert.Single(w.OutgoingTypes, t => t.Name == DamageTypes.Em).Dps, 6);
        Assert.Equal(66 / 60.0, Assert.Single(w.OutgoingTypes, t => t.Name == DamageTypes.Kinetic).Dps, 6);
        Assert.Equal(0, TypeOf(w, DamageTypes.Em).Dps, 6); // attacker profile never touches OUT
    }

    // ---------- fixtures (real log lines, untouched) ----------

    private static void SumInvariantOnFixture(string name)
    {
        var events = LoadFixture(name).Select(LogParser.ParseCombat)
            .Where(ev => ev is not null).Cast<CombatEvent>()
            .ToList();
        var agg = new DpsAggregator(60);
        foreach (var ev in events) agg.Add(ev);
        var reference = events.Max(ev => ev.Time);

        var snap = agg.Take(reference);
        AssertSumInvariant(snap);
    }

    [Fact]
    public void Multi_rat_fixture_type_sum_equals_incoming_dps() =>
        SumInvariantOnFixture("session-20261107-from-1942.txt");

    [Fact]
    public void One_attacker_fixture_type_sum_equals_incoming_dps() =>
        SumInvariantOnFixture("session-full-combat.txt");

    [Fact]
    public void Fully_unprofiled_fixture_reports_every_incoming_attacker_and_weapon_as_unmapped()
    {
        var events = LoadFixture("session-20261107-from-1942.txt").Select(LogParser.ParseCombat)
            .Where(ev => ev is not null).Cast<CombatEvent>().ToList();
        var agg = new DpsAggregator(60);
        foreach (var ev in events) agg.Add(ev);

        var snap = agg.Take(events.Max(ev => ev.Time));

        // Without any curation the two-tier model is fully inert: every
        // attacker and weapon the log names lands in the curation lists.
        var incomingAttackers = events
            .Where(ev => ev.Direction == CombatDirection.Incoming && !ev.IsMiss)
            .Select(ev => ev.OtherParty).Distinct(StringComparer.OrdinalIgnoreCase);
        Assert.All(incomingAttackers, a => Assert.Contains(a, snap.UnmappedAttackers));
        var incomingWeapons = events
            .Where(ev => ev.Direction == CombatDirection.Incoming && !ev.IsMiss && ev.Weapon is not null)
            .Select(ev => ev.Weapon!).Distinct(StringComparer.OrdinalIgnoreCase);
        Assert.All(incomingWeapons, wname => Assert.Contains(wname, snap.UnmappedWeapons));
    }

    [Fact]
    public void Profiles_applied_to_the_real_missile_burst()
    {
        // The 2026-11-07 fixture's known ground truth (measured 2026-11-09):
        // 28 incoming missile events, 995 damage, all weapon
        // "Scourge Heavy Missile" — but spread over a 201 s river, wider
        // than the 60 s window. The test cross-checks the window slice
        // against an independently computed expectation.
        var events = LoadFixture("session-20261107-from-1942.txt").Select(LogParser.ParseCombat)
            .Where(ev => ev is not null).Cast<CombatEvent>().ToList();
        var weapons = WeaponProfiles("""{ "profiles": { "Scourge Heavy Missile": { "EM": 1 } } }""");
        var attackers = AttackerProfiles("""{ "attackers": { "Mercenary Commander": { "Kinetic": 1 } } }""");
        var agg = new DpsAggregator([60], weapons, attackers);
        foreach (var ev in events) agg.Add(ev);

        // Reference at the burst's end; everything in the 60 s window is
        // missile damage (weapon tier) plus the same commander's turret hits
        // (attacker tier), nothing unclaimed.
        var burstEnd = events.Where(ev => ev.Direction == CombatDirection.Incoming && ev.Weapon is not null)
            .Max(ev => ev.Time);
        var snap = agg.Take(burstEnd);
        var w = WindowOf(snap, 60);

        Assert.Equal(995, events
            .Where(ev => ev.Direction == CombatDirection.Incoming && !ev.IsMiss && ev.Weapon is not null)
            .Sum(ev => ev.Damage));
        var horizon = burstEnd.AddSeconds(-60);
        var inWindow = events
            .Where(ev => ev.Time >= horizon && ev.Time <= burstEnd)
            .Where(ev => ev.Direction == CombatDirection.Incoming && !ev.IsMiss)
            .ToList();
        var missileDmg = inWindow.Where(ev => ev.Weapon is not null).Sum(ev => ev.Damage);
        var totalDmg = inWindow.Sum(ev => ev.Damage);

        Assert.Equal(totalDmg / 60.0, w.IncomingDps, 3);
        Assert.Equal(missileDmg / 60.0, TypeOf(w, DamageTypes.Em).Dps, 3);         // weapon tier
        Assert.Equal((totalDmg - missileDmg) / 60.0, TypeOf(w, DamageTypes.Kinetic).Dps, 3); // attacker tier
        Assert.Equal(0, TypeOf(w, DamageTypes.Unknown).Dps, 3);
        // The missile tier is claimed → no longer unmapped. UnmappedWeapons
        // keeps listing the unmapped OUTGOING drone weapons (Vespa/Wasp/
        // Hornet) exactly as phases 4/7 did — curation work is per weapon.
        AssertDoesNotContain(snap.UnmappedWeapons, "Scourge Heavy Missile");
        AssertDoesNotContain(snap.UnmappedAttackers, "Mercenary Commander"); // both tiers claim his damage
        AssertSumInvariant(snap);
    }

    private static void AssertDoesNotContain(IReadOnlyList<string> list, string value) =>
        Assert.All(list, item => Assert.NotEqual(value, item, StringComparer.OrdinalIgnoreCase));
}
