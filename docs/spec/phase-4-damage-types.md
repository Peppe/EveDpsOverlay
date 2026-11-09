---
status: done
---

# Phase 4 — Damage-type attribution (weapon profiles)

Goal: SMART numbers for the HUD type bar (phase 5): outgoing dps split by
**EM / Thermal / Kinetic / Explosive**. EVE's gamelog combat lines do not
contain the damage type (verified fact in `intent.md`), so the type is
**inferred from the weapon name** via a user-curated profile table. This is
inference, never measured data — the UI and docs must not pretend otherwise.

## Rules

- `CombatEvent` stays unchanged: it already carries `Weapon` (weapon name,
  populated for outgoing lines only; incoming lines have no weapon).
- A weapon profile maps a weapon name to a 4-part weight vector
  (EM, Thermal, Kinetic, Explosive). Weights are normalized to sum to 1 at
  load time; names are matched case-insensitively.
- Profiles live in `%APPDATA%\EveDpsOverlay\type-profiles.json` (user-edited;
  the app never writes dynamic state into it). Missing/empty file = the
  feature is inert: every hit lands in the **Unknown** bucket.
- Weapons without a profile fall into an gray **Unknown** bucket treated as
  a fifth type everywhere downstream. Detection of "unmapped weapons exist"
  is reported, not hidden.
- Attribution happens once, when the aggregator `Add`s the event: the queued
  event stores its per-type split. Snapshots stay cheap (sum per type within
  the window, the same pass that computes dps).
- Snapshot gets per-type outgoing dps for each rolling window
  (5/15/60 s), same window math as the existing numbers. Dps per type =
  attributed sum / window length. Session totals stay as-is (no per-type
  session totals until a need shows up).
- Misses have no weapon-derived damage: they contribute shots, never type
  damage (and no "Unknown" damage either).
- `EveDps.Core` stays UI-free; console is the verification surface.

## Console surface

- `--replay` summaries gain one thin line: dedicated per-type dps values at
  the session's last timestamp, plus the Unknown share. Same invariant as
  with `--dps`: rendering stays dumb, math lives in Core.

## Acceptance criteria

- [x] A mapped weapon's damage is split exactly by its profile weights
      (synthetic events, pinned reference time).
- [x] An unmapped weapon lands entirely in Unknown and is detectable
      (`UnmappedWeapons` on the snapshot, session-level distinct names).
- [x] Profiles with non-normalized / partial weights normalize deterministically
      (sum to 1); a file-parse failure falls back to "all Unknown" without
      crashing; all-zero rows are rejected into Unknown.
- [x] Per-type dps values are mutually consistent: sum over the four types
      + Unknown equals the window's total outgoing dps (invariant test).
- [x] Fixture replay verified. The fixture session actually uses TWO
      weapons — `Civilian Gatling Railgun` and `75mm Gatling Rail I` — which
      the independent recomputation handles per weapon. With an empty
      profile table the console readout reads:
      `# types(60s) EM 0.0 TH 0.0 KIN 0.0 EX 0.0 UNK 0.9 |
      unmapped: 75mm Gatling Rail I, Civilian Gatling Railgun`.
      With both weapons mapped, the test verifies per-weapon weighted sums
      match and Unknown is absent.
- [x] `dotnet build` / `dotnet test` stay 0-warning; parser and prior tests
      untouched in behavior. Suite grew 24 → 37 tests, all green.

## Verification (2026-11-08)

- `dotnet build` 0 warnings / 0 errors; `dotnet test` 37/37 (13 new tests,
  all against real serialized profile files in `DamageTypeAttributionTests`).
- Live `--smoke` runs unchanged (no profile file on disk → inert table →
  no crash).
- Implementation note: JSON loading is case-insensitive so hand-edited
  profile files ("profiles" vs "Profiles") work; the same one-line fix went
  into the WPF `HudSettings` reader.
- The two fixture weapons land in the profile table with real values only
  when the user curates them (feature does not invent numbers).

## Out of scope

Incoming type attribution (impossible: no weapon in incoming log lines),
auto-populating profiles from CCP's static data export, HUD editing of
profiles (phase 5 adds an "open the file" shortcut only), per-type session
totals.
