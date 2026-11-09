using EveDps.Core;

namespace EveDps.Core.Tests;

public class ReplayFeedTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 17, 30, 0, TimeSpan.Zero);

    private static string FixtureFile(params string[] body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"eveDps-replay-{Guid.NewGuid():N}.txt");
        var header = new[]
        {
            "------------------------------------------------------------",
            "  Gamelog",
            "  Session Started: 2026.11.01 17:19:24",
            "------------------------------------------------------------",
        };
        File.WriteAllLines(path, header.Concat(body), new System.Text.UTF8Encoding(false));
        return path;
    }

    private static string CombatAt(DateTimeOffset time, int dmg) =>
        $"[ {time:yyyy.MM.dd HH:mm:ss} ] (combat) {dmg} to Test Rat - Civilian Gatling Railgun - Hits";

    private static async Task<(List<string> Lines, ReplayFeed Feed)> RunAsync(
        string path, double speed, TimeSpan? maxIdleGap = null)
    {
        var feed = new ReplayFeed(path, speed, maxIdleGap);
        var seen = new List<string>();
        var done = new TaskCompletionSource();
        feed.Line += line => seen.Add(line.Text);
        feed.Finished += () => done.TrySetResult();
        feed.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await feed.StopAsync();
        feed.Dispose();
        return (seen, feed);
    }

    [Fact]
    public async Task Emits_every_line_in_order_and_reaches_final_virtual_time()
    {
        var path = FixtureFile(
            CombatAt(T0, 10),
            CombatAt(T0.AddSeconds(2), 20),
            CombatAt(T0.AddSeconds(6), 30));
        var (lines, feed) = await RunAsync(path, speed: 10_000);

        Assert.Equal(7, lines.Count); // header block + 3 combat lines, all in order
        Assert.Equal(T0.AddSeconds(6), feed.CurrentVirtualTime);
        Assert.Equal(CombatAt(T0.AddSeconds(6), 30), lines[^1]);
        // Phase 9 race-fix gate: stamped lines processed ⇒ TimeKnown true.
        Assert.True(feed.TimeKnown);
    }

    [Fact]
    public async Task Long_idle_gaps_hold_only_the_cap()
    {
        var path = FixtureFile(
            CombatAt(T0, 5),
            // 10 minutes of log-time idling between these two shots.
            CombatAt(T0.AddMinutes(10), 15));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (lines, feed) = await RunAsync(path, speed: 1.0, maxIdleGap: TimeSpan.FromSeconds(1));
        sw.Stop();

        Assert.Equal(6, lines.Count);
        Assert.Equal(T0.AddMinutes(10), feed.CurrentVirtualTime);
        // 1 s cap ÷ speed 1 ⇒ at most ~1 s pacing; 10 full minutes would fail.
        Assert.True(sw.Elapsed <= TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Virtual_time_stays_monotonic_through_the_file()
    {
        var path = FixtureFile(
            CombatAt(T0, 1),
            CombatAt(T0.AddSeconds(3), 2),
            CombatAt(T0.AddSeconds(3), 4)); // same second: zero gap, still monotonic
        var (lines, feed) = await RunAsync(path, speed: 10_000);

        Assert.Equal(7, lines.Count); // header block (4) + 3 combat lines
        Assert.Equal(T0.AddSeconds(3), feed.CurrentVirtualTime);
    }

    [Fact]
    public async Task Empty_and_missing_files_finish_without_emitting()
    {
        var empty = FixtureFile();
        var (lines0, _) = await RunAsync(empty, speed: 100);
        Assert.Equal(4, lines0.Count); // just the header block

        var missing = Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.txt");
        var (lines1, _) = await RunAsync(missing, speed: 100);
        Assert.Empty(lines1);
    }

    [Fact]
    public async Task TimeKnown_stays_false_when_a_file_has_no_timestamped_lines()
    {
        // Header-only file: hosts keep gating aggregation (epoch clock must
        // never reach Take). The HUD shows nothing — also correct for combat.
        var path = FixtureFile();
        var (_, feed) = await RunAsync(path, speed: 100);
        Assert.False(feed.TimeKnown);
    }
}
