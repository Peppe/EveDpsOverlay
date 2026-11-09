---
status: done
---

# Phase 7 — Incoming damage attribution (per-attacker pipeline)

Goal: numbers for the HUD attackers panel (phase 8): **who is shooting me,
and how hard**. Unlike damage *type* (phase 4), this needs no inference:
incoming log lines carry the attacker's name, and `CombatEvent` already
stores it in `OtherParty`. This is a `EveDps.Core` phase, console-verifiable
exactly like phase 4.

## Rules

- Per-attacker dps is computed inside the same rolling 5/15/60 s windows the
  aggregator already maintains: during `Take`'s single pass over the event
  queue, incoming non-miss events additionally bucket by `OtherParty`.
  Cost stays O(events in window); distinct attackers within a window are
  few, so no unbounded growth.
- Only **incoming** damage is attributed; misses (incoming "misses you
  completely" lines) add no damage and no shots to any attacker row.
  Outgoing per-target attribution is out of scope (the top-targets idea
  lives in `intent.md`'s backlog).
- Misses (incoming "misses you completely" lines) add no damage anywhere.
  An attacker with misses but zero damage in a window still produces a row,
  flagged as **miss-only** (`IncomingDps = 0`, miss count included) — the
  HUD (phase 8) renders those rows dimmed instead of hiding them. Damaging
  attackers never carry this flag.
  Snapshot gains, per window: a descending-sorted list of
  `Name, IncomingDps, Misses` rows for every attacker with dps > 0 **or**
  ≥ 1 miss in the window. Invariant that must hold and be tested: the sum
  of per-attacker dps within a window equals that window's `IncomingDps`
  (same events, same math).
- No per-attacker session totals for now; the panel reflects rolling windows
  only (a rat that stops shooting decays out of the table, which is the
  behavior a pilot wants for threat assessment).

## Console surface

- `--replay` summaries gain one thin block: top attackers by dps at the
  session's last timestamp, descending, with an Others-share line for cross-
  checks. Rendering stays dumb; the math lives in Core.

## Fixture note

`session-full-combat.txt` has a single attacker, so it proves the invariant
(one row equals window `IncomingDps`) but not the ranking path. Ranking is
proven with synthetic multi-attacker streams **and** — since 2026-11-08 —
with the real multi-rat fixture `session-20261107-from-1942.txt` (18 distinct
attackers; see the fixtures inventory in `intent.md`). Existing fixtures are
never edited; new sessions are added as new files.

## Acceptance criteria

- [x] Per-attacker dps equals (attacker's damage within window) / window
      length, verified against a synthetic 3-attacker stream with pinned
      reference time.
- [x] Attacker rows decay out when their events leave the window; an
      attacker with no damage and no misses in the window produces no row
      (the "Old Rat" row vanishing from the 5 s/whole-window decay tests).
- [x] A miss-only attacker produces exactly a zero-dps row with its miss
      count; sum of rows still equals the window's `IncomingDps` (no row
      carries damage that isn't in the window's total).
- [x] Sum of rows equals the window's `IncomingDps`: proven on both real
      fixtures (`session-full-combat.txt`,
      `session-20261107-from-1942.txt` — the latter ranks two attackers in
      the final 60 s: Corelatis Platoon Leader 11.4/3m, Corelatis Wing
      Leader 2.1/2m) plus two synthetic cases, all windows every time.
- [x] Descending order with stable tie-breaking (dps equal ⇒ name,
      `string.CompareOrdinal`).
- [x] All previously green tests stay green (suite grown 41 → 48, seven new
      tests); the aggregator stays one pass over the bounded window queue;
      O(1)-per-tick property survives (dictionary cost = rows present).
- [x] `dotnet build` 0 warnings; `dotnet test` all green (48/48).

## Implementation record (2026-11-08)

- `DpsWindow` gained `Attackers` (`IReadOnlyList<AttackerRow>`); new
  `AttackerRow(Name, IncomingDps, Misses)` record with `IsMissOnly`.
- `Take` buckets incoming events per window into
  `Dictionary<string,(double Damage,int Misses)>` during its existing pass —
  the same events feed `inSum[w]`, so the sum invariant is structural, not
  coincidental. Blank `OtherParty` (never observed in fixtures) would land
  in an "unknown" bucket to keep the invariant honest under any parser
  change.
- Console: `--replay` summaries print an `attackers(60s)` block (top 5,
  `name dps[/misses m]` pairs, plus an `others(n)` share line only when more
  rows exist). `Name` ordering is ordinal; rows list may be empty.

## Out of scope

HUD rendering (phase 8), outgoing per-target attribution (specced nowhere
yet; the symmetric panel idea lives in the intent backlog), per-attacker
session totals, per-attacker damage-type breakdowns (composable later),
grouping NPC ship vs player names.
