using EveDps.Core;

namespace EveDps.Core.Tests;

public class DpsAggregatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static CombatEvent Out(int damage, double secondsAfterT0, string target = "Rat") =>
        new(T0.AddSeconds(secondsAfterT0), CombatDirection.Outgoing, damage, target, "Gun", "Hits", IsMiss: false);

    private static CombatEvent In(int damage, double secondsAfterT0, string attacker = "Rat") =>
        new(T0.AddSeconds(secondsAfterT0), CombatDirection.Incoming, damage, attacker, null, "Hits", IsMiss: false);

    private static CombatEvent Miss(CombatDirection direction, double secondsAfterT0) =>
        new(T0.AddSeconds(secondsAfterT0), direction, 0, "Rat", null, null, IsMiss: true);

    private static DpsWindow WindowOf(DpsSnapshot snapshot, double seconds) =>
        Assert.Single(snapshot.Windows, w => w.Seconds == seconds);

    // ── window math ────────────────────────────────────────────────────

    [Fact]
    public void Window_dps_equals_sum_divided_by_window_length()
    {
        // 300 outgoing damage landed across the 10 s before T0+10,
        // 60 incoming damage in the same span.
        var agg = new DpsAggregator(5, 15, 60);
        for (var i = 0; i < 10; i++)
            agg.Add(Out(30, i));
        agg.Add(In(60, 9));

        var snap = agg.Take(T0.AddSeconds(10));

        var w15 = WindowOf(snap, 15);
        Assert.Equal(300 / 15.0, w15.OutgoingDps, 6);
        Assert.Equal(60 / 15.0, w15.IncomingDps, 6);
        var w60 = WindowOf(snap, 60);
        Assert.Equal(300 / 60.0, w60.OutgoingDps, 6);
        Assert.Equal(60 / 60.0, w60.IncomingDps, 6);
        // The 5 s window only saw the last 5 events (t = 5..9): 150 out / 60 in.
        var w5 = WindowOf(snap, 5);
        Assert.Equal(150 / 5.0, w5.OutgoingDps, 6);
        Assert.Equal(60 / 5.0, w5.IncomingDps, 6);
        Assert.Equal(5, w5.Seconds);
    }

    [Fact]
    public void Old_events_drop_out_without_re_adds()
    {
        var agg = new DpsAggregator(5, 15, 60);
        agg.Add(Out(100, 0)); // outside every window by T0+10
        for (var i = 5; i < 10; i++)
            agg.Add(Out(10, i));

        var snap = agg.Take(T0.AddSeconds(10));

        Assert.Equal(50 / 5.0, WindowOf(snap, 5).OutgoingDps, 6);
        Assert.Equal(150 / 60.0, WindowOf(snap, 60).OutgoingDps, 6); // 100@t0 already inside the 60s window
        Assert.Equal(150, snap.Session.TotalOutgoing);   // totals survive windows
        Assert.Equal(6, snap.Session.Events);
    }

    [Fact]
    public void Dead_window_shows_zero_even_when_partially_empty()
    {
        var agg = new DpsAggregator(5, 15, 60);
        agg.Add(Out(100, 0));
        var snap = agg.Take(T0.AddSeconds(120)); // long after the event

        Assert.All(snap.Windows, w =>
        {
            Assert.Equal(0, w.OutgoingDps);
            Assert.Equal(0, w.IncomingDps);
            Assert.Equal(0, w.Shots);
        });
        Assert.Equal(100, snap.Session.TotalOutgoing); // totals never decay
    }

    [Fact]
    public void Misses_count_as_shots_but_never_damage()
    {
        var agg = new DpsAggregator(5, 15, 60);
        agg.Add(Miss(CombatDirection.Outgoing, 1));
        agg.Add(Miss(CombatDirection.Incoming, 2));
        agg.Add(Out(50, 3));
        var snap = agg.Take(T0.AddSeconds(5));

        var w15 = WindowOf(snap, 15);
        Assert.Equal(3, w15.Shots);              // 2 misses + 1 hit
        Assert.Equal(50 / 15.0, w15.OutgoingDps, 6);
        Assert.Equal(0, w15.IncomingDps);
        Assert.Equal(2, snap.Session.Misses);
        Assert.Equal(3, snap.Session.Events);
    }

    // ── session totals ─────────────────────────────────────────────────

    [Fact]
    public void Session_totals_are_monotonic_across_windows()
    {
        var agg = new DpsAggregator(5, 15, 60);
        agg.Add(Out(10, 0));
        agg.Add(In(5, 1));
        var s1 = agg.Take(T0.AddSeconds(2)).Session;
        agg.Add(Out(20, 3));
        var s2 = agg.Take(T0.AddSeconds(4)).Session;

        Assert.Equal(10, s1.TotalOutgoing);
        Assert.Equal(30, s2.TotalOutgoing);
        Assert.Equal(5, s2.TotalIncoming);
        Assert.Equal(3, s2.Events);
    }

    // ── O(1)-per-tick: bounded queue under load ────────────────────────

    [Fact]
    public void Long_synthetic_session_keeps_the_recent_queue_bounded()
    {
        var agg = new DpsAggregator(5, 15, 60);
        for (var i = 0; i < 100_000; i++)       // ~7 days of 1 event / 6 s
            agg.Add(Out(1, i * 6.0));
        var snap = agg.Take(T0.AddSeconds(100_000 * 6.0));

        // Session totals still cover all 100k; windows hold only recent hits.
        Assert.Equal(100_000, snap.Session.Events);
        Assert.Equal(100_000, snap.Session.TotalOutgoing);
        Assert.All(snap.Windows, w => Assert.InRange(w.Shots, 0, 20));
        Assert.Equal(10 / 60.0, WindowOf(snap, 60).OutgoingDps, 6);
    }

    // ── fixture verification ───────────────────────────────────────────

    [Fact]
    public void Captured_fixture_windows_match_independent_computation()
    {
        var lines = File.ReadAllLines(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "session-full-combat.txt"),
            new System.Text.UTF8Encoding(false, false));

        var events = lines
            .Select(LogParser.ParseCombat)
            .OfType<CombatEvent>()
            .OrderBy(e => e.Time)
            .ToList();
        Assert.NotEmpty(events);
        var last = events[^1].Time;

        var agg = new DpsAggregator();
        foreach (var ev in events)
            agg.Add(ev);
        var snap = agg.Take(last);

        foreach (var window in snap.Windows)
        {
            var from = last.AddSeconds(-window.Seconds);
            var inSpan = events.Where(e => e.Time >= from).ToList(); // shots include misses
            var outSum = inSpan
                .Where(e => !e.IsMiss && e.Direction == CombatDirection.Outgoing)
                .Sum(e => (double)e.Damage);
            var inSum = inSpan
                .Where(e => !e.IsMiss && e.Direction == CombatDirection.Incoming)
                .Sum(e => (double)e.Damage);

            Assert.Equal(inSpan.Count, window.Shots);
            Assert.Equal(outSum / window.Seconds, window.OutgoingDps, 6);
            Assert.Equal(inSum / window.Seconds, window.IncomingDps, 6);
        }

        Assert.Equal(
            events.Where(e => !e.IsMiss && e.Direction == CombatDirection.Outgoing)
                  .Sum(e => (long)e.Damage),
            snap.Session.TotalOutgoing);
        Assert.Equal(
            events.Where(e => !e.IsMiss && e.Direction == CombatDirection.Incoming)
                  .Sum(e => (long)e.Damage),
            snap.Session.TotalIncoming);
        Assert.Equal(events.Count(e => e.IsMiss), snap.Session.Misses);
        Assert.Equal(events.Count, snap.Session.Events);
    }
}
