using System.Text;
using EveDps.Core;

namespace EveDps.Core.Tests;

public class LogParserTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static string[] LoadFixture(string name) =>
        File.ReadAllLines(Path.Combine(FixturesDir, name), new UTF8Encoding(false, false));

    // ── markup stripping ───────────────────────────────────────────────

    [Fact]
    public void StripMarkup_removes_color_font_and_bold_tags()
    {
        const string line =
            "<color=0xff00ffff><b>7</b> <color=0x77ffffff><font size=10>to</font> " +
            "<b><color=0xffffffff>Pithi Arrogator</b>";
        const string expected = "7 to Pithi Arrogator";
        Assert.Equal(expected, LogParser.StripMarkup(line));
    }

    // ── damage lines ───────────────────────────────────────────────────

    [Fact]
    public void Parses_outgoing_damage_with_weapon_and_hit_kind()
    {
        var ev = LogParser.ParseCombat(
            "[ 2026.11.01 17:34:46 ] (combat) <color=0xff00ffff><b>7</b> <color=0x77ffffff><font size=10>to</font> <b><color=0xffffffff>Pithi Arrogator</b><font size=10><color=0x77ffffff> - Civilian Gatling Railgun - Penetrates");
        Assert.NotNull(ev);
        Assert.Equal(CombatDirection.Outgoing, ev!.Direction);
        Assert.Equal(7, ev.Damage);
        Assert.Equal("Pithi Arrogator", ev.OtherParty);
        Assert.Equal("Civilian Gatling Railgun", ev.Weapon);
        Assert.Equal("Penetrates", ev.HitKind);
        Assert.False(ev.IsMiss);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T17:34:46+00:00"), ev.Time);
    }

    [Fact]
    public void Parses_incoming_damage_without_weapon()
    {
        var ev = LogParser.ParseCombat(
            "[ 2026.11.01 17:36:37 ] (combat) <color=0xffcc0000><b>2</b> <color=0x77ffffff><font size=10>from</font> <b><color=0xffffffff>Pithi Arrogator</b><font size=10><color=0x77ffffff> - Penetrates");
        Assert.NotNull(ev);
        Assert.Equal(CombatDirection.Incoming, ev!.Direction);
        Assert.Equal(2, ev.Damage);
        Assert.Equal("Pithi Arrogator", ev.OtherParty);
        Assert.Null(ev.Weapon);
        Assert.Equal("Penetrates", ev.HitKind);
        Assert.False(ev.IsMiss);
    }

    [Fact]
    public void Incoming_damage_with_weapon_still_binds_weapon()
    {
        // Some incoming lines do carry a weapon segment; the parser must not
        // lose it. (Spec: per-weapon stats stay outgoing-only until proven.)
        var ev = LogParser.ParseCombat(
            "[ 2026.11.01 17:00:00 ] (combat) 12 from Hostile Pilot - Hammerhead II - Hits");
        Assert.NotNull(ev);
        Assert.Equal(CombatDirection.Incoming, ev!.Direction);
        Assert.Equal(12, ev.Damage);
        Assert.Equal("Hostile Pilot", ev.OtherParty);
        Assert.Equal("Hammerhead II", ev.Weapon);
        Assert.Equal("Hits", ev.HitKind);
    }

    // ── misses ─────────────────────────────────────────────────────────

    [Fact]
    public void Parses_outgoing_miss_as_zero_damage_shot()
    {
        var ev = LogParser.ParseCombat(
            "[ 2026.11.01 17:36:29 ] (combat) Your Civilian Gatling Railgun misses Pithi Arrogator completely - Civilian Gatling Railgun");
        Assert.NotNull(ev);
        Assert.Equal(CombatDirection.Outgoing, ev!.Direction);
        Assert.Equal(0, ev.Damage);
        Assert.Equal("Pithi Arrogator", ev.OtherParty);
        Assert.Equal("Civilian Gatling Railgun", ev.Weapon);
        Assert.True(ev.IsMiss);
    }

    [Fact]
    public void Parses_incoming_miss_as_zero_damage_shot()
    {
        var ev = LogParser.ParseCombat(
            "[ 2026.11.01 17:36:36 ] (combat) Pithi Arrogator misses you completely");
        Assert.NotNull(ev);
        Assert.Equal(CombatDirection.Incoming, ev!.Direction);
        Assert.Equal(0, ev.Damage);
        Assert.Equal("Pithi Arrogator", ev.OtherParty);
        Assert.Null(ev.Weapon);
        Assert.True(ev.IsMiss);
    }

    // ── non-combat content ─────────────────────────────────────────────

    [Fact]
    public void Non_combat_channels_are_recognized_but_not_combat_events()
    {
        var bounty = LogParser.ParseCombat(
            "[ 2026.11.01 17:34:50 ] (bounty) You have earned 1,350.00 ISK for the destruction of your enemies");
        var notify = LogParser.ParseLine(
            "[ 2026.11.01 17:36:20 ] (notify) <color=0xffcc0000>Pithi Arrogator</color> missed you");
        Assert.Null(bounty);
        Assert.NotNull(notify);
        Assert.Equal("notify", notify!.Channel);
        Assert.Equal("Pithi Arrogator missed you", notify.Text);
    }

    [Fact]
    public void Header_block_and_banner_lines_are_not_parseable()
    {
        Assert.Null(LogParser.ParseLine("------------------------------------------------------------"));
        Assert.Null(LogParser.ParseLine("  Gamelog"));
        Assert.Null(LogParser.ParseLine("  Listener: Apollo Lange"));
        Assert.Null(LogParser.ParseLine("  Session Started: 2026.11.07 13:28:14"));
    }

    // ── fixture regression: whole real session ─────────────────────────

    [Fact]
    public void Parses_the_full_captured_session_without_unknown_damage_lines()
    {
        var lines = LoadFixture("session-full-combat.txt");
        var events = lines.Select(LogParser.ParseCombat).OfType<CombatEvent>().ToList();

        Assert.True(events.Count >= 250, $"expected hundreds of combat events, got {events.Count}");
        Assert.All(events, e => Assert.False(string.IsNullOrWhiteSpace(e.OtherParty)));
        Assert.All(events, e => Assert.True(e.Time.Year >= 2026));

        var misses = events.Count(e => e.IsMiss);
        var damage = events.Where(e => !e.IsMiss).Sum(e => (long)e.Damage);
        Assert.True(damage > 0);
        Assert.True(events.Count(e => e.Direction == CombatDirection.Outgoing) > 0);
        Assert.True(events.Count(e => e.Direction == CombatDirection.Incoming) > 0);
    }

    [Fact]
    public void Curated_fixture_covers_all_ranked_line_shapes()
    {
        var lines = LoadFixture("curated-lines.txt");
        var events = lines.Select(LogParser.ParseCombat).OfType<CombatEvent>().ToList();
        Assert.True(events.Count >= 6, $"expected >=6 events, got {events.Count}");
        Assert.Contains(events, e => e.Direction == CombatDirection.Outgoing && !e.IsMiss && e.Weapon is not null);
        Assert.Contains(events, e => e.Direction == CombatDirection.Incoming && !e.IsMiss);
        Assert.Contains(events, e => e.IsMiss && e.Direction == CombatDirection.Outgoing);
        Assert.Contains(events, e => e.IsMiss && e.Direction == CombatDirection.Incoming);
    }
}
