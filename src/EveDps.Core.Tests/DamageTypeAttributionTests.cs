using EveDps.Core;

namespace EveDps.Core.Tests;

public class DamageTypeAttributionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private const string Railgun = "Civilian Gatling Railgun";
    private const string Blaster = "Frost Mini Blaster";

    // Railgun: 34 % EM / 66 % kinetic, exactly EVE's hybrid profile for it.
    private static WeaponTypeProfiles Profiles() => LoadJson("""
        { "profiles": {
            "Civilian Gatling Railgun": { "EM": 0.34, "Kinetic": 0.66 },
            "Frost Mini Blaster": { "Thermal": 1 }
        } }
        """);

    private static WeaponTypeProfiles LoadJson(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"eveDps-profiles-{Guid.NewGuid():N}.json");
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

    private static CombatEvent Out(int damage, double secondsAfterT0, string weapon, string target = "Rat") =>
        new(T0.AddSeconds(secondsAfterT0), CombatDirection.Outgoing, damage, target, weapon, "Hits", IsMiss: false);

    private static CombatEvent In(int damage, double secondsAfterT0) =>
        new(T0.AddSeconds(secondsAfterT0), CombatDirection.Incoming, damage, "Rat", null, "Hits", IsMiss: false);

    private static DpsType TypeOf(DpsWindow window, string name) =>
        Assert.Single(window.OutgoingTypes, t => t.Name == name);

    private static DpsWindow WindowOf(DpsSnapshot snapshot, double seconds) =>
        Assert.Single(snapshot.Windows, w => w.Seconds == seconds);

    // ── profile loading ────────────────────────────────────────────────

    [Fact]
    public void Missing_file_is_inert()
    {
        var profiles = WeaponTypeProfiles.Load("no-such-directory/no-such-file.json");
        Assert.Empty(profiles.Weapons);
    }

    [Fact]
    public void Corrupt_file_is_inert()
    {
        var profiles = LoadJson("{ not valid json");
        Assert.Empty(profiles.Weapons);
    }

    [Fact]
    public void Weights_normalize_to_sum_of_one()
    {
        var profiles = LoadJson("""{ "profiles": { "Railgun X": { "EM": 2, "Kinetic": 1 } } }""");
        var weights = profiles.GetWeights("Railgun X")!;
        Assert.Equal(2 / 3.0, weights[0], 6);
        Assert.Equal(0.0, weights[1], 6);
        Assert.Equal(1 / 3.0, weights[2], 6);
        Assert.Equal(1.0, weights.Sum(), 6);
    }

    [Fact]
    public void Distinct_weapons_keep_their_own_profiles()
    {
        var blaster = Profiles().GetWeights(Blaster)!;
        Assert.Equal(1.0, blaster.Sum(), 6);
        Assert.Equal(1.0, blaster[1], 6); // the Thermal slot carries everything
    }

    [Fact]
    public void All_zero_weight_rows_are_rejected_as_unmapped()
    {
        var profiles = LoadJson("""{ "profiles": { "Broken Weapon": { "EM": -5 } } }""");
        Assert.DoesNotContain("Broken Weapon", profiles.Weapons);
        Assert.Null(profiles.GetWeights("Broken Weapon"));
    }

    [Fact]
    public void Lookup_is_case_insensitive()
    {
        Assert.NotNull(Profiles().GetWeights("CIVILIAN gatling railgun"));
    }

    // ── attribution math ───────────────────────────────────────────────

    [Fact]
    public void Mapped_weapon_damage_splits_by_profile()
    {
        var agg = new DpsAggregator(Profiles());
        agg.Add(Out(100, 0, Railgun));
        var snap = agg.Take(T0.AddSeconds(5));

        var w15 = WindowOf(snap, 15);
        Assert.Equal(100 * 0.34 / 15.0, TypeOf(w15, DamageTypes.Em).Dps, 6);   // 34 dmg → 34/15 dps
        Assert.Equal(100 * 0.66 / 15.0, TypeOf(w15, DamageTypes.Kinetic).Dps, 6);
        Assert.Equal(0, TypeOf(w15, DamageTypes.Thermal).Dps, 6);
        Assert.Equal(0, TypeOf(w15, DamageTypes.Explosive).Dps, 6);
        Assert.Equal(0, TypeOf(w15, DamageTypes.Unknown).Dps, 6);
        Assert.Equal(100 / 15.0, w15.OutgoingDps, 6);                       // total unchanged
    }

    [Fact]
    public void Unmapped_weapons_land_in_unknown_and_are_reported()
    {
        var agg = new DpsAggregator(Profiles());
        agg.Add(Out(20, 0, "Mystery Cannon"));
        agg.Add(Out(30, 1, Railgun));
        agg.Add(Out(10, 2, null!)); // only diagnostic for impossible input; defensive
        var snap = agg.Take(T0.AddSeconds(5));

        var w60 = WindowOf(snap, 60);
        // Unnamed sources and unmapped weapons both funnel into Unknown:
        // 20 (Mystery Cannon) + 10 (missing weapon name) over the 60 s window.
        Assert.Equal(30 / 60.0, TypeOf(w60, DamageTypes.Unknown).Dps, 6);
        Assert.Equal(["Mystery Cannon"], snap.UnmappedWeapons); // named weapons only
        // Invariant: the five buckets sum to the window total.
        Assert.Equal(
            w60.OutgoingTypes.Sum(t => t.Dps),
            w60.OutgoingDps, 6);
    }

    [Fact]
    public void Incoming_and_miss_events_carry_no_type_damage_but_misses_count_as_shots()
    {
        var agg = new DpsAggregator(Profiles());
        agg.Add(In(50, 0));
        agg.Add(new CombatEvent(T0.AddSeconds(1), CombatDirection.Outgoing, 0, "Rat", Railgun, null, IsMiss: true));
        var snap = agg.Take(T0.AddSeconds(5));

        var w15 = WindowOf(snap, 15);
        Assert.All(w15.OutgoingTypes, t => Assert.Equal(0, t.Dps, 6));
        Assert.Equal(2, w15.Shots);   // incoming hit + outgoing miss
        Assert.Equal(50 / 15.0, w15.IncomingDps, 6);
        Assert.Empty(snap.UnmappedWeapons);
    }

    [Fact]
    public void Old_events_decay_out_of_the_type_split_too()
    {
        var agg = new DpsAggregator(Profiles());
        agg.Add(Out(100, 0, Railgun));
        var snap = agg.Take(T0.AddSeconds(120));

        Assert.All(snap.Windows, w => Assert.All(w.OutgoingTypes, t => Assert.Equal(0, t.Dps, 6)));
        Assert.Equal(100, snap.Session.TotalOutgoing); // session totals never decay
        Assert.Empty(snap.UnmappedWeapons);
    }

    [Fact]
    public void No_profiles_means_everything_is_unknown()
    {
        var agg = new DpsAggregator();
        agg.Add(Out(7, 0, Railgun));
        var snap = agg.Take(T0.AddSeconds(5));

        var w15 = WindowOf(snap, 15);
        Assert.Equal(7 / 15.0, TypeOf(w15, DamageTypes.Unknown).Dps, 6);
        Assert.Equal([Railgun], snap.UnmappedWeapons);
    }

    [Fact]
    public void Buckets_sum_to_window_total_across_many_weapons()
    {
        var agg = new DpsAggregator(Profiles());
        agg.Add(Out(34, 0, Railgun));
        agg.Add(Out(12, 1, Railgun));
        agg.Add(Out(25, 2, Blaster));
        agg.Add(Out(9, 3, null!));
        var snap = agg.Take(T0.AddSeconds(5));

        Assert.All(snap.Windows, w =>
            Assert.Equal(w.OutgoingDps, w.OutgoingTypes.Sum(t => t.Dps), 6));
    }

    // ── fixture verification ───────────────────────────────────────────

    [Fact]
    public void Captured_fixture_types_match_independent_computation()
    {
        // The fixture session uses TWO weapons: the civilian railgun and a
        // 75 mm Gatling Rail I (both rail-hybrid). Both are mapped here; the
        // independent recomputation below applies each weapon's own profile.
        var profiles = LoadJson("""
            { "profiles": {
                "Civilian Gatling Railgun": { "EM": 0.34, "Kinetic": 0.66 },
                "75mm Gatling Rail I": { "EM": 0.2, "Kinetic": 0.8 }
            } }
            """);

        var lines = File.ReadAllLines(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "session-full-combat.txt"),
            new System.Text.UTF8Encoding(false, false));

        var events = lines
            .Select(LogParser.ParseCombat)
            .OfType<CombatEvent>()
            .Where(e => e.Direction == CombatDirection.Outgoing && !e.IsMiss)
            .OrderBy(e => e.Time)
            .ToList();
        Assert.NotEmpty(events);
        var last = events[^1].Time;

        var agg = new DpsAggregator(profiles);
        foreach (var ev in events)
            agg.Add(ev);
        var snap = agg.Take(last);

        var w60 = WindowOf(snap, 60);
        // Every hit in the session came from the two mapped weapons.
        Assert.Empty(snap.UnmappedWeapons);
        Assert.Equal(0, TypeOf(w60, DamageTypes.Unknown).Dps, 6);

        // Independent recomputation: per-weapon sums, each weighted by its
        // own profile, over the last 60 s of the fixture.
        var from = last.AddSeconds(-60);
        var span = events.Where(e => e.Time >= from).ToList();
        Assert.All(span, e => Assert.NotNull(e.Weapon));
        var perWeapon = span
            .GroupBy(e => e.Weapon!)
            .ToDictionary(g => g.Key, g => (double)g.Sum(e => e.Damage));
        Assert.NotEmpty(perWeapon);
        // Sanity: everything in the span is covered by the profile table
        // (the last 60 s uses at least one of the two mapped weapons).
        Assert.All(perWeapon.Keys, k => Assert.NotNull(profiles.GetWeights(k)));

        var expectedEm = perWeapon.Sum(e => e.Value * profiles.GetWeights(e.Key)![0]) / 60.0;
        var expectedKin = perWeapon.Sum(e => e.Value * profiles.GetWeights(e.Key)![2]) / 60.0;
        Assert.Equal(expectedEm, TypeOf(w60, DamageTypes.Em).Dps, 6);
        Assert.Equal(expectedKin, TypeOf(w60, DamageTypes.Kinetic).Dps, 6);
        Assert.Equal(perWeapon.Values.Sum() / 60.0, w60.OutgoingDps, 6);
    }
}
