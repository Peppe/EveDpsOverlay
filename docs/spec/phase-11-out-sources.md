---
status: done
---

# Phase 11 — Outgoing damage sources panel (per-weapon, type-colored)

Goal: make the source panels symmetric. The IN panel (attackers, phase 7/8)
gains a matching OUT panel: YOUR damage sources — weapons/drone bays — as
rows. And per the 2026-11-14 decision, source bars stop being solid
direction-colored: every damage bar shows its **damage type colors**.

## Data (fixture-verified 2026-11-14)

- Outgoing hits carry the weapon: `113 to The Thief - Vespa I - Smashes`
  (1,219 hit lines in `session-20261107-from-1942.txt`: Vespa I ×933,
  Hornet I ×143, Wasp I ×143).
- Outgoing misses carry the weapon too: `Your Vespa I misses Mercenary
  Commander completely - Vespa I` (177 lines).
- So an OUT panel is a perfect mirror of the IN panel: per-source dps AND
  per-source miss counts exist for outgoing.

## Core rules

- `DpsWindow` gains `OutgoingSources`: per-weapon rows in the same shape as
  the attacker rows — one record type for both (rename `AttackerRow` →
  `SourceRow`, rename the display helper `AttackerPanel` → `SourcePanel`;
  phase-7/8 docs get amendment pointers). Rows exist for every weapon with
  dps > 0 or ≥ 1 miss in the window; sorted dps-descending, ties by name;
  missing weapon name defensively buckets as "unknown".
- Invariant (same structural guarantee as phase 7/10): the sum of
  `OutgoingSources` dps equals the window's `OutgoingDps`, and the sum of
  `Attackers` dps equals `IncomingDps`.
- Each row additionally carries its **type split** (EM/Thermal/Kinetic/
  Explosive dps): outgoing events attribute through the SAME weapon profile
  tier the type bar uses; unmapped weapons carry an all-zero split (all
  Unknown). The attacker profile tier is incoming-only and never touches
  these rows. Segment sums ≤ row dps always (remainder = Unknown share).
- The panel budget/Others/dimming rules stay in the one UI-free Core
  helper — now shared by both panels.

## HUD rules

- Stack order: title → `OUT | IN` → OUT type bar → IN type bar → OUT
  sources panel → IN sources panel → status. (Amends phase 8/10 stacks.)
- **Both panels behave identically**: top-5 rows + trailing Others merge
  (phase-8 rules), miss-only rows dimmed with miss count, leader at 100 %
  row width, zero panels collapse entirely (idle HUD stays compact).
- **Bars are type-colored**: each row's bar is a miniature segmented bar —
  segments in the established type colors (EM `#4d7cff`, TH `#ff5a4e`,
  KIN `#9aa3ad`, EX `#ff9854`) sized by the row's profiled split, and the
  unclaimed remainder in the Unknown gray (`#565d66`). A fully unmapped
  source renders as one gray bar. This supersedes phase 8's solid red row
  bars for the IN panel too (same treatment for both lists).
- Row text (name + dps/misses) unchanged in style; phase 11 keeps the
  window at its current width with both panels full-width, stacked.
- Window growth stays bottom-anchored (phase 8); two panels make the grown
  window taller — that's the intended look.
- No toggle yet: the horizontal/vertical layout switch is phase 12.

## Console surface

- `--replay` summaries gain `# out-sources(60s) Vespa I 74.2/12m | …` in the
  same shape as `# attackers(...)` for cross-checks.

## Acceptance criteria

- [x] Per-weapon bucketing with miss counts on a synthetic stream at pinned
      reference time; a weapon with only misses yields a dimmed (miss-only)
      row with zero dps. (OutgoingSourcesTests, 2026-11-14)
- [x] Invariants: sum(OutgoingSources) == OutgoingDps and sum(Attackers) ==
      IncomingDps on synthetic cases AND on both real fixtures at their last
      combat timestamp.
- [x] Type split correctness: a profiled weapon's split matches its profile
      (weights × damage); an unmapped weapon's split is all-zero.
- [x] Display merge rules shared: the existing `DisplayRows` tests pass
      against the renamed/Generalized helper (no behavior change).
- [x] Console `--replay` shows the out-sources block, cross-checked against
      the fixture's known weapons (Vespa/Hornet/Wasp): `# out-sources(60s)
      Vespa I 78.5 | Wasp I 38.4/1m | Hornet I 18.2`.
- [x] HUD screenshot verification at ×90 replay: both panels visible with
      type-colored segmented bars; miss-only rows dimmed on both sides.
      OUT rows gray (drones unmapped in user profiles → all Unknown),
      IN rows segmented thermal-red/kinetic-gray per the seeded attacker
      profiles; bar lengths match the numbers (e.g. Wasp 51.9/127.3 ≈ 41 %).
- [x] `dotnet build` 0 warnings; `dotnet test` all green (87/87; rename
      fallout in existing tests updated, behavior unchanged); single-file
      publish works.
- [x] Git workflow rule: implemented on `phase-11-out-sources`, merged to
      `main` only when everything above is green.

## Verification notes (2026-11-14)

- One real bug caught by screenshot verification: the row-bar segment
  Borders were added to the segment grid without `Grid.SetColumn`, so all
  five stacked in column 0 and bars rendered invisible. Fixed and
  re-verified; unit tests had no coverage of the WPF layer, as expected.
- Renames applied everywhere: `AttackerRow`→`SourceRow` (gains the four
  split fields, `.IncomingDps`→`.Dps`), `AttackerPanel`→`SourcePanel`
  (PanelRow gains the merged slices), `RenderAttackers`→`RenderPanel`
  (shared by both panels), `AttackerRowBudget`→`SourceRowBudget`.

## Out of scope

Layout toggle / half-width modes (phase 12), per-target mirror (backlog),
per-source IN-panel attacker-type splits beyond what profiles provide,
per-weapon mini numbers beyond the existing ≥ ~30 px bar rule.
