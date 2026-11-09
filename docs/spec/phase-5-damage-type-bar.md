---
status: done
---

# Phase 5 — HUD damage-type bar

Goal: the requested visualization. A slim, 100 %-wide bar under the
`OUT | IN` row, segmented by damage type, with each segment's dps printed in
its middle. Data source: phase 4's per-type snapshot values for **the HUD's
displayed window** — the primary-window selection introduced in phase 6
(until phase 6 exists, the HUD displays 60 s; see implementation order in
`intent.md`). The bar and the big `OUT` number must never answer for
different windows.

## Visual rules

- Bar spans beneath the `OUT | IN` row (phase 8's compact layout; beneath
  the full-width `OUT` line until that amendment lands). It carries
  **outgoing types only**: incoming log lines carry no weapon name, so
  incoming type attribution is impossible — a documented data fact, not a
  TODO.
- Five fixed-order segments: EM, Thermal, Kinetic, Explosive, Unknown. A
  zero-dps type occupies zero width; an empty pass-through (no dps at all,
  e.g. idle) shows as one neutral full-width gray bar.
- Segment colors follow EVE-informed conventions and reuse the HUD palette:
  EM blues/purples, Thermal red, Kinetic neutral gray, Explosive orange,
  Unknown muted gray. Implemented palette (segment fill / number tint):
  EM `#4d7cff` / `#bfd2ff`, Thermal `#ff5a4e` / `#ffc9c4`, Kinetic
  `#9aa3ad` / `#dde2e8`, Explosive `#ff9854` / `#ffd8b4`, Unknown
  `#565d66` / `#a7adb5`; idle pass-through bar `#3a414b`.
- Bar height 9 px inside the existing border padding; the segment grid is
  rounded (radius 3) via a RectangleGeometry clip that tracks SizeChanged.
- Per-segment number: centered, "0.#" dps format, tinted by the segment
  color; only rendered when the segment is at least ~30 logical px wide
  (else the number is dropped, the segment still shows). Hard rule: never
  overlap text between adjacent segments.
- The bar renders inside the existing 200 ms DispatcherTimer tick from the
  SAME `DpsSnapshot` the text lines use — no extra aggregation, no thread
  changes, no disk I/O on the UI thread.
- Drag, right-click menu, opacity, tray behavior all unaffected (the bar is
  part of the drag surface, `IsHitTestVisible` stays true).
- Right-click menu gains one item: **Open profiles file** (`Process.Start`
  on `type-profiles.json` in the default editor) to make mapping weapons a
  one-click workflow. Phase 4 delivers the file itself.

## Acceptance criteria

- [x] Bar segment widths are proportional to per-type dps of the CURRENT
      displayed window and always sum to exactly 100 % of bar width:
      implemented as star-length Grid columns sized to each type's dps share
      (zero-dps types get 0 px), so the stars sum to the total by construction.
- [x] Per-segment numbers match the snapshot's per-type dps values; numbers
      hide on segments narrower than 30 logical px.
- [x] Switching the primary window re-bases the bar with every other
      window-driven element (the bar reads the same `WindowOf` selection;
      `ApplyPrimaryWindow` already re-renders synchronously).
- [x] Brightness/opacity respect the existing window `Opacity` setting for
      free (they inherit it; no per-bar opacity exists).
- [x] Showcase/live verification: cross-checked against the console
      `--replay` of `session-full-combat.txt` with the same profiles file
      (`%APPDATA%\EveDpsOverlay\type-profiles.json`): mid-replay screenshot
      showed TH/KIN segments with proportional widths and centered tinted
      numbers summing to the OUT figure; idle tail showed `OUT 0 dps` with
      the one neutral full-width gray bar and `IN —`. Zero-dps types
      (EM/EX/Unknown with these profiles) rendered at zero width.
- [x] Drag area, right-click menu, opacity and tray untouched; "Open
      profiles file" added after the Opacity submenu (seeds an empty
      template file first if missing, then opens the default editor).
- [x] `dotnet build` 0 warnings, `dotnet test` all green (Core untouched in
      this phase; 41/41).

## Verification notes (2026-11-08)

- Visual verification done on-screen via the published exe replaying
  `session-full-combat.txt` at ×15 with screenshots (live HUD over EVE):
  populated segments, segment numbers, and the idle pass-through bar all
  observed. Rounding note, accepted: the big OUT number and the segment
  numbers format "0.#" independently, so displayed values can differ from
  their exact sum by ≤ 0.1 (e.g. 0.85 + 0.85 shows as 1.7 vs 0.9 + 0.9).
  The underlying doubles are the snapshot's values; the bar widths are
  always exact.
- Drone weapons (Hornet I, Vespa I, Wasp I) from
  `session-20261107-from-1942.txt` are Unmapped until the user curates
  profiles; with no profile file the whole bar shows the Unknown segment.

## Out of scope

Type bar under `IN` (no data source), animation of segment transitions,
editing profiles inside the HUD (Notepad shortcut only), history sparkline.
