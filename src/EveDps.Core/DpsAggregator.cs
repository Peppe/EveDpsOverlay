namespace EveDps.Core;

/// <summary>Rolling-window rates at one reference point. DPS = dmg/window.</summary>
public sealed record DpsWindow(
    double Seconds,
    double OutgoingDps,
    double IncomingDps,
    int Shots,
    IReadOnlyList<DpsType> OutgoingTypes,
    IReadOnlyList<DpsType> IncomingTypes,
    IReadOnlyList<SourceRow> OutgoingSources,
    IReadOnlyList<SourceRow> Attackers);

/// <summary>One damage type's dps within a window. Buckets follow DamageTypes.All.</summary>
public sealed record DpsType(string Name, double Dps);

/// <summary>
/// One damage source's row within a window (phase 7: incoming attackers;
/// phase 11: outgoing weapons). Rows exist for every source with dps &gt; 0
/// or ≥ 1 miss in the window; miss-only rows carry zero dps — the HUD
/// renders them dimmed, never hidden. Descending by Dps, ties broken by
/// Name. The four split fields carry the profile-attributed dps (always
/// ≤ Dps; the remainder is the Unknown share) so the HUD can draw
/// type-colored bars without re-touching the profile tables.
/// </summary>
public sealed record SourceRow(
    string Name,
    double Dps,
    int Misses,
    double Em = 0,
    double Thermal = 0,
    double Kinetic = 0,
    double Explosive = 0)
{
    /// <summary>Missed everything in this window; no damage is in any row.</summary>
    public bool IsMissOnly => Dps <= 0 && Misses > 0;
}

/// <summary>Monotonic totals since the aggregator saw its first event.</summary>
public sealed record DpsSession(long TotalOutgoing, long TotalIncoming, long Events, long Misses);

/// <summary>Point-in-time numbers; everything is a copy, safe to hand to UI threads.</summary>
public sealed record DpsSnapshot(
    DateTimeOffset Reference,
    IReadOnlyList<DpsWindow> Windows,
    DpsSession Session,
    IReadOnlyList<string> UnmappedWeapons,
    IReadOnlyList<string> UnmappedAttackers);

/// <summary>
/// Aggregates CombatEvents into rolling-window DPS and monotonic session
/// totals. See docs/spec/phase-2-dps.md (window math) and
/// docs/spec/phase-4-damage-types.md (type attribution via weapon profiles).
/// Thread-safety: callers guard Add/Take with a lock (console and the future
/// WPF timer each own their locking strategy); snapshots are immutable copies.
/// </summary>
public sealed class DpsAggregator
{
    private readonly int[] _windowSeconds;
    private readonly WeaponTypeProfiles? _profiles;
    private readonly AttackerTypeProfiles? _attackerProfiles;
    private readonly Queue<Attributed> _recent = new();
    private readonly HashSet<string> _unmappedWeapons = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unmappedAttackers = new(StringComparer.OrdinalIgnoreCase);
    private long _outgoing;
    private long _incoming;
    private long _events;
    private long _misses;

    public static readonly int[] DefaultWindows = [5, 15, 60];

    public DpsAggregator() : this(DefaultWindows) { }

    public DpsAggregator(IReadOnlyList<int> windowSeconds) : this(windowSeconds, profiles: null) { }

    public DpsAggregator(WeaponTypeProfiles? profiles) : this(DefaultWindows, profiles) { }

    public DpsAggregator(IReadOnlyList<int> windowSeconds, WeaponTypeProfiles? profiles) :
        this(windowSeconds, profiles, attackerProfiles: null) { }

    /// <summary>Full shape (phase 10): weapon tier + attacker tier profiles.</summary>
    public DpsAggregator(WeaponTypeProfiles? profiles, AttackerTypeProfiles? attackerProfiles) :
        this(DefaultWindows, profiles, attackerProfiles) { }

