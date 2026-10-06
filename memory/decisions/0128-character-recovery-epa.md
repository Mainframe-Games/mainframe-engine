# ADR 0128 — Character recovery and settling with EPA

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Driving Range Simulator (Godot 4.7) — PM2 / E10 (the game's `docs/porting.md`)

## Context

Porting the Driving Range player (a capsule, radius 0.5, height 2, whose bottom starts exactly on a 125 × 10 × 130
box) to `CharacterBody3D` showed three faults with Jitter2's queries near contact on large shapes:

1. **Stuck at an exact touch.** GJK `Distance` reported "not separated" and `MprEpa` "no penetration", so recovery saw
   nothing, every sweep then started overlapping (no normal) and `MoveAndSlide` stayed put forever: the player never
   moved and gravity piled up in `Velocity`.
2. **Height noise.** Walking, the recovered height jittered between 0 and 3 mm (GJK/MPR error), above Godot's
   steady 0.8 mm with its 1 mm `safe_margin`.
3. **Resting high.** Sweeps stop a few millimetres short; a body that came to rest above two margins (3.7 mm with a
   1 mm margin) was never pulled down, since recovery only pushes out.

The game's parity bar (dump positions within 1 mm of Godot's) needs a body that starts on the floor to walk, and a
resting height within the margin.

## Decisions

1. **Recovery asks EPA first** (`NarrowPhase.Collision`): one signed distance for touching, separated and overlapping
   shapes (negative when apart), its normal pointing from the body to the other shape in every case. `Distance` and
   `MprEpa` remain the fallbacks when EPA does not converge.
2. **Contacts the body is leaving are not classified**: recovery skips a contact whose normal the velocity points
   along (a jump off the floor), so the body is airborne after the take-off step as in Godot, where only the motion's
   collisions set the floor state.
3. **Settle after a floor contact from a sweep or the snap** (`PhysicsSpace3D.SettleBody`): move the body along EPA's
   normal so it sits exactly one margin from that floor, if the gap is within 2 cm of the margin.
4. Defaults are unchanged (`SafeMargin` 1 cm); a game ported from Godot sets Godot's 0.001.

## Consequences

- Tests: `CharacterJumpingOffTheFloorIsNotOnTheFloorAfterTheJumpStep`, `CharacterStartingExactlyOnALargeFloorWalksAway`, `CharacterRestingWithGodotsMarginHoldsItsHeight` (1 mm
  margin: height within 0.2 mm over 2 s, walking and standing). The landing test's floor normal is EPA's (within 1e-5
  of +Y instead of exact).
- EPA costs more than GJK per contact; recovery runs per character per step, which is negligible here (one player).
