---
status: done
---

# Phase 13 — Bars into headers: remove type bars, numbers join the panels

Goal: declutter the HUD after phase 11/12 made the full-width type bars
redundant. The top-5 source lists already show per-row type-colored
segmented bars, so the two full-width aggregated bars (OUT phase 5/8, IN
phase 10) go away. The freed chrome collapses into the panels themselves:
the big OUT/IN dps numbers become the panels' header row, replacing the
small gray `OUT`/`IN` captions. After this phase **everything the HUD
shows lives in the two panels** (plus title buttons and status line),
and the panels are the only content area in both layouts.

Depends on phases 5–12. Pure presentation + chrome; **no Core changes**
(the type-split pipeline stays — the row-bar segmentation in phase 11
consumes the same snapshot data the bars did).

## Changes

- **Removed**: both full-width type bars — the OUT bar (phase 5: segments,
  middle dps numbers, idle pass-through gray) and the IN bar (phase 10:
  same plus red-family border). Their renderers (`SetBar`, the two
  SizeChanged clip handlers) and the `_bar`/`_barIn` segment tables go
  with them. Phase 4/10 type attribution in Core is untouched — it still
  feeds the row bars.
- **Moved**: the big numbers (`OUT 156.6 dps` cyan / `IN 26.7 dps` red,
  FontSize 16 Bold — current size unchanged) move from the shared number
  row / stacked variants into the panel headers:
  - OUT number sits above the OUT rows, left-aligned, replacing the
    small gray `OUT` caption (phase 11).
  - IN number sits above the attacker rows, right-aligned (right-edge
    mirroring, phase 8/12), replacing the small gray `IN` caption.
  - The number's own `OUT`/`IN` prefix keeps the direction labeling, so
    the captions carry no lost information.
  - Header alignment is the SAME in both layouts (inside the panel, so
    ApplyLayout stops switching NumberRow/NumberStack entirely — the
    dual vertical number stack disappears as a concept).
- **Stack** (both layouts): title row → PanelArea (number header + rows
  per panel) → status line. Window height shrinks by the two bar rows
  (~26 px); SizeToContent handles it, bottom-anchored growth rules stay.
- Status line unchanged (other windows + session totals).
- Horizontal: OUT panel left half, IN panel right half — headers and
  rows align as before. Vertical: panels stacked full width, IN panel
  still right-edge mirrored; title text hides, buttons tighten (phase
  12 rules unchanged, minus the number row/stack switching).

## Amendment pointers

- `phase-5-damage-type-bar.md` and `phase-10-incoming-types.md`: the HUD
  bars are removed in phase 13; Core type data remains in use (row-bar
  segmentation). Stack-order lists in `phase-8-hud-attackers-panel.md`,
  `phase-11-out-sources.md`, `phase-12-layout-toggle.md` lose the bar
  entries and gain the number-as-header change.

## Acceptance criteria

- [x] Neither full-width type bar renders; no dead chrome or gaps; the
      window is ~2 bar-heights shorter than the phase-12 build.
- [x] Big numbers render as panel headers in BOTH layouts, at the current
      16 px size: OUT left-aligned, IN right-aligned; clicking the layout
      toggle re-renders correctly in the same tick.
- [x] Numbers keep updating per tick in the selected window (no data
      resets on layout/window switches — phase 5/6 rules).
- [x] Row bars still show per-row type segmentation (Core pipeline
      intact); miss-only dimming, Others merge, idle collapse all hold.
- [x] Replay screenshot verification in both layouts (same session
      fixture as phase 11/12).
- [x] `dotnet build` 0 warnings; `dotnet test` all green (87/87 expected —
      Core untouched); single-file publish runs.
- [x] Specs amended in the same branch (pointers above); `intent.md`
      phase table gains this phase.
- [x] Git workflow: implemented on `phase-13-bars-into-headers`, merged
      to `main` only when green.

## Verification notes (2026-11-15)

- Replay `session-20261107-from-1942` at ×30, both layouts: horizontal —
  no bars, big OUT number left-aligned atop OUT rows, big IN
  right-aligned atop attacker rows, row bars still type-segmented,
  miss-only row present and dimmed; window height shrank (193/206 px
  captures before → 135 px window after). Vertical — title hidden,
  3 tight buttons fit, stacked panels each carry their own number header
  (IN right-mirrored), abbreviated wrapped status, no dead chrome.
- App-launch note for future verification runs: the framework-dependent
  publish exe needs `DOTNET_ROOT` (and `DOTNET_ROOT_X64`) pointed at the
  local SDK dir (`C:\Users\jensj\AppData\Local\dotnet-sdk`) when
  launched directly, or it dies with "You must install .NET" — an
  event-log red herring, not a code crash (cost one false-alarm
  debugging round).

## Out of scope

The damage-type legend (planned phase 14), height stability toggle
(planned phase 15), any Core/attribution change, restoring any
aggregate type visualization.
