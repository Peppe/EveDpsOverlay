using System.Text;
using EveDps.Core;

namespace EveDps.Core.Tests;

public class AttackerAttributionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 20, 0, 0, TimeSpan.Zero);
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static string[] LoadFixture(string name) =>
        File.ReadAllLines(Path.Combine(FixturesDir, name), new UTF8Encoding(false, false));

    private static CombatEvent In(int damage, double secondsAfterT0, string attacker) =>
        new(T0.AddSeconds(secondsAfterT0), CombatDirection.Incoming, damage, attacker, null, "Hits", IsMiss: false);

    private static CombatEvent Out(int damage, double secondsAfterT0, string target = "Rat") =>
        new(T0.AddSeconds(secondsAfterT0), CombatDirection.Outgoing, damage, target, "Gun", "Hits", IsMiss: false);

    private static CombatEvent InMiss(double secondsAfterT0, string attacker) =>
        new(T0.AddSeconds(secondsAfterT0), CombatDirection.Incoming, 0, attacker, null, null, IsMiss: true);

    private static DpsWindow WindowOf(DpsSnapshot snapshot, double seconds) =>
        Assert.Single(snapshot.Windows, w => w.Seconds == seconds);

    // ── core math ──────────────────────────────────────────────────────

    [Fact]
    public void Per_attacker_dps_equals_damage_divided_by_window()
    {
        // Synthetic 3-attacker stream, pinned reference T0+10.
        var agg = new DpsAggregator(15);
        agg.Add(In(120, 1, "Alpha"));
        agg.Add(In(90, 2, "Bravo"));
        agg.Add(In(30, 5, "Charlie"));
        agg.Add(In(60, 9, "Alpha"));
        agg.Add(Out(100, 9)); // must not create attacker rows

        var snap = agg.Take(T0.AddSeconds(10));
        var w = WindowOf(snap, 15);

        Assert.Equal(new AttackerRow("Alpha", 180 / 15.0, 0), w.Attackers.Single(a => a.Name == "Alpha"));
        Assert.Equal(new AttackerRow("Bravo", 90 / 15.0, 0), w.Attackers.Single(a => a.Name == "Bravo"));
        Assert.Equal(new AttackerRow("Charlie", 30 / 15.0, 0), w.Attackers.Single(a => a.Name == "Charlie"));
        Assert.Equal(3, w.Attackers.Count); // outgoing damage is not attacker data
    }

    [Fact]
    public void Rows_decay_out_when_events_leave_the_window()
    {
        var agg = new DpsAggregator(5, 15);
        agg.Add(In(100, 0, "Old Rat"));   // inside 15 s at T0+10, outside 5 s
        agg.Add(In(50, 8, "New Rat"));

        var snap = agg.Take(T0.AddSeconds(10));
        Assert.Null(WindowOf(snap, 5).Attackers.FirstOrDefault(a => a.Name == "Old Rat"));
        Assert.Equal(50 / 5.0, WindowOf(snap, 5).Attackers.Single(a => a.Name == "New Rat").IncomingDps, 6);
        Assert.Equal(2, WindowOf(snap, 15).Attackers.Count);

        var later = agg.Take(T0.AddSeconds(60)); // everything decayed everywhere
        Assert.All(later.Windows, w => Assert.Empty(w.Attackers));
    }

    [Fact]
    public void Miss_only_attacker_gets_a_zero_dps_row_with_miss_count()
    {
        var agg = new DpsAggregator(15);
        agg.Add(InMiss(1, "Whiffing Rat"));
        agg.Add(In(30, 2, "Damaging Rat"));

        var snap = agg.Take(T0.AddSeconds(10));
        var w = WindowOf(snap, 15);

        var missOnly = w.Attackers.Single(a => a.Name == "Whiffing Rat");
        Assert.Equal(0, missOnly.IncomingDps);
        Assert.Equal(1, missOnly.Misses);
        Assert.True(missOnly.IsMissOnly);
        Assert.False(w.Attackers.Single(a => a.Name == "Damaging Rat").IsMissOnly);
    }

    [Fact]
    public void Sum_of_rows_equals_window_incoming_dps()
    {
        var agg = new DpsAggregator(5, 15, 60);
        agg.Add(In(40, 1, "A"));
        agg.Add(In(10, 2, "B"));
        agg.Add(In(25, 3, "A"));
        agg.Add(In(5, 4, "C"));
        agg.Add(InMiss(4, "D")); // misses contribute no damage to any row

        var snap = agg.Take(T0.AddSeconds(5));
        Assert.All(snap.Windows, w =>
            Assert.Equal(w.IncomingDps, w.Attackers.Sum(a => a.IncomingDps), 6));
    }

    [Fact]
    public void Sum_invariant_holds_over_real_fixtures()
    {
        foreach (var fixture in new[] { "session-full-combat.txt", "session-20261107-from-1942.txt" })
        {
            var agg = new DpsAggregator(DpsAggregator.DefaultWindows);
            DateTimeOffset last = default;
            foreach (var line in LoadFixture(fixture))
            {
                var ev = LogParser.ParseCombat(line);
                if (ev is null) continue;
                agg.Add(ev);
                if (ev.Time > last) last = ev.Time;
            }

            var snap = agg.Take(last);
            Assert.All(snap.Windows, w =>
            {
                // No row may carry damage that isn't in the window total.
                Assert.Equal(w.IncomingDps, w.Attackers.Sum(a => a.IncomingDps), 6);
            });
        }
    }

    [Fact]
    public void Rows_sort_descending_with_name_tiebreak()
    {
        var agg = new DpsAggregator(15);
        agg.Add(In(10, 1, "Zulu"));
        agg.Add(In(30, 2, "Yankee"));
        agg.Add(In(30, 3, "Xray"));   // equal dps with Yankee → name decides
        agg.Add(In(50, 4, "Alpha"));

        var snap = agg.Take(T0.AddSeconds(10));
        var names = WindowOf(snap, 15).Attackers.Select(a => a.Name).ToArray();

        Assert.Equal(["Alpha", "Xray", "Yankee", "Zulu"], names);
    }

    [Fact]
    public void Multi_rat_fixture_sessions_top_rows()
    {
        // Real multi-rat capture (18 attackers): top of the 60 s window at
        // the session's last combat moment, as the console block prints it.
        var agg = new DpsAggregator(DpsAggregator.DefaultWindows);
        DateTimeOffset last = default;
        foreach (var line in LoadFixture("session-20261107-from-1942.txt"))
        {
            var ev = LogParser.ParseCombat(line);
            if (ev is null) continue;
            agg.Add(ev);
            if (ev.Time > last) last = ev.Time;
        }

        var w60 = agg.Take(last).Windows.Single(w => w.Seconds == 60);
        // Real multi-rat capture (18 attackers session-wide): at the last
        // combat moment exactly two of them were still shooting inside the
        // final 60 s (verified 2026-11-08), and their miss-only/bookkeeping
        // rows appear too — ranking against a real file, not synthetics.
        Assert.Equal(["Corelatis Platoon Leader", "Corelatis Wing Leader"],
            w60.Attackers.Select(a => a.Name).ToArray());
        Assert.Equal(new[] { 3, 2 }, w60.Attackers.Select(a => a.Misses).ToArray());
        for (var i = 1; i < w60.Attackers.Count; i++)
            Assert.True(w60.Attackers[i - 1].IncomingDps >= w60.Attackers[i].IncomingDps,
                "rows must be sorted descending by dps");
    }
}
