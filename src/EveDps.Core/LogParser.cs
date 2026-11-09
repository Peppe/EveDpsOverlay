using System.Globalization;
using System.Text.RegularExpressions;

namespace EveDps.Core;

/// <summary>
/// Parses EVE Online gamelog lines. Real combat lines are full of markup
/// (color/font/bold tags), so everything is stripped before matching. See
/// docs/spec/fixtures/ for ground truth and AGENTS.md for workflow rules.
/// </summary>
public static partial class LogParser
{
    private static readonly Regex LineStart = new(
        @"^\[\s*(?<ts>\d{4}\.\d{2}\.\d{2}\s+\d{2}:\d{2}:\d{2})\s*\]\s+\((?<channel>\w+)\)\s+(?<rest>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex Damage = new(
        @"^(?<dmg>\d+)\s+(?<dir>to|from)\s+(?<payload>.+)$",
        RegexOptions.Compiled);

    // "Your Civilian Gatling Railgun misses Pithi Arrogator completely - Civilian Gatling Railgun"
    private static readonly Regex OutgoingMiss = new(
        @"^Your\s+(?<weapon>.+?)\s+misses\s+(?<name>.+?)\s+completely(?:\s+-\s+.+)?$",
        RegexOptions.Compiled);

    // "Pithi Arrogator misses you completely"
    private static readonly Regex IncomingMiss = new(
        @"^(?<name>.+?)\s+misses\s+you\s+completely$",
        RegexOptions.Compiled);

    private static readonly Regex Markup = new("<[^>]*>", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly HashSet<string> HitKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Hits", "Grazes", "Smashes", "Penetrates", "Scrapes", "Wrecks",
        "Glances Off", "Deflected", "Ricochet",
    };

    /// <summary>Removes inline EVE formatting tags (color/font/bold/…).</summary>
    public static string StripMarkup(string line) => Markup.Replace(line, string.Empty);

    /// <summary>
    /// Parses any channel line. Returns null for header-block lines, the
    /// banner, or anything that is not a `[ time ] (channel) ...` line.
    /// </summary>
    public static ChannelLine? ParseLine(string line)
    {
        if (line is null) return null;
        var m = LineStart.Match(Whitespace.Replace(line, " ").Trim());
        if (!m.Success) return null;
        if (!TryParseTime(m.Groups["ts"].Value, out var time)) return null;
        var text = Collapse(StripMarkup(m.Groups["rest"].Value));
        return new ChannelLine(time, m.Groups["channel"].Value, text);
    }

    /// <summary>Parses a combat-channel line into a <see cref="CombatEvent"/>.</summary>
    public static CombatEvent? ParseCombat(string line)
    {
        var parsed = ParseLine(line);
        if (parsed is null || parsed.Channel != "combat") return null;
        var text = parsed.Text;

        var damage = Damage.Match(text);
        if (damage.Success)
        {
            var direction = damage.Groups["dir"].Value == "to"
                ? CombatDirection.Outgoing
                : CombatDirection.Incoming;
            var amount = int.Parse(damage.Groups["dmg"].Value, CultureInfo.InvariantCulture);
            var parts = damage.Groups["payload"].Value.Split(" - ", StringSplitOptions.TrimEntries);

            var hitKind = (string?)null;
            var last = parts.Length - 1;
            if (last >= 0 && HitKinds.Contains(parts[last]))
            {
                hitKind = parts[last];
                last--;
            }

            if (last < 0) return null;
            var name = parts[0];
            var weapon = last >= 1
                ? string.Join(" - ", parts[1..(last + 1)])
                : null;
            return new CombatEvent(parsed.Time, direction, amount, name, weapon, hitKind, IsMiss: false);
        }

        var outMiss = OutgoingMiss.Match(text);
        if (outMiss.Success)
        {
            return new CombatEvent(parsed.Time, CombatDirection.Outgoing, 0,
                outMiss.Groups["name"].Value, outMiss.Groups["weapon"].Value,
                HitKind: null, IsMiss: true);
        }

        var inMiss = IncomingMiss.Match(text);
        if (inMiss.Success)
        {
            return new CombatEvent(parsed.Time, CombatDirection.Incoming, 0,
                inMiss.Groups["name"].Value, Weapon: null,
                HitKind: null, IsMiss: true);
        }

        // Combat text that is neither damage nor a miss: warp/disruption and
        // similar flavor lines. Not a damage event; not an error.
        return null;
    }

    private static string Collapse(string s) => Whitespace.Replace(s, " ").Trim();

    private static bool TryParseTime(string value, out DateTimeOffset time) =>
        DateTimeOffset.TryParseExact(value, "yyyy.MM.dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out time);
}
