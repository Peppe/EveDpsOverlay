---
status: done
---

# Phase 1 — Tail & Parse (console)

Goal: follow the newest EVE gamelog file and print parsed combat events from a
console application. No DPS math, no UI. This phase exists to prove the
data pipeline; phase 2/3 build on it unchanged.

## Components

- `EveDps.Core` — `LogParser`, `GamelogTailer` (UI-free library).
- `EveDps.Console` — thin CLI wrapper: live mode, `--raw`, `--replay <file>`,
  `--smoke`.
- `EveDps.Core.Tests` — unit + integration tests over real fixtures.

## Rules (verified against the user's real logs)

- Open log files with `FileShare.ReadWrite | FileShare.Delete`; poll file
  length on a timer (default 250 ms). Do not use FileSystemWatcher.
- Files are UTF-8/ASCII without BOM, CRLF line endings.
- Combat lines contain EVE markup (`<color=…>`, `<b>`, `<font size=…>`); strip
  markup BEFORE matching, then collapse whitespace.
- Orientation comes from the literal `to` / `from` word in the stripped text;
  color codes are a secondary cross-check only.
- Zero-damage lines (`Your X misses Y completely - X`, `Y misses you completely`)
  are shots: count them, damage 0, never report them as unparsed lines.
- Incoming damage lines may lack a weapon name; outgoing damage lines carry
  one. Per-weapon breakdown is outgoing-only until proven otherwise.
- Parse the in-log timestamp `yyyy.MM.dd HH:mm:ss` (EVE time, UTC) with
  TryParseExact; never derive event timing from file LastWriteTime.
- The followed file is the newest `*.txt` by LastWriteTime that ALSO grows
  after it is first seen. Brief header-only stub files must never become the
  followed file.
- On startup, follow the newest file at its current end (no duplicate lines
  after a restart). A `--from-start` flag opts into reading its backlog.
- Switching to a newly created session file reads that file from the start.
- Non-combat channels (bounty, notify, question, …) and the header block are
  recognized but ignored by the combat parser.

## Acceptance criteria

- [x] Follows a file being appended while running, byte-offset based, partial
      lines buffered until a newline arrives.
- [x] After a restart over an existing file, no previously existing lines are
      re-emitted.
- [x] Switches to a newly created session file within one poll cycle once it
      actually grows; stays on the current file otherwise.
      (Also caught: DirectoryInfo caches Exists — the tailer polls
      Directory.Exists per cycle, added 2026-11-07.)
- [x] Executes the parser against fixtures: markup variants, miss lines,
      bounty/notify channels ignored, bin output ignored.
- [x] `--raw` prints every tailed line; default prints one event per line with
      time, direction, damage, other party, weapon (when known), hit kind;
      `[MISS]` marked for misses.
- [x] `dotnet test` passes (17/17) with zero build warnings.

Verified against real data on 2026-11-07:
`--replay session-full-combat.txt` → 294 events, 591 OUT dmg, 78 IN dmg,
171 misses; `--smoke` follows the live log directory and exits 0.

## Out of scope

DPS math, rolling windows, UI/tray/hotkeys, per-weapon and hit-quality stats,
multi-client support, character filtering via the header's `Listener:`.
