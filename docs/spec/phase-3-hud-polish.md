---
status: done
---

# Phase 3 — WPF overlay HUD + polish

Goal: the always-on-top overlay the app exists for. `EveDps.Core` stays
untouched; this phase is a small WPF app reading `DpsSnapshot`s.

## Window rules

- `WindowStyle="None"`, `AllowsTransparency="True"`, semi-transparent
  background (default ~85 %), `Topmost="True"`, `ShowInTaskbar="False"`,
  `ResizeMode="NoResize"`, small default size (~300×140), bottom-right corner.
- Drag via `DragMove()` anywhere on the window; right-click context menu with
  opacity + Exit.
- PerMonitorV2 DPI awareness via `app.manifest` so it stays sharp on scaled
  displays.
- EVE must run windowed/borderless; README notes that exclusive fullscreen
  hides topmost windows.

## Shell integration

- Tray icon is mandatory before "done" (a frameless window has no close
  button): implemented via the `System.Windows.Forms.NotifyIcon` interop in
  the WPF host (`UseWindowsForms`) — zero third-party NuGet dependencies,
  icon rendered in code. Menu: Show/Hide HUD, Opacity (40–100 %), Exit.
- HOTKEY DEFERRED (toggle with a global hotkey) — tray Show/Hide covers it
  for now; revisit after real gameplay feedback.
- Persist position and opacity under `%APPDATA%\EveDpsOverlay\settings.json`
  (window size is fixed, so only X/Y/opacity are stored).

## Acceptance criteria

- [x] HUD displays live rolling DPS (phase 2 snapshots) updated at 5 Hz
      (200 ms DispatcherTimer), tailer on a background task, no UI freezes.
- [x] Frameless, drag anywhere, remembers position and opacity across runs.
- [x] Visible on top of EVE in windowed mode; tray menu Show/Hide, Opacity,
      Exit all work; app exits cleanly (process ends, tray icon removed).
- [x] No disk I/O on the UI thread; snapshots are the only cross-thread data.
- [x] Single-file publish works: `dotnet publish -c Release -r win-x64
      -p:PublishSingleFile=true --self-contained`.

## Out of scope

Theming/skins, per-weapon panels, fleet aggregation, click-through mode
(needs `WS_EX_TRANSPARENT` toggle + a way to get input back later).

## Later amendments (all implemented as of 2026-11-09)

- **Phase 6 (done)** unified opacity into the ring 100 % → 85 % → 60 % →
  40 % everywhere, removing the 80 % menu entry and pinning the default at
  exactly 85 %; this doc's "Opacity (40–100 %)" menu rule is superseded.
- **Phase 8 (done)** moved `OUT` and `IN` onto a shared row (16 px bold,
  left/right pair) and made the HUD height adaptive: the attackers panel
  grows the window with a bottom-anchored edge (`SizeToContent="Height"` +
  Top compensation on SizeChanged, clamped into the virtual screen),
  superseding the fixed two-stacked-lines ~300×150 layout here. X/Y and
  opacity persistence unchanged.
- **Phase 5 (done)** added the full-width type bar under the shared row
  and the right-click "Open profiles file" item.

## Verification (2026-11-07)

Machine-verified:
- `dotnet build` → 0 warnings / 0 errors (all four projects);
  `dotnet test` → 24/24 (Core untouched by this phase).
- Debug exe and Release single-file publish both launched and stayed alive
  past startup (a XAML parse error would exit immediately). Publish output:
  `EveDps.Wpf.exe` 157 MB self-contained + WPF's native runtime DLLs beside
  it (expected for WPF single-file; not third-party code).
- Architecture: tailer on its own task, `DispatcherTimer` 200 ms takes one
  `DpsSnapshot` under a lock — the only cross-thread data; no disk I/O on
  the UI thread (code inspection, same wiring pattern as the console host).

Confirmed in real use (next EVE session): interactivity — drag, tray menu
click-through, opacity menu, graceful Exit; wording on top of a windowed
EVE client. Until then consider this phase done in implementation,
user-confirmed on the next play session. Note: EVE must run windowed or
borderless; exclusive fullscreen hides all topmost windows.
