# EveDpsOverlay

A tiny, always-on-top Windows overlay for EVE Online that reads the game's own
gamelog files and shows outgoing/incoming damage-per-second.

**Spec-driven:** the app's intent and per-phase acceptance criteria live in
`docs/spec/` (`intent.md` + one page per phase). Agents (OpenCode) preload them
via `AGENTS.md` + `instructions` in `opencode.jsonc`. Git: features ship on
feature branches, `main` always holds the last known-good state (see
`AGENTS.md`).

## Current status: phases 1–10 done (2026-11-09); backlog in intent.md

```sh
dotnet build
dotnet test                       # parser + tailer + aggregator over real fixtures

dotnet run --project src/EveDps.Console -- --dps         # live DPS in the console
dotnet run --project src/EveDps.Wpf                      # the overlay HUD itself

dotnet publish src/EveDps.Wpf -c Release -r win-x64 -p:PublishSingleFile=true --self-contained
# -> src/EveDps.Wpf/bin/Release/net10.0-windows/win-x64/publish/EveDps.Wpf.exe
```

### Overlay usage

- A small cyan `OUT` / red `IN` DPS meter appears bottom-right, always on top.
- Below the type bar, up to five **attackers rows** (top-5 incoming dps +
  `Others`) show who is shooting you and how hard; miss-only attackers
  render dimmed with a miss count. From your real combat data (phase-8
  replay-verified on the 2026-11-07 ratting session).
- **Phase 10**: a second, red-bordered type bar under the first one shows
  the *incoming* damage types — inferred two-tier (weapon profiles where
  the log names the weapon, per-attacker profiles for turret hits).
- Left-drag moves it (position is remembered), right-click opens a menu
  (Hide / Opacity 40–100 % / Open profiles file / Exit). A tray icon is
  always present — that is how you get the window back or quit (a frameless
  window has no close button).
- Settings live in `%APPDATA%\EveDpsOverlay\settings.json`; weapon damage
  types are user-mapped in `%APPDATA%\EveDpsOverlay\type-profiles.json` and
  — for weaponless incoming damage (phase 10's attacker tier) — rat names in
  `%APPDATA%\EveDpsOverlay\attacker-profiles.json`; missing files = those
  parts show Unknown. Right-click has open-menu items for both.
- **Run EVE in windowed or borderless mode** — in exclusive fullscreen the
  overlay (like any topmost window) is not drawn on top of the game.

Options: `--raw` (print every tailed line), `--from-start` (read backlog),
optional `[<logdir>]` positional override; default log dir is
`Documents\EVE\logs\Gamelogs`.

### Replay an old session (don't launch the game)

Watch a captured session "live" — the overlay/console replays a fixture
file at speed ×N (default ×60), numbers move and decay like the real thing:

```sh
# the 2026-11-07 ratting session (from 19:42, ~3,074 combat events):
dotnet run --project src/EveDps.Console -- --replay-live docs/spec/fixtures/session-20261107-from-1942.txt --dps --speed 60
dotnet run --project src/EveDps.Wpf      -- --replay      docs/spec/fixtures/session-20261107-from-1942.txt --speed 60

# the 2026-11-01 single-rat session:
dotnet run --project src/EveDps.Wpf      -- --replay docs/spec/fixtures/session-full-combat.txt --speed 20
```

Use a lower `--speed` (10–20) to watch window decay behavior closely.

## Lay of the land

- `src/EveDps.Core` — `LogParser` (markup stripping, combat events) and
  `GamelogTailer` (polling, byte offsets, session-file adoption). UI-free.
- `src/EveDps.Console` — phase 1 CLI.
- `src/EveDps.Core.Tests` — fixtures are REAL lines from the user's gamelogs;
  do not edit them.
- Phase 2 = DPS aggregation, phase 3 = the WPF HUD. See `docs/spec/`.

Note for gamers: EVE must run windowed/borderless — an exclusive-fullscreen
game hides topmost windows.
