---
status: done
---

# Phase 8 — HUD incoming attackers panel

Goal: the requested threat visualization. Up to **five rows** below the
`OUT | IN` row, each for one attacker: a horizontal bar whose width is
proportional to their share of the incoming dps, with name + dps written
**on top of the bar**. Data: phase 7's per-attacker snapshot rows.

## Layout & content rules

- **Row budget is five.** With ≤ 5 attackers, each row is one name. When
  more than 5 attacker rows exist, the rows ranked 5th and below are merged
  into a single final row labeled `Others` (their dps summed). Exactly 5
  attacker rows ⇒ five name rows, no Others row.
- Bars use the window the HUD currently displays as primary (phase 6's
  primary-window selection, default 15 s). The panel and the big `IN`
  number always answer for the same window.
- Ranking by dps within that window, descending; name rows keep that order,
  and the `Others` row always sits last regardless of its dps. **The row
  with the highest dps is 100 % wide — whether it is a name row or the
  Others row — and every other bar's width is proportional to that
  leader.** No capping exists: a leading Others row is displayed at full
  width, not clipped.
- **Miss-only attackers render as dimmed rows.** A row whose dps is 0 (only
  missed shots in the window) shows a zero-width bar, its name at reduced
  opacity, and its miss count in place of a dps value (e.g. `12 misses`).
  They rank below all damaging attackers (by miss count, descending) and
  count toward the row budget exactly as damaging rows do — falling into
  Others the same way. When no attacker has dps > 0 but miss-only ones
  exist, the panel shows only its dimmed rows and no leader, so nothing is
  100 % wide.
- Row anatomy: one bar per row (~12 logical px tall), full-radius rounded
  ends, red-family translucent fill consistent with the `IN` line color.
  Overlaid on the bar: attacker name left, dps right, small light text
  (readable over the bar's fill). Long names truncate with `…`.
- Visibility of the whole block: it exists while **any attacker row
  exists** (damaging or miss-only); when none do (window empty / no one
  shooting), the rows disappear and the HUD collapses back to its fixed
  compact size — the "blocks view when idle" problem must not exist.
- Growing rule: when row count changes, the HUD's **bottom edge stays
  anchored** (Top compensates for the height delta) so the panel grows
  upward-only from the status line and never drifts off-screen in the
  default bottom-right placement. A saved custom position follows the same
  rule; if the grown window would leave the virtual screen, Top clamps.
- Stack order top-to-bottom: title (buttons) → `OUT | IN` (single row) →
  type bar (phase 5) → attacker rows → status line.

### Compact layout amendment (amends phase 3's stacks)

To make room for the panel, the big `OUT` and `IN` numbers **share one
row**: `OUT` on the left half, `IN` on the right half, right-aligned pair.
Font sizes shrink to fit (≈16 px bold), keeping the established colors.
This replaces phase 3's two stacked lines; phase 3's doc gets the pointer
added when this phase implements it. The type bar (phase 5) remains 100 %
wide, now spanning beneath the shared row.

## Interaction rules

- Drag, right-click menu, opacity, tray behavior all unaffected; rows are
  part of the drag surface like the type bar.
- All drawing happens from the same `DpsSnapshot` as every other element in
  the same 200 ms tick — no new timers, no aggregation on the UI thread, no
  disk I/O.
- Top-5 layout recomputes freely each tick; rows may appear, vanish and
  re-rank between ticks without animation (deliberate, keeps it cheap and
  honest).

## Acceptance criteria

- [x] With a replayed multi-attacker stream, bar widths are proportional
      with the leader at 100 % (leader may be a name row or Others), and
      overlaid dps values equal the snapshot's per-attacker values in the
      primary window. Screenshot-verified on the ×90 replay of
      `session-20261107-from-1942.txt`: `Corelatis Wing Leader 30.7` full
      width, `Corelatis Platoon Leader 9.3` at ≈30 % — 9.3/30.7 exactly.
- [x] 6+ attackers merge into exactly `4 names + Others`: the merge rules
      (damaging-first, miss-only by miss count with name tie-break, tail
      into Others, Others always last even when it would lead) live in the
      UI-free Core helper `AttackerPanel.DisplayRows` with seven unit tests
      (56 total). The HUD method only draws.
- [x] Miss-only attackers render dimmed with a miss count; verified both
      shapes on-screen mid-replay: a single dimmed row mixed into damaging
      rows (rank 5, `Corelum Chief Watchman — 5 misses`, zero bar) and the
      all-miss-only window (`IN —`, three dimmed rows `6/6/3 misses` ranked
      by miss count, NO leader, nothing 100 % wide).
- [x] Zero incoming dps and no miss-only attackers ⇒ panel fully gone and
      the HUD collapses back to compact height with the bottom edge where
      it was (bottom-anchored growth via Top compensation on SizeChanged,
      clamped to the virtual screen; x/y persisted on every growth change).
- [x] Rows follow the phase-6 primary-window selection (they read the same
      `WindowOf` snapshot; switching re-renders synchronously).
- [x] `OUT` and `IN` share one row (16 px bold, left/right pair); the type
      bar spans the full width beneath them.
- [x] Dragging, menus and opacity tune the grown HUD identically to the
      compact one; persisted bounds stay valid across restart with panel
      grown or collapsed. Rows are plain children of the Root border, part
      of the drag surface; bounds are saved on every growth change
      (in-game interactive confirmation still pending, like phases 3/6).
- [x] `dotnet build` 0 warnings; `dotnet test` all green (Core's existing
      49 tests unchanged — `AttackerPanel` is an addition, not a rewrite);
      single-file publish re-verified.

## Amendment (2026-11-09): IN always numeric

User decision: the shared `OUT | IN` row is symmetric — `IN` prints its
number at zero exactly like `OUT` (`IN 0 dps`), retiring the earlier
undocumented `IN —` dash styling for idle windows. No data model change;
purely the HUD text. (The `IN —` strings quoted as *observations* in this
doc's verification notes below refer to the pre-amendment build.)

## Verification notes (2026-11-09)

- Multi-rat replay at ×90 (screenshots over the live desktop): compact
  `OUT | IN` row, type bar spanning full width, 1–5 attacker rows across
  ~20 samples, dimmed miss-only rows in both single-row and rows-only
  windows, idle collapse, and growth behavior all observed. The maximum
  concurrent damaging rows in the fixture's 15 s primary window topped at
  exactly 5, so the Others row appears when the budget overflows — covered
  by the `AttackerPanel` unit tests rather than a lucky screenshot.
- Two adjacent defects found and fixed during verification:
  1. **Phase-9 race**: `Take` on the epoch clock (before the feed's first
     timestamped line) overflowed `DateTime` and killed the HUD
     mid-replay. Fixed in Core `ReplayFeed` with a `TimeKnown` gate; hosts
     skip aggregation ticks until it turns true (see phase-9 spec).
  2. **Status clobber**: the 200 ms tick overwrote the "replay finished"
     line every second; the tick now freezes the status line after
     `Finished`, ending with a final session-totals verdict line.

## Pending user confirmation

Interactive in-game behaviors (drag across rows, menus over the grown
window, restart with grown/collapsed bounds) follow the phases-3/6 pattern:
staged with the app and confirmed during real play.

## Out of scope

Row-level detail beyond name + dps (hit kinds per attacker, weapon types),
sorting toggles, colored threat gradients, animations/transitions, mouse
hover highlighting of names.
