---
status: done
---

# Phase 9 — Replay mode (virtual clock, paced feed)

Goal: verify the HUD against captured sessions without the game — replay an
old fixture as if it were a live session, in the console and in the overlay.
This is also the eyeball-tool for the type bar (phase 5) and the attackers
panel (phase 8).

## Rules

- **Virtual clock.** Live mode keeps using the wall clock. Replay mode must
  drive the aggregator from the stream's own time: `Take` reference = the
  replayed events' current log timestamp (`CurrentVirtualTime`), so old
  sessions land correctly inside the 5/15/60 s windows. This is the core of
  the feature; getting it wrong means zero numbers forever.
- `EveDps.Core` gains `ReplayFeed` (UI-free, testable like every engine
  piece): reads a fixture file, emits `TailedLine`s on a background task,
  paced by in-event log gaps ÷ speed. Idles longer than a cap (default 10 s
  of log time) hold only for the cap ÷ speed instead of their full length —
  an hour of warp-jumping never stalls the replay.
- Pacing anchors on each line's own timestamp; header and non-timestamped
  lines emit immediately. Line shape is exactly `TailedLine` — hosts parse
  it through the same path as tailer lines.
- `CurrentVirtualTime` update/reads may race (feed task vs UI thread) and
  must be safe (interlocked ticks), always monotonic within one replay.
- Completion raises a `Finished` event. Console: exits, prints the final
  summary like every other mode. HUD: status line says `replay finished`;
  numbers decay naturally; tray/lifecycle unaffected; process stays alive.
- HUD startup arg `--replay <file> [--speed N]` (console: `--replay-live
  <file> [--speed N]`). Missing file: console exits 2; HUD falls back to
  live tailing with a one-line status notice. Replay args are never
  persisted to settings, and the real tailer never starts in replay mode —
  no writes anywhere near the live log directory.
- Default speed ×60 (an hour-long session ≈ 1 wall minute). Use low speeds
  to watch decay behavior.

## Acceptance criteria

- [x] Core feed tests: line ordering preserved, `CurrentVirtualTime` equals
      the last event's timestamp after natural completion, idle-gap cap math
      verified with a 10-minute in-log gap held as 1 s (capital trimmable).
- [x] Console `--replay-live` runs to natural completion, prints the window
      + types + summary block at the final virtual time, exits 0 (verified:
      294 events / session 591-78 / 171 misses, identical to batch replay;
      final windows legitimately zero — see note below).
- [x] HUD `--replay <fixture>` runs the paced feed with the virtual clock
      (published exe launched, alive past 15 s at ×120, killed; full visual
      confirmation next session like phases 3/6).
- [x] Live paths unchanged: `--smoke`, live tail, HUD without args behave
      exactly as before (suite grown to 41, all green).
- [x] `dotnet build` 0 warnings; `dotnet test` green; single-file publish
      re-verified.

## Verification (2026-11-08)

- `dotnet build` 0/0; `dotnet test` 41/41 (4 new ReplayFeed tests).
- Console replay of `session-full-combat.txt` at ×60 and ×600 both complete
  and exit 0. Note observed and accepted: the fixture's last COMBAT event is
  17:49:39 but the file continues to 17:54:34 (a "log off?" prompt).
  Batch `--replay` pins the final snapshot at the last combat event (windows
  populated); `--replay-live`'s virtual clock runs to the file's end, so the
  ~5 idle minutes decay the windows to zero while session totals stay
  intact — the same view a pilot would have had live. Both are per-spec.
- HUD replay: `--replay <file> [--speed N]` parsed in `App.OnStartup`; the
  virtual clock drives the 200 ms snapshot loop; missing replay file falls
  back to live tailing with a status notice. Replay args never persisted.

## Amendment (2026-11-09): TimeKnown gate

Phase-8 verification caught a real race this spec's rule ("reads may race
and must be safe") had left open: `CurrentVirtualTime` is epoch-zeroed until
the feed's first timestamped line is processed, and `Take` against it
overflows `DateTime` (60 s window subtraction below `DateTime.MinValue`),
which killed the HUD mid-replay. Fixes:

- `ReplayFeed` gains `TimeKnown` (true once the first timestamped line is
  processed; stays false for files with no timestamped lines at all).
- Hosts gate aggregation on it: the HUD's tick and the console's renderer
  skip `Take` until `TimeKnown`; the console's final snapshot falls back to
  the wall clock for stamp-less files. Two ReplayFeed tests cover the flag.

## Out of scope

Scrub/pause/loop UI in the HUD, capturing NEW fixtures via replay (new
fixtures come from real sessions), replaying whole directories, audio.