    public DpsAggregator(
        IReadOnlyList<int> windowSeconds,
        WeaponTypeProfiles? profiles = null,
        AttackerTypeProfiles? attackerProfiles = null)
    {
        _windowSeconds = windowSeconds.Count == 0
            ? DefaultWindows
            : [.. windowSeconds];
        Array.Sort(_windowSeconds);
        Array.Reverse(_windowSeconds); // largest first; pruning bound uses [0]
        _profiles = profiles;
        _attackerProfiles = attackerProfiles;
    }

    public DpsAggregator(params int[] windowSeconds) : this((IReadOnlyList<int>)windowSeconds) { }

    /// <summary>Adds an event; duplicate events are the caller's problem.</summary>
    public void Add(CombatEvent ev)
    {
        var attr = Attribute(ev);
        if (attr.Unmapped)
        {
            // Fully-unattributed damage feeds the curation lists: outgoing
            // unmapped weapons, and (phase 10) incoming events contribute
            // the weapon name (if the line carries one) AND the attacker.
            // Anything a tier claimed never shows up here.
            if (!string.IsNullOrEmpty(ev.Weapon))
                _unmappedWeapons.Add(ev.Weapon);
            if (ev.Direction == CombatDirection.Incoming && !string.IsNullOrEmpty(ev.OtherParty))
                _unmappedAttackers.Add(ev.OtherParty);
        }
        if (ev.IsMiss) _misses++;
        else if (ev.Direction == CombatDirection.Outgoing) _outgoing += ev.Damage;
        else _incoming += ev.Damage;
        _events++;
        _recent.Enqueue(attr);
    }

    /// <summary>
    /// Splits a hit's damage across EM/Thermal/Kinetic/Explosive:
    /// outgoing hits by the weapon's profile; incoming hits (phase 10) by a
    /// two-tier priority — weapon profile when the line carries a known
    /// weapon, else the attacker's profile, else Unknown. No mixing of tiers
    /// or partial profiles within one event. Misses carry no split (and are
    /// never flagged unmapped). Unmapped damage is carried as "ex-free" zero
    /// splits; the take() step reconstructs Unknown as damage minus the four
    /// attributed slices.
    /// </summary>
    private Attributed Attribute(CombatEvent ev)
    {
        if (ev.IsMiss)
            return new Attributed(ev, 0, 0, 0, 0, unmapped: false);

        if (ev.Direction == CombatDirection.Outgoing)
        {
            var weights = ev.Weapon is null ? null : _profiles?.GetWeights(ev.Weapon);
            if (weights is null)
                return new Attributed(ev, 0, 0, 0, 0, unmapped: true);
            return Scaled(ev, weights);
        }

        // Incoming: weapon tier first (e.g. "... - Scourge Heavy Missile -
        // Hits"), attacker tier second (weaponless turret hits), else Unknown.
        var inWeights = ev.Weapon is null ? null : _profiles?.GetWeights(ev.Weapon);
        inWeights ??= _attackerProfiles?.GetWeights(ev.OtherParty);
        if (inWeights is null)
            return new Attributed(ev, 0, 0, 0, 0, unmapped: true);
        return Scaled(ev, inWeights);
    }

    private static Attributed Scaled(CombatEvent ev, double[] weights) => new(
        ev,
        em: ev.Damage * weights[0],
        thermal: ev.Damage * weights[1],
        kinetic: ev.Damage * weights[2],
        explosive: ev.Damage * weights[3],
        unmapped: false);

