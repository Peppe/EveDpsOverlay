# Intent

EveDpsOverlay is a small, always-on-top Windows overlay for EVE Online that
reads the game's gamelog files from disk and shows the player their
damage-per-second — outgoing and incoming — in real time.

Status: the north-star document. Changes to scope belong here, in the same
change that implements them.

## Goals

- Read ONLY the game's own log files while the client appends them. No memory
  reading, no game-process injection, no input automation (EULA-safe by design).
- A tiny HUD: minimal chrome, frameless, semi-transparent, always on top.
  Eventually shows rolling DPS (incoming/outgoing), session totals, top
  shooters/targets.
- Works while EVE runs in windowed/borderless mode (topmost windows are hidden
  behind exclusive fullscreen).
- Everything local. No server, no telemetry, no accounts.
- One self-contained `.exe` in the end state, unsigned is fine.

## Non-goals

- Not a general-purpose log viewer.
- Not a fleet-wide analytics or replay tool (for now).
- Not cross-platform: Windows 10/11 only.
- No multi-client support in v1: the newest active gamelog wins.

## Known environment facts (verified 2026-11-07 on the user's machine)

- Logs live at `%USERPROFILE%\Documents\EVE\logs\Gamelogs\`, possibly OneDrive-
  redirected (always resolve via Environment.SpecialFolder.MyDocuments).
- One file per session: `yyyyMMdd_HHmmss[_charid].txt`; a short header block
  (`Gamelog`, optional `Listener: <character>`, `Session Started:`).
- Files are UTF-8/ASCII without BOM, CRLF line endings.
- Combat lines embed EVE markup (`<color=…>`, `<b>`, `<font size=…>`) that must
  be stripped before parsing; zero-damage "misses completely" lines exist.
- In-log timestamps are `yyyy.MM.dd HH:mm:ss` in EVE time (UTC); file mtime is
  local time and may be hours ahead — never derive timings from mtime.
- Brief header-only "stub" files appear at login and must not become the
  followed file.

### Captured fixtures inventory (REAL log lines; never edited to fit tests)

- `session-full-combat.txt` — 2026-11-01 session, 294 combat events, single
  attacker (`Pithi Arrogator`), two weapons (Civilian Gatling Railgun,
  75mm Gatling Rail I).
- `session-20261107-from-1942.txt` — 2026-11-07 session cut from 19:42:30 at
  the user's request (first kept line 19:42:34; source:
  `%USERPROFILE%\Documents\EVE\logs\Gamelogs\20261107_132814_2124408811.txt`,
  lines 488 onto end). 3,074 combat events, 153,719 OUT / 21,767 IN damage,
  1,081 misses, **18 distinct incoming attackers** (first multi-rat capture —
  fixture for the phase-7/8 attackers panel), 3 outgoing drone weapons
  (Hornet I, Vespa I, Wasp I — currently Unmapped in the type pipeline).
- `curated-lines.txt` — hand-picked line shapes for parser edge cases.
- Combat lines carry NO damage type (verified 2026-11-07 against the
  captured fixtures: zero `EM|Thermal|Kinetic|Explosive` tokens; the trailing
  tag is hit quality — Penetrates / Smashes / Hits / Grazes / Glances Off).
  Any damage-type display must infer type from the weapon name.
- Incoming hit lines contain NO weapon name (attacker + hit quality only),
  so incoming type attribution is impossible from logs — OUT-only feature.

## Milestones

| Phase | Scope | Spec |
|---|---|---|
| 1 | Console tail + parse, verified against real fixtures | `phase-1-tail-parse.md` |
| 2 | DPS aggregation (rolling windows, totals, breakdown) | `phase-2-dps.md` |
| 3 | WPF overlay HUD + tray/hotkey/settings polish | `phase-3-hud-polish.md` |
| 4 | Damage-type attribution via user-curated weapon profiles (console-verifiable) | `phase-4-damage-types.md` |
| 5 | HUD damage-type bar under OUT (phase 4 snapshot data) | `phase-5-damage-type-bar.md` |
| 6 | HUD stage controls: primary-window (default 15 s) + opacity cycle buttons on the HUD | `phase-6-hud-stage-controls.md` |
| 7 | Incoming per-attacker dps pipeline (console-verifiable) | `phase-7-incoming-attackers.md` |
| 8 | HUD incoming attackers panel (top-5 + Others rows) | `phase-8-hud-attackers-panel.md` |
| 9 | Replay mode (virtual clock, paced feed; console + HUD) | `phase-9-replay.md` |

Implementation order: phases ship by number, with one constraint:
**6 (the primary-window selection) must precede 5 and 8**, since both HUD
extensions bind to that selection. Done: 1–6 then 9 (replay as the visual
verification tool), 5 on 2026-11-08, 7 the same day, 8 on 2026-11-09 —
the planned milestone list is complete; backlog items below remain open.

## Deferred backlog (not yet specced; revisit after a real play session)

- Global visibility hotkey (deferred in phase 3; tray Show/Hide covers it).
- Click-through HUD mode (needs `WS_EX_TRANSPARENT` + a way to get input
  back).
- Per-weapon panels / hit-quality stats (parser already captures both).
- Outgoing per-target panel (symmetric counterpart of the phase-7/8
  attackers panel; pipeline mirrors it in Core).
- Auto-prefill weapon profiles from CCP's static data export.
