# EVE DPS Overlay — project instructions

A tiny, always-on-top Windows overlay that reads EVE Online gamelog files from
disk and shows outgoing and incoming damage-per-second.

## Spec-driven workflow

- The intent of the app lives in `docs/spec/`. `docs/spec/intent.md` is the
  source of truth for scope and non-goals; each phase has a one-page spec with
  acceptance criteria.
- Read the active phase spec **before** implementing anything for that phase.
- Implement only what the active spec's acceptance criteria require. If behavior
  changes in a way the spec does not cover, update the spec in the same change
  so the document never drifts from reality.
- Files in `docs/spec/fixtures/` are REAL EVE log lines copied from the user's
  gamelogs. Never edit them to make a test pass; parse them, or replace them
  with fresh captured lines.

## Verification

Run after every change:

```sh
dotnet build
dotnet test
```

- Parser behavior is only proven against fixtures. Do not claim a change works
  without running `dotnet test`.
- A quick wiring check against the live log directory (no game needed):
  `dotnet run --project src/EveDps.Console -- --smoke`

## Git workflow (user rule)

- Every feature/phase is implemented on its own **feature branch**, never
  directly on `main`. Branch names: `phase-<n>-<short-name>` or
  `feat/<short-name>`.
- **`main` must always be a known-good, buildable, tested state** — the
  user can switch to it manually whenever something goes wrong. Do not
  commit broken or unverified code to `main`, ever.
- Only merge a feature branch back into `main` after its verification is
  green (`dotnet build` 0 warnings, `dotnet test` all passed) and the
  affected specs are updated in the same branch. Merge with
  `git merge --no-ff <branch>` so the history keeps the branch shape, then
  delete the branch.
- Commit small and early while working on a branch; subject lines short and
  imperative ("Add TimeKnown gate to ReplayFeed"). Never leave the user
  without a committed working state at the end of a session — commit
  whatever is complete before finishing, even if the feature isn't.

## Conventions

- Target framework: `net10.0`. Windows-only; do not chase portability.
- `EveDps.Core` stays UI-free: no WPF, no console output, no file dialogs.
  The console app (phase 1) and the WPF app (phase 3) are thin layers on top.
- Nullable reference types are enabled. Prefer records for parsed data.
- Timestamps parsed from log lines are EVE time (UTC). Never use file mtime
  for event timing.
- Keep user-facing console text ASCII.
- Do not commit without the spec status field reflecting reality
  (`draft -> accepted -> done`).
