---
status: done
---

# Phase 2 — DPS aggregation

Goal: turn `CombatEvent`s into numbers worth showing: rolling DPS both ways,
session totals, and (later HUD) a compact breakdown. Still no UI beyond thin
console rendering: phase 2 lives in `EveDps.Core` and is proven by tests.

## Rules

- Window math uses the events' own (UTC, in-log) timestamps, not wall clock.
  The snapshot's reference time comes from an injected clock (default UTC now);
  for deterministic tests the caller pins the reference (e.g. last event time).
- Misses count as shots with zero damage for hit-rate stats; they never add
  to DPS.
- Windows: 5 s, 15 s, 60 s, plus monotonic session totals, per direction.
- DPS over a window is sum(damage)/window length — a dead window shows 0 even
  when partially empty (classic meter behavior, deliberate).
- Snapshots are point-in-time and cheap to read at 5–10 Hz: one pass over the
  bounded event queue (no LINQ over full history), stale events pruned O(1).
- Keep `CombatEvent` immutable; the aggregator owns copies, not references
  into the parser.

## Sketch

```
DpsSnapshot(Reference, Windows: [{Seconds, OutgoingDps, IncomingDps, Shots},
                                ... known windows],
            Session: {TotalOutgoing, TotalIncoming, Events, Misses})
```

## Acceptance criteria

- [x] DPS for a window equals sum(damage in window) / window length using
      event timestamps, verified against a synthetic event stream.
- [x] Events older than the window drop out on the next snapshot without
      re-adds; the event queue stays bounded (verified over a 100k-event
      synthetic session; window hit counts stay within recent bounds).
- [x] Session totals survive every window; misses tracked separately from
      damage and counted as shots.
- [x] Snapshotting stays O(1) per tick amortized (bounded queue, single pass
      over windowed events; no LINQ over full history).
- [x] The full captured session fixture, replayed and snapshotted at its last
      event time, produces windows that match an independently computed result
      in the test (DpsAggregatorTests.Captured_fixture_windows_match_...).
- [x] Console rendering stays thin: `--dps` shows the rolling windows live
      (in-place line update, redirected-output fallback), and
      `--replay`/`--smoke` summaries include the final window values.

Verified 2026-11-07: `dotnet test` → 24/24 passed; fixture replay readout at
session end: `60s OUT 0.9 IN 0.0 (37) | 15s OUT 3.7 IN 0.1 (13) |
5s OUT 11.0 IN 0.2 (5) | session 591/78` with 294 events / 171 misses —
matching phase 1 counts.

## Out of scope

WPF rendering, per-weapon/target breakdowns, hit-quality stats.

