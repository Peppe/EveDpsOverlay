---
status: done
---

# Phase 6 — HUD stage controls (primary-window & opacity cycle buttons)

Goal: put two control decisions the user makes most often directly on the
HUD instead of in menus: (1) which window length feeds the big `OUT`/`IN`
numbers, cycled with one click; (2) the HUD's overall opacity, cycled the
same way. WPF-only phase: **no `EveDps.Core` changes at all** — the
aggregator already computes every window; the HUD only picks which one is
prominent.

## Behavior rules

### Primary DPS window

- Default is **15 s** (was 60 s) for the big `OUT` and `IN` numbers on first
  run. The other two windows (from the fixed set 5/15/60) stay in the small
  bottom status line, outgoing-only, as today.
- The small status line always shows **every non-primary window**'s outgoing
  dps, ascending. (Primary = 60 s ⇒ 5s + 15s shown, i.e. exactly today's
  layout; primary = 15 s ⇒ 5s + 60s shown.)
- A cycle button (label = current choice, e.g. `15s`) advances the primary
  window through 5 → 15 → 60 → 5. The choice persists across restarts
  (`PrimaryWindowSeconds` in `settings.json`; invalid values snap back to
  15).
- **This selection is the HUD's single displayed-window control**: the type
  bar (phase 5) and the attackers panel (phase 8), when they exist, bind to
  the same value — every window-driven element answers for the same window,
  and one click re-bases them all.

### Opacity

- Opacity values are unified into one ring used **everywhere** — cycle
  button, tray menu, right-click menu: `100 % → 85 % → 60 % → 40 % → 100 %`.
  Default stays 85 % (the phase-3 "~85 %" range narrows to exactly this).
  This supersedes phase 3's 40/60/80/100 list; phase 3's doc is updated in
  this phase's implementation.
- The cycle button (label = current value, e.g. `85%`) advances the ring in
  the order above — each click dims one step, then wraps to 100 %.
- Values restored from legacy settings files (e.g. 0.80) are legal states;
  the first click rounds *down* to the next ring step below the current
  value (0.80 → 0.60).

### Interaction & placement

- Both buttons sit on the title row, right-aligned; title stays left.
- Buttons intercept their own clicks (`ButtonBase` marks the mouse-down
  handled), so **dragging the window works everywhere except the two button
  rects (~30×24 logical px each)** — a documented trade, not a bug.
- ToolTips on hover: "Primary DPS window (click to cycle 5/15/60)" and
  "HUD opacity (click to cycle)".

## Visual rules

The user requirement verbatim: not too strongly styled; a light border is
enough. Rules that enforce it:

- Custom minimal `ControlTemplate` replaces the default WPF button chrome
  (gradient/hover-slab looks too heavy on a transparent HUD).
- Background: transparent. Border: 1 px light translucent stroke (~30 %
  white), corner radius 3. Font: ~10 px, muted gray (`#68707c`).
- Hover/focus feedback: border brightens to ~50 % white — a border change
  only, never a filled background.
- Width fits the label; no fixed oversized hit area.

## Acceptance criteria

- [x] First run shows 15 s as the primary window; small line shows 5s + 60s.
- [x] Cycle button advances the primary window; big `OUT`/`IN` and the small
      line react in the same tick (ApplyPrimaryWindow refreshes immediately);
      choice survives restart (`PrimaryWindowSeconds` persisted; invalid
      values restored as 15).
- [x] Opacity cycle button walks the ring and persists; tray and right-click
      menus offer the same four values with the current one checked.
- [x] Buttons match the visual rules (custom ControlTemplate; transparent,
      1 px ~30 % white border; border-only hover feedback) — user eyeball at
      both ends of the ring still pending on the next play session.
- [x] Drag from anywhere outside the two buttons still moves the window;
      right-click menu still opens over the title row.
- [x] `dotnet build` 0 warnings; `dotnet test` all green (37/37, Core
      untouched); single-file publish re-verified with the published exe
      launched and alive past startup.

## Verification (2026-11-08)

- `dotnet build` 0/0; `dotnet test` 37/37; Release single-file publish + a
  4 s alive check of the published exe.
- The un-used WPF project builds away cleanly with the new
  `HudButton` style; both buttons render on the title row, right-aligned,
  with ToolTips.
- Machine-observed: the user's live `settings.json` held a legacy 0.80
  opacity — documented legacy handling (first cycle click rounds down to
  0.60) is in place and exact-ring values check-marked.
- Awaiting live confirmation (next play session like phase 3): visual
  overlay reading at 85 % and 40 %, button interplay vs drag in real
  combat, resize-free cycling while tails run.

## Out of scope

Any per-window layout beyond selecting the primary (no resizable HUD, no
window-config presets), menu reorganization, hotkey equivalents of the two
buttons (backlog), redrawing the small status line as a second bar.
