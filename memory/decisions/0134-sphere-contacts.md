# ADR 0134 — One exact contact for spheres

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Driving Range Simulator (Godot 4.7) — PM3 / E12 (the game's `docs/porting.md`)

## Context

Driving Range's golf balls (4.46 cm spheres) fall 0.94 m onto a 125 m ground box at start-up. In Godot they come to
rest. On the engine every one of them spun up about −X, rolled away in −Z at a steadily growing speed (0.23 m/s
after 4 s) and slowly sank, so the player's aim missed the ball. With friction 0 the ball stayed in place but spun ever
faster; a ball placed at rest stayed put. The rolling came from a constant off-centre normal force.

**Tracking it down:**

- **Contact point and normal from MPR:** pinning the sphere's contact point to its centre line, and making the
  sphere-box normal analytic, changed nothing.
- **Stored contacts:** clearing them before each step changed nothing either.
- **The actual cause:** the solver's anchor on the sphere was 0.446 mm (1 % of the radius) ahead of its centre on
  every step. With `World.EnableAuxiliaryContactPoints` (Jitter2's default), the narrow phase doesn't register the
  filtered point: `CollisionManifold.BuildManifold` samples support points around the normal, and on a sphere those
  land off the centre line.

## Decisions

1. **`SphereContactFilter` replaces `World.NarrowPhaseFilter`** and chains to the filter it replaces (Jitter2's
   `TriangleEdgeCollisionFilter`).
   - For every overlapping pair with a sphere, it registers one contact with `World.RegisterContact`, with the
     sphere's point at centre ± radius × normal, and returns false, so Jitter2 builds no manifold for that pair.
   - Speculative contacts (negative depth) and spheres under a non-uniform transform are left to Jitter2.
2. **Sphere against box is computed exactly:** the closest point on the box gives the normal, both points and the
   depth (the analytic test Godot uses). With the centre inside the box, Jitter2's EPA result stands. Against other
   shapes, Jitter2's normal and the other shape's point are kept.
3. **Jitter2's normal convention:** it passes the normal from shape A to shape B, although its XML documentation says
   B to A. This was checked on a box A under a sphere B, which gives +Y.

## Consequences

- **Spheres rest and roll like Godot's.** In the Driving Range throw, the ball's path stays within 2.5 cm of Godot's
  after 4 s of flight and roll.
- A sphere pair has one contact point, which is all a sphere ever has. Stacks of spheres are unaffected.
- **Tests:** `Physics3DTests.ASmallBallDroppedOnALargeBoxComesToRestWithoutSpinningAway` and
  `ABallRollingOnABoxKeepsItsLineAndSlowsDown`.
- **Known issue:** bodies still settle up to 1 cm into what they rest on, because Jitter2's `AllowedPenetration` is a
  constant.
