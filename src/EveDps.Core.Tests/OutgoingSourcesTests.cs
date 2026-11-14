using System.Text;
using EveDps.Core;

namespace EveDps.Core.Tests;

/// <summary>
/// Phase 11: outgoing per-weapon source rows — the OUT panel's mirror of the
/// attacker rows, with per-row profiled type splits for the segmented bars.
/// </summary>
public class OutgoingSourcesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 20, 0, 0, TimeSpan.Zero);
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static string[] LoadFixture(string name) =>
        File.ReadAllLines(Path.Combine(FixturesDir, name), new UTF8Encoding(false, false));

    private static WeaponTypeProfiles WeaponProfiles(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"eveDps-src-{Guid.NewGuid():N}.json");
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

    private static CombatEvent Out(int damage, double secondsAfterT0, string weapon) =>
        new(T0.AddSeconds(secondsAfterT0), CombatDirection.Outgoing, damage, "Rat", weapon, "Hits", IsMiss: false);

    private static CombatEvent OutMiss(double secondsAfterT0, string weapon) =>
        new(T0.AddSeconds(secondsAfterT0), CombatDirection.Outgoing, 0, "Rat", weapon, null, IsMiss: true);

    private static DpsWindow WindowOf(DpsSnapshot snapshot, double seconds) =>
        Assert.Single(snapshot.Windows, w => w.Seconds == seconds);

    private static void AssertSumInvariant(DpsSnapshot snapshot)
    {
        foreach (var window in snapshot.Windows)
        {
            Assert.Equal(window.OutgoingDps, window.OutgoingSources.Sum(s => s.Dps), 6);
            Assert.Equal(window.IncomingDps, window.Attackers.Sum(s => s.Dps), 6);
        }
    }

    // ---------- bucketing ----------

    [Fact]
    public void Outgoing_damage_buckets_per_weapon()
    {
        var agg = new DpsAggregator(15);
        agg.Add(Out(120, 1, "Vespa I"));
        agg.Add(Out(60, 2, "Hornet I"));
        agg.Add(Out(30, 3, "Vespa I"));

        var snap = agg.Take(T0.AddSeconds(10));
        var w = WindowOf(snap, 15);

        Assert.Equal(150 / 15.0, w.OutgoingSources.Single(s => s.Name == "Vespa I").Dps, 6);
        Assert.Equal(60 / 15.0, w.OutgoingSources.Single(s => s.Name == "Hornet I").Dps, 6);
        Assert.Equal(2, w.OutgoingSources.Count);
        Assert.Equal(210 / 15.0, w.OutgoingDps, 6);
        Assert.Equal(w.OutgoingDps, w.OutgoingSources.Sum(s => s.Dps), 6);
    }

    [Fact]
    public void Outgoing_misses_count_per_weapon()
    {
        var agg = new DpsAggregator([15]);
        agg.Add(Out(100, 1, "Wasp I"));
        agg.Add(OutMiss(2, "Hornet I"));
        agg.Add(OutMiss(3, "Hornet I"));
        agg.Add(OutMiss(4, "Wasp I"));

        var snap = agg.Take(T0.AddSeconds(10));
        var w = WindowOf(snap, 15);

        var hornet = w.OutgoingSources.Single(s => s.Name == "Hornet I");
        Assert.True(hornet.IsMissOnly);          // zero dps row, dimmed in the HUD
        Assert.Equal(2, hornet.Misses);
        Assert.Equal(1, w.OutgoingSources.Single(s => s.Name == "Wasp I").Misses);
        Assert.Equal(100 / 15.0, w.OutgoingDps, 6);
    }

    // ---------- type split ----------

    [Fact]
    public void Profiled_weapon_row_carries_its_type_split()
    {
        // Railgun 34 % EM / 66 % kinetic (the phase-4 reference profile).
        var weapons = WeaponProfiles("""{ "profiles": { "75mm Gatling Rail I": { "EM": 0.34, "Kinetic": 0.66 } } }""");
        var agg = new DpsAggregator([60], weapons);
        agg.Add(Out(100, 1, "75mm Gatling Rail I"));

        var snap = agg.Take(T0.AddSeconds(5));
        var row = WindowOf(snap, 60).OutgoingSources.Single(s => s.Name == "75mm Gatling Rail I");

        Assert.Equal(34 / 60.0, row.Em, 6);
        Assert.Equal(66 / 60.0, row.Kinetic, 6);
        Assert.Equal(0, row.Thermal, 6);
        Assert.Equal(0, row.Explosive, 6);
        Assert.Equal(row.Dps, row.Em + row.Thermal + row.Kinetic + row.Explosive, 6);
    }

    [Fact]
    public void Unmapped_weapon_row_has_all_zero_split()
    {
        var agg = new DpsAggregator(60);
        agg.Add(Out(80, 1, "Mystery Cannon"));

        var snap = agg.Take(T0.AddSeconds(5));
        var row = WindowOf(snap, 60).OutgoingSources.Single(s => s.Name == "Mystery Cannon");

        Assert.Equal(80 / 60.0, row.Dps, 6);
        Assert.Equal(0, row.Em + row.Thermal + row.Kinetic + row.Explosive, 6);
    }

    [Fact]
    public void Others_merge_sums_the_type_slices()
    {
        var weapons = WeaponProfiles("""
            { "profiles": {
                "Alpha Blaster": { "Thermal": 1 },
                "Beta Blaster": { "Thermal": 1 }
            } }
            """);
        var agg = new DpsAggregator([60], weapons);
        agg.Add(Out(100, 1, "Alpha Blaster"));  // rank 1, kept by budget 2
        agg.Add(Out(80, 2, "Beta Blaster"));    // rank 2 → merged into Others
        agg.Add(Out(20, 3, "Gamma Rifle"));     // unmapped, merged into Others

        var snap = agg.Take(T0.AddSeconds(5));
        var display = SourcePanel.DisplayRows(WindowOf(snap, 60).OutgoingSources, budget: 2);

        Assert.Equal(["Alpha Blaster", "Others"], display.Select(r => r.Name));
        var others = display[^1];
        Assert.Equal(100 / 60.0, others.Dps, 6);       // (80 + 20) / 60
        Assert.Equal(80 / 60.0, others.Thermal, 6);    // Beta's profiled slice merges in
        Assert.Equal(0, others.Em, 6);                 // Gamma is unmapped: gray remainder = 20/60
        Assert.Equal(0, others.Kinetic, 6);
        Assert.Equal(0, others.Explosive, 6);
    }

    // ---------- fixtures ----------

    [Fact]
    public void Multi_rat_fixture_source_sums_equal_window_totals()
    {
        var events = LoadFixture("session-20261107-from-1942.txt").Select(LogParser.ParseCombat)
            .Where(ev => ev is not null).Cast<CombatEvent>().ToList();
        var agg = new DpsAggregator(60);
        foreach (var ev in events) agg.Add(ev);

        var snap = agg.Take(events.Max(ev => ev.Time));
        AssertSumInvariant(snap);

        // The fixture's known outgoing weapons all appear as rows somewhere
        // in the session-level distinct set of any window that contains them;
        // the 60 s tail window only has the final burst, so assert on the
        // rows that exist there instead of pinning names.
        Assert.All(snap.Windows[0].OutgoingSources, s => Assert.True(s.Dps > 0 || s.Misses > 0));
    }

    [Fact]
    public void One_attacker_fixture_source_sums_equal_window_totals()
    {
        var events = LoadFixture("session-full-combat.txt").Select(LogParser.ParseCombat)
            .Where(ev => ev is not null).Cast<CombatEvent>().ToList();
        var agg = new DpsAggregator(60);
        foreach (var ev in events) agg.Add(ev);

        var snap = agg.Take(events.Max(ev => ev.Time));
        AssertSumInvariant(snap);
    }
}
