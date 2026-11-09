---
status: draft
---

# Phase 10 — Incoming damage types (estimated)

Goal: bring the type breakdown to the **other half** of the HUD. Fixtures
proved (2026-11-09, `session-20261107-from-1942.txt`) that incoming lines DO
sometimes name the weapon — `103 from Mercenary Commander - Scourge Heavy
Missile - Hits` — while turret-style incoming hits never do (`80 from X -
Glances Off`). Measured split there: 995 of ~17.8k incoming damage carried a
weapon (~5 %), zero in the 2026-11-01 session. So a two-tier attribution
model covers everything:

1. **Weapon tier (measured-ish)**: incoming events whose `Weapon` is set are
   attributed through the **existing** `type-profiles.json` weapon table
   (same case-insensitive lookup, same EM/Thermal/Kinetic/Explosive vector).
   Scourge Heavy Missile (the only observed rat weapon so far) is a user
   entry like any weapon.
2. **Attacker tier (inferred)**: weaponless incoming events attribute by the
   **attacker's** profile — a new user-curated
   `%APPDATA%\EveDpsOverlay\attacker-profiles.json`, same JSON shape,
   normalization, inert-on-missing-broken rules as the weapon table.

## Rules

- Attribute priority per event: weapon profile when the event carries a
  weapon AND the table knows the name; else attacker profile; else Unknown.
  No mixing of partial profiles within one event.
- Only incoming, non-miss events are attributed. Misses (incoming "misses
  you completely") add no damage/shot to any type, exactly like the per-
  attacker rows (phase 7).
- `DpsWindow` gains `IncomingTypes` (same fixed `DamageTypes.All` order,
  Unknown = everything the tiers did not claim). Invariant tested: the sum
  of `IncomingTypes` equals the window's `IncomingDps` — same events, same
  math, phase-7-style structural guarantee.
- Snapshot gains `UnmappedAttackers` (sorted, case-insensitive) alongside
  `UnmappedWeapons`, so curation needs stay visible in the console.
- No per-attacker damage types yet (composable later, per phase-7's out of
  scope); no changes to the attackers panel in this phase.
- Core stays UI-free; the aggregator stays one pass over the bounded queue.

## HUD (display)

- A second full-width segmented bar, directly beneath the existing type
  bar: same five segments, same colors, same "dps in its middle" number
  rule, same ≥ ~30 px hide rule, still bound to the primary-window
  selection (one `DpsSnapshot`, one 200 ms tick — no second pipeline).
- Visual distinction from the OUT bar (see the open questions below —
  decided before implementation).
- Right-click menu gains **Open attacker profiles file** (below the weapon
  one; same seed-if-missing + `Process.Start` mechanics).

## Console surface

- `--replay` summaries gain an `# in-types(60s) … | unmapped attackers: …`
  block in the same shape as `FormatTypes` — cross-check tool like phase 4.

## Acceptance criteria

- [ ] Weapon-tier attribution: incoming events with a known weapon land in
      their profiled types; with an unknown weapon they fall through to the
      attacker tier; with neither they are Unknown. Tested on a synthetic
      stream with pinned reference time.
- [ ] Attacker-tier attribution: weaponless events attribute by the
      attacker profile; two attackers with different profiles separate
      correctly in one window.
- [ ] Sum of `IncomingTypes` equals window `IncomingDps` on synthetic cases
      AND on both real fixtures at their last combat timestamp.
- [ ] Inert-file behavior: missing/corrupt `attacker-profiles.json` ⇒ all
      weaponless incoming damage is Unknown, hosts unaffected (mirrors
      phase 4).
- [ ] Console `--replay` shows the in-types block; values cross-checked
      against a hand-computed expectation for the fixture's final burst.
- [ ] HUD shows the second bar bound to the primary window with all the
      phase-5 segment rules; switching the window re-bases both bars.
- [ ] Existing suite green (extend, do not break: `IncomingTypes` addition
      updates the phase-2/4 window record usages); `dotnet build` 0
      warnings; `dotnet test` all green; single-file publish works.
- [ ] Per the git workflow rule: work happens on `phase-10-incoming-types`,
      merges to `main` only when all the above are green.

## Fixture note

Fixtures stay untouched (never edited). `session-20261107-from-1942.txt`
gains a new job here: its Mercenary Commander river proves the weapon tier,
its turret rats prove the attacker tier. The 2026-11-01 fixture (0 % weapon
coverage) proves the pure attacker path.

## Out of scope

Per-attacker type breakdowns (composable later), NPC-faction auto-prefill
of profiles from the SDE (backlog), EM/TH/KIN/EX split per attacker row
display, any second multiplier for incoming view.
