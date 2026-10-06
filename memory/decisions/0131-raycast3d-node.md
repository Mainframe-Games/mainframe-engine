# ADR 0131 — RayCast3D node and rays that start inside a shape

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Driving Range Simulator (Godot 4.7) — PM3 / E3 (the game's `docs/porting.md`)

## Context

Driving Range finds what the player looks at with a `RayCast3D` under the camera (0.5 m to 3.5 m ahead,
`exclude_parent = false`, `collide_with_areas = true`) and reads `get_collider()` every frame. Looking steeply down,
the ray starts inside the player's own capsule; Godot skips such shapes (`hit_from_inside` defaults to false), while
the engine's `RayCast` reported them at fraction 0 with a zero normal. The engine had no ray node.

## Decisions

1. **`PhysicsDirectSpaceState3D.RayCast(..., bool hitFromInside = true)`**: false adds a cached Jitter2 post-filter
   that rejects results with a zero normal (Jitter2's marker for "origin inside the shape"), so the search continues
   to the next body. The default keeps today's behaviour.
2. **`RayCast3D : Node3D`** with Godot's names and defaults (`Enabled`, `TargetPosition` (0, −1, 0), `CollisionMask`,
   `ExcludeParent` true, `CollideWithBodies`, `CollideWithAreas`, `HitFromInside` false; `IsColliding`, `GetCollider`,
   `GetColliderShape`, `GetCollisionPoint`, `GetCollisionNormal`, `ForceRaycastUpdate`). It casts in its own
   `OnPhysicsProcess`, so in tree order after the nodes before it moved — Godot's internal physics process runs
   interleaved with `_physics_process` in tree order too.
3. **Limits:** areas are never reported (the engine's queries don't see areas yet), so `CollideWithAreas` is stored
   only; only the parent can be excluded (no `add_exception` list); no debug shape.

## Consequences

- Godot scenes with a `RayCast3D` convert one to one (godot2mf maps the node).
- Tests: `Physics3DQueryTests.RayCastFromInsideAConvexShapeHitsItAtTheOriginOrSkipsItLikeGodot`,
  `RayCastNodeUpdatesEachPhysicsStepFromItsGlobalTransform`.
- A subclass overriding `OnPhysicsProcess` must call the base, or the ray stops updating.
