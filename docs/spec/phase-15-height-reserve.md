---
status: done
---

# Phase 15 — Height stability toggle (reserve row space)

> **Amendment (2026-11-15 live feedback):** in AUTO mode the big OUT/IN
> number headers are now ALWAYS visible — idle collapses only the row
> list, never the panel header (previously the whole panel went away).
> Reserved mode unchanged. See `feat/live-feedback-fixes`.

Goal: the window grows and shrinks as sources enter/leave the lists
(phase 8 idle-collapse, phase 11/12 row churn). That jumping is annoying
in combat; but auto-sizing is also what keeps the HUD minimal. A toggle
chooses between the two behaviors: reserve space for the full five-row
budget (window height constant), or hug the content as today.

Depends on phases 13/14 (panel stack shape). Presentation only; no Core
changes (`SourceRowBudget = 5` is already the full budget — 4 names +
trailing Others when merging, fewer only when sources run out).

## Two modes

- **Auto (default; current behavior)** — window hugs content exactly as
  phase 8/11/12 built it: panels shrink when lists shrink, the panel
  area collapses when idle, height growth stays bottom-anchored.
- **Reserved** — each panel ALWAYS occupies its full five row slots:
  - Empty slots below the current row count take up space but render
    nothing (invisible background — not dim placeholder rows, which
    could be mistaken for miss-only dimming).
  - The panel area never collapses (idle no longer shrinks the window).
  - Result: window height is constant in reserved mode — enemies
    appearing/disappearing cause no resize. Width is already constant
    per layout (phase 12), so the whole window is now stable.
  - Applies to both panels simultaneously (one toggle; window height is
    shared, and vertical stacks both panels anyway).
- Both layouts behave identically; layout switching in reserved mode
  changes only width (right-edge anchored per phase 12).

## Toggle

- Fifth button in the title row, same `HudButton` style; content shows
  state (glyph + tooltip; e.g. `↕`-family or `pin` metaphor — final
  glyph picked at implementation, shown in the verification screenshot).
- Persisted in `settings.json` (e.g. `ReserveRows` bool, default false =
  Auto), same write-on-change rules as Layout/Legend.
- Title-row fit: now SIX tight buttons at 156 px vertical width — same
  screenshot legibility check as phase 14; shrink glyphs/spacing before
  ever dropping state.

## Amendment pointers

- `phase-13-bars-into-headers.md` / `phase-14-damage-legend.md`: stack
  unchanged; only row-area sizing behavior toggles.
  `phase-8-hud-attackers-panel.md`: idle-collapse now conditional on
  mode. `phase-12-layout-toggle.md`: button count note.

## Acceptance criteria

- [x] Auto mode: behavior byte-identical to phase 13/14 build (idle
      collapse, growth, shrink all as before).
- [x] Reserved mode: with 0–5 rows present, window height stays exactly
      the five-row height (measure across a replay: no size change when
      sources spawn/despawn); empty slots invisible; no collapse when
      idle.
- [x] Toggle switches instantly both ways; re-render same tick; no data
      resets; works in both layouts; geometry clamps/anchors per phase 12.
- [x] State persists across restart via settings.json; default Auto on
      fresh settings (existing users see no change after upgrade).
- [x] `dotnet build` 0 warnings; `dotnet test` all green; single-file
      publish runs.
- [x] Replay screenshot verification: reserved idle vs mid-combat frames
      same height; Auto frames differ.
- [x] Specs amended in the same branch; `intent.md` phase table gains
      this phase.
- [x] Git workflow: implemented on `phase-15-height-reserve`, merged to
      `main` only when green.

## Verification notes (2026-11-15)

- Replay at ×30, horizontal, reserved: a 1-row frame (~5 s, one OUT row
  + one IN row) and three 5-row frames (~25 s) captured at IDENTICAL
  window geometry (332×179 incl. margin, Top unchanged) — zero jumping.
  Live-tail idle run: OUT 0 dps / IN 0 dps with no rows at all still
  holds the same 179 px height (auto mode would collapse it — heights
  128–167 px varied across the phase-13/14 auto runs).
- Empty slots render as pure background: stale bar/segment/name/value
  state is scrubbed each tick (Root has fixed Height 13, so empty slots
  are silent spacers).
- Vertical reserved verified too: both stacked panels reserve, the five
  tight buttons (15s / 85% / ↔ / ▦ / 5R) still fit the 156 px title row.
- Toggle state text: "5R" bright when reserved, "A" dim when auto
  (matches the 15s/85% show-current-state convention plus the legend
  button's brightness scheme). Load path verified via settings.json;
  the click handler mirrors the proven ToggleLegend pattern (flip, save,
  re-apply + UpdateHud same tick).
- User settings restored to Auto after verification; reserved mode is
  opt-in via the title-row button.

## Out of scope

User-set row counts other than the five-row budget, resizable window,
per-panel reserve modes, remembered per-mode window positions.
