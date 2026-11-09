namespace EveDps.Core;

/// <summary>Whether damage flows away from or onto the player.</summary>
public enum CombatDirection
{
    Outgoing,
    Incoming,
}

/// <summary>
/// One damage or miss event parsed from a combat-channel line.
/// <paramref name="OtherParty"/> is the pilot/NPC on the other side of the
/// exchange regardless of direction; <paramref name="Weapon"/> is only present
/// when the log emits it (outgoing lines; some incoming lines).
/// </summary>
public sealed record CombatEvent(
    DateTimeOffset Time,
    CombatDirection Direction,
    int Damage,
    string OtherParty,
    string? Weapon,
    string? HitKind,
    bool IsMiss);

/// <summary>A recognized channel line with its timestamp and stripped text.</summary>
public sealed record ChannelLine(DateTimeOffset Time, string Channel, string Text);
