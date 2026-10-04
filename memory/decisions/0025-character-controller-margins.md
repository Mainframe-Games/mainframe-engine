# ADR 0025 — Character controller: recover-then-sweep with a safe margin (1 cm in 3D)

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M6

## Context

`MoveAndSlide` is engine code (neither library has a character controller). Measured on Jitter2 2.9.0: shape sweeps
report "already overlapping" (fraction 0, no normal) when shapes are within ~2–4 mm, and resolve hit distances to
~1–2 mm (reporting slightly early); distance queries are ~1 mm accurate. Godot's default 1 mm margin therefore gets
a 3D character stuck on the floor. Box2D's casts are exact.

## Decision

- **Recover first**: everything within two `SafeMargin`s counts as a contact (that's how a resting or slope-walking body
  stays "on floor" without flicker); anything closer than one margin is pushed back out to it (Jitter2
  `NarrowPhase.Distance`, else `MprEpa`; Box2D manifold functions, which report penetration and nearby points).
- **Then sweep** each shape along the motion extended by the margin; stop a margin short; slide; when a sweep says
  "touching" without a normal, take the normal from a distance/penetration query.
- Defaults: `CharacterBody3D.SafeMargin` = **0.01 m**; `CharacterBody2D.SafeMargin` = 1 px (= 1 cm at 100 px/m).
  `FloorSnapLength` 0.1 m / 1 px, `FloorMaxAngle` 45°, `MaxSlides` 4, `FloorStopOnSlope` on. On the floor, vertical
  velocity gained by sliding is dropped (a jump is kept).

## Consequences

- Characters hover ~1 cm above surfaces (invisible at typical scales); tiny characters should lower the margin.
- No step climbing yet (only snapping down); constant speed on slopes is not offered.
