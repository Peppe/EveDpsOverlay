---
status: done
---

# Phase 12 — Layout toggle: horizontal ↔ vertical source panels

Goal: a button that switches the HUD between the two source-panel layouts.
Depends on phase 11 (both panels exist; bars are type-colored).

## The two layouts

- **Horizontal** — window keeps its current width (300 px). OUT panel is
  the left half, IN panel the right half; bars are roughly half the
  phase-11 width. Text alignment mirrors the big numbers: the OUT panel's
  text is left-aligned (left edge), the IN panel's text is right-aligned
  (right edge) — matching `OUT` left / `IN` right on the shared number row.
- **Vertical** — the whole window becomes roughly **half the current
  width** (~150–160 px) and grows in height: both panels stack at full
  window width. Consequences designed for, since nothing else fits at that
  width:
  - The big `OUT | IN` numbers stop sharing one row — each gets its own
    line (`OUT 92.5 dps`, then `IN 10.2 dps`).
  - The title text hides; the three cycle buttons (5/15/60, opacity,
    layout) stay in a single row and shrink to icon-sized.
  - The status line abbreviates to session totals only (`session x/y · n
    misses`); the small windows line drops.
  - Type bars span the narrow full width; the ≥ ~30 px number rule applies
    unchanged (fewer numbers render — expected).
- Both layouts show the same data from the SAME snapshot/tick — pure
  presentation; no Core changes.

## Toggle button

- Third button in the title row, next to the 5/15/60 and opacity buttons.
  Same `HudButton` style.
- Icon: double arrow glyphs — `↔` for horizontal, `↕` for vertical. The
  button shows the icon of the layout clicking it will switch **to**
  (action-oriented; unlike the 5s/85% buttons which show current state —
  a deliberate difference, since "what happens if I click" is the useful
  information for a binary toggle).
- Click cycles Horizontal → Vertical → Horizontal instantly; both panels
  re-render synchronously (the phase-5/6 rule: window switches re-base).

## Persistence + placement

- `settings.json` gains `Layout` (`"horizontal"` | `"vertical"`; default
  horizontal), saved with the same write-on-change rules as x/y/opacity.
- Window width changes on toggle are anchored to the RIGHT edge (the HUD
  lives bottom-right; the left edge moves), height growth stays
  bottom-anchored (phase 8), clamped into the virtual screen. Bounds saved
  per usual on every geometry change.
- Grown/collapsed bounds stay valid across restart in either layout
  (phase-8 persistence rules apply unchanged).

## Acceptance criteria

- [x] Button cycles both directions; panels/type bars/numbers re-render in
      the same tick; no data resets. (Clicked both ways live; settings.json
      followed every click.)
- [x] Horizontal: panels side-by-side, IN panel right-aligned, bars ~half
      width, window stays 300 px wide.
- [x] Vertical: window ~half width; numbers stacked, panels stacked, title
      hidden, status abbreviated; height growth stays bottom-anchored and
      clamped; right edge stays fixed on width change (X moved exactly
      +144/−144 px = the width delta on every toggle).
- [x] Layout persists across restart (settings.json), including mid-replay
      usage. (Started a fresh replay with Layout=vertical saved.)
- [x] Miss-only dimming, Others merge, ≥30 px number rule, and idle
      collapse all still hold in both layouts (spot-check screenshots).
- [x] HUD screenshot verification at ×90 replay: both layouts captured.
      Vertical: title hidden, tight buttons, stacked OUT/IN numbers, both
      panels stacked full width (IN rows right-edge mirrored), status
      wrapped to two lines. Horizontal: side-by-side halves with IN rows/
      caption right-aligned; idle side collapses.
- [x] `dotnet build` 0 warnings; `dotnet test` all green (Core untouched;
      87/87); single-file publish works.
- [x] Git workflow rule: implemented on `phase-12-layout-toggle`, merged to
      `main` only when green.

## Verification notes (2026-11-14)

- Two real startup crashes were caught by the first live run, both caused
  by ApplyLayout now rendering from the constructor: (1) UpdateHud's
  replay clock dereferenced `_replay` before StartTailer created it —
  fixed with an explicit null guard (this race pre-dated phase 12 but was
  previously only reachable if a tick beat Loaded); (2) SavePosition wrote
  NaN Left/Top (unplaced window) into JSON — guarded until placement.
- The vertical status line clipped at the narrow width; StatusText now
  wraps (harmless for the horizontal one-liner).
- Interpretation note: the IN panel's right-edge mirroring (row names +
  caption) is kept in BOTH layouts — it reads as the panel's identity
  ("IN" hugs the right edge like the big IN number), which vertical keeps.
- Buttons shrink via tighter Padding in vertical (2 px vs 4 px); text
  content stays ("15s"/"85%") — dropping it would hide the current state,
  and the row fits the 156 px window as-is.

## Out of scope

Detachable/resizable panels, per-panel opacity, three-column layouts,
drag-to-reorder panels, panel content scrolling.
