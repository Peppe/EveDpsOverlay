---
status: done
---

# Phase 14 — Damage-type legend

Goal: after phase 13 removes the full-width type bars, damage-type
colors appear only inside the tiny per-row bar segments with no
anywhere to decode them. A static legend bar gives every player a
permanent color key: five segments, one per damage type, each labeled
with its abbreviation the way the old bars wrote dps numbers inside
segments.

Depends on phase 13 (the freed stack it slots into). Pure presentation;
no Core changes.

## Legend bar

- One horizontal bar, **five equal-width segments**, always all five
  visible (static color key — segment widths never change with dps).
  Proportional sizing was considered and rejected: zero-dps types would
  vanish, defeating the key's purpose.
- Segments in the canonical `DamageTypes.All` order with the exact row-bar
  colors: **EM `#4d7cff`** (blue) · **TH `#ff5a4e`** (red) · **KIN
  `#9aa3ad`** (gray) · **EXP `#ff9854`** (orange) · **UNK `#565d66`**
  (dark gray).
- Each segment carries its abbreviation as in-segment text (FontSize 8
  SemiBold, same styling family as the old bar labels): `EM / TH / KIN /
  EXP / UNK`.
- Placement: **above the status line** (panels → legend → status); spans
  the full window width in both layouts.
- Vertical layout (156 px): same labels at FontSize 8 — five ~31 px
  segments must fit the abbreviations; verified by screenshot, font
  shrunk only if it genuinely clips. No layout-specific labels.
- Always rendered (idle included); never bound to snapshot data.

## Hide toggle

- Fourth button in the title row (next to 5/15/60, opacity, layout).
  Same `HudButton` style; content/tooltip shows state (e.g. key glyph or
  `key`/`no key`); clicking hides/shows the legend row instantly.
- Persisted in `settings.json` (e.g. `Legend` bool, default visible),
  same write-on-change rules as the other toggles.
- Title-row width note: five tight buttons at the 156 px vertical width
  get a legibility screenshot check (phase-12 verification precedent);
  if they crowd, shrink to shorter glyphs rather than dropping state.

## Amendment pointers

- `phase-13-bars-into-headers.md`: stack gains the legend row above the
  status line. `phase-12-layout-toggle.md`: title row button count and
  the vertical button-row fit note.

## Acceptance criteria

- [x] Legend renders in both layouts with the five exact colors, equal
      widths, labels legible (screenshot at 156 px proves vertical fit).
- [x] Hide toggle shows/hides instantly; state persists across restart
      via settings.json; default is visible on fresh settings.
- [x] No snapshot dependency: legend identical idle vs combat; row bars
      and panels unaffected.
- [x] `dotnet build` 0 warnings; `dotnet test` all green; single-file
      publish runs.
- [x] Specs amended in the same branch; `intent.md` phase table gains
      this phase.
- [x] Git workflow: implemented on `phase-14-damage-legend`, merged to
      `main` only when green.

## Verification notes (2026-11-15)

- Replay `session-20261107-from-1942` at ×30: horizontal frame shows the
  legend above the status line — EM blue / TH red / KIN gray / EXP
  orange / UNK dark-gray, equal segments, labels legible; the fourth
  title button (▦) sits next to the layout toggle. Vertical frame: all
  five labels fit the ~31 px segments at FontSize 8 with NO font
  shrink needed; four tightened buttons still fit the 156 px title row.
- Hidden path verified via settings (`"Legend": false` → restart):
  legend row absent (window 245 px → 233 px with legend), glyph dimmed.
  The app itself serializes `Legend` on any settings save (observed
  `Legend: true` written by a layout-toggle save before any legend
  click — default-on serialization confirmed).
- Toggle glyph state: bright `#9aa3ad` when shown, near-background
  `#3a414b` when hidden.

## Out of scope

Clickable legend segments (filtering/highlighting), tooltips with dps
splits, per-panel legends, proportional widths.