    /// <summary>
    /// Computes current windows at <paramref name="reference"/> (a UTC time
    /// comparable to log timestamps) and prunes events outside the largest
    /// window forever. Cost: one pass over events within the largest window.
    /// </summary>
    public DpsSnapshot Take(DateTimeOffset reference)
    {
        var horizon = reference.AddSeconds(-_windowSeconds[0]);
        while (_recent.Count > 0 && _recent.Peek().Event.Time < horizon)
            _recent.Dequeue();

        var outSum = new double[_windowSeconds.Length];
        var inSum = new double[_windowSeconds.Length];
        var shots = new int[_windowSeconds.Length];
        var typeSum = new double[_windowSeconds.Length][];
        var inTypeSum = new double[_windowSeconds.Length][];
        // Phase 7: incoming events additionally bucket by attacker. A rat
        // that stops shooting decays out with the window — window-only rows,
        // no session totals (per the phase-7 spec).
        // Phase 11: outgoing events bucket by weapon the same way, and both
        // maps additionally accumulate the profile-attributed type slices
        // (for the HUD's type-colored row bars).
        var attackerSums = new Dictionary<string, (double Damage, int Misses, double Em, double Thermal, double Kinetic, double Explosive)>[_windowSeconds.Length];
        var weaponSums = new Dictionary<string, (double Damage, int Misses, double Em, double Thermal, double Kinetic, double Explosive)>[_windowSeconds.Length];
        for (var w = 0; w < _windowSeconds.Length; w++)
        {
            typeSum[w] = new double[DamageTypes.Attributed.Length];
            inTypeSum[w] = new double[DamageTypes.Attributed.Length];
            attackerSums[w] = new Dictionary<string, (double, int, double, double, double, double)>(StringComparer.Ordinal);
            weaponSums[w] = new Dictionary<string, (double, int, double, double, double, double)>(StringComparer.Ordinal);
        }

        foreach (var attr in _recent)
        {
            for (var w = 0; w < _windowSeconds.Length; w++)
            {
                if (attr.Event.Time < reference.AddSeconds(-_windowSeconds[w]) || attr.Event.Time > reference)
                    continue;
                if (attr.Event.Direction == CombatDirection.Outgoing)
                {
                    if (!attr.Event.IsMiss && attr.Event.Damage > 0)
                    {
                        outSum[w] += attr.Event.Damage;
                        for (var t = 0; t < DamageTypes.Attributed.Length; t++)
                            typeSum[w][t] += attr.Type(t);
                    }
                    // Phase 11 row bucket: per weapon, hits and misses both
                    // count (miss-only weapons get rows). Missing weapon
                    // names defensively bucket as "unknown".
                    var weapon = string.IsNullOrEmpty(attr.Event.Weapon) ? "unknown" : attr.Event.Weapon;
                    var wmap = weaponSums[w];
                    wmap.TryGetValue(weapon, out var we);
                    weaponSums[w][weapon] = (we.Item1 + attr.Event.Damage,
                                             we.Item2 + (attr.Event.IsMiss ? 1 : 0),
                                             we.Item3 + attr.Type(0), we.Item4 + attr.Type(1),
                                             we.Item5 + attr.Type(2), we.Item6 + attr.Type(3));
                }
                else if (attr.Event.Direction == CombatDirection.Incoming)
                {
                    inSum[w] += attr.Event.Damage;
                    // Phase 10: incoming events attribute through the same
                    // four slices (weapon or attacker tier). Same invariant
                    // as outgoing: every unit of inSum[w] lands in exactly
                    // one IncomingTypes bucket.
                    for (var t = 0; t < DamageTypes.Attributed.Length; t++)
                        inTypeSum[w][t] += attr.Type(t);
                    // Same events, same math: every unit of inSum[w] ends up
                    // in exactly one attacker row of window w (defensive
                    // "unknown" name for any hypothetical blank OtherParty —
                    // fixtures never showed one, but the invariant is the
                    // sum, not the parser).
                    var name = attr.Event.OtherParty;
                    if (string.IsNullOrEmpty(name)) name = "unknown";
                    var map = attackerSums[w];
                    map.TryGetValue(name, out var entry);
                    attackerSums[w][name] = (entry.Item1 + attr.Event.Damage,
                                             entry.Item2 + (attr.Event.IsMiss ? 1 : 0),
                                             entry.Item3 + attr.Type(0), entry.Item4 + attr.Type(1),
                                             entry.Item5 + attr.Type(2), entry.Item6 + attr.Type(3));
                }
                shots[w]++;
            }
        }

        var windows = new DpsWindow[_windowSeconds.Length];
        for (var w = 0; w < _windowSeconds.Length; w++)
        {
            var perType = new DpsType[DamageTypes.All.Length];
            for (var t = 0; t < DamageTypes.Attributed.Length; t++)
                perType[t] = new DpsType(DamageTypes.Attributed[t], typeSum[w][t] / _windowSeconds[w]);
            // Unknown = everything the four profiles did not claim.
            perType[^1] = new DpsType(DamageTypes.Unknown,
                (outSum[w] - typeSum[w].Sum()) / _windowSeconds[w]);

            // Phase 10: the same five-bucket shape for IN. Unknown is the
            // unclaimed remainder, clamped ≥ 0 (only rounding slack can make
            // the slices exceed the damage sum).
            var perInType = new DpsType[DamageTypes.All.Length];
            for (var t = 0; t < DamageTypes.Attributed.Length; t++)
                perInType[t] = new DpsType(DamageTypes.Attributed[t], inTypeSum[w][t] / _windowSeconds[w]);
            perInType[^1] = new DpsType(DamageTypes.Unknown,
                Math.Max(0, inSum[w] - inTypeSum[w].Sum()) / _windowSeconds[w]);

            // Phase 7 rows: every attacker with dps > 0 or ≥ 1 miss; sorted
            // descending by dps, ties by name (spec tie-breaking rule).
            // Phase 11: the same row shape for outgoing weapons, and both
            // carry their profile-attributed type slices.
            var attackers = new List<SourceRow>(attackerSums[w].Count);
            foreach (var kv in attackerSums[w])
            {
                if (kv.Value.Item1 > 0 || kv.Value.Item2 > 0)
                    attackers.Add(new SourceRow(kv.Key, kv.Value.Item1 / _windowSeconds[w], kv.Value.Item2,
                        kv.Value.Item3 / _windowSeconds[w], kv.Value.Item4 / _windowSeconds[w],
                        kv.Value.Item5 / _windowSeconds[w], kv.Value.Item6 / _windowSeconds[w]));
            }
            attackers.Sort(CompareRows);

            var weapons = new List<SourceRow>(weaponSums[w].Count);
            foreach (var kv in weaponSums[w])
            {
                if (kv.Value.Item1 > 0 || kv.Value.Item2 > 0)
                    weapons.Add(new SourceRow(kv.Key, kv.Value.Item1 / _windowSeconds[w], kv.Value.Item2,
                        kv.Value.Item3 / _windowSeconds[w], kv.Value.Item4 / _windowSeconds[w],
                        kv.Value.Item5 / _windowSeconds[w], kv.Value.Item6 / _windowSeconds[w]));
            }
            weapons.Sort(CompareRows);

            windows[w] = new DpsWindow(
                _windowSeconds[w],
                outSum[w] / _windowSeconds[w],
                inSum[w] / _windowSeconds[w],
                shots[w],
                perType,
                perInType,
                weapons,
                attackers);
        }

        return new DpsSnapshot(
            reference,
            windows,
            new DpsSession(_outgoing, _incoming, _events, _misses),
            [.. _unmappedWeapons.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)],
            [.. _unmappedAttackers.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>Shared row ordering: dps descending, ties by name (spec tie-break).</summary>
    private static int CompareRows(SourceRow a, SourceRow b)
    {
        var byDps = b.Dps.CompareTo(a.Dps);
        return byDps != 0 ? byDps : string.CompareOrdinal(a.Name, b.Name);
    }

    /// <summary>Type-attributed view of one queued event plus its bookkeeping flags.</summary>
    private readonly struct Attributed
    {
        public Attributed(CombatEvent ev, double em, double thermal, double kinetic, double explosive, bool unmapped)
        {
            Event = ev;
            Em = em;
            Thermal = thermal;
            Kinetic = kinetic;
            Explosive = explosive;
            Unmapped = unmapped;
        }

        public readonly CombatEvent Event;
        public readonly double Em;
        public readonly double Thermal;
        public readonly double Kinetic;
        public readonly double Explosive;
        public readonly bool Unmapped;

        public double Type(int index) => index switch
        {
            0 => Em,
            1 => Thermal,
            2 => Kinetic,
            3 => Explosive,
            _ => 0,
        };
    }
}
