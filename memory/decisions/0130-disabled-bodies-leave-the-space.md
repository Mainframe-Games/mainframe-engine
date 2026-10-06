# ADR 0130 — Disabled bodies leave the physics space

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Driving Range Simulator (Godot 4.7) — PM3 / E11 (the game's `docs/porting.md`)

## Context

Driving Range holds an item by instancing its scene under a marker in front of the camera with
`ProcessMode = Disabled`. In Godot that takes a `RigidBody3D` out of the physics space (`CollisionObject3D.disable_mode`
defaults to `DISABLE_MODE_REMOVE`): the held ball neither falls nor blocks the interaction ray. When the player drops
it, the game reparents it into the world, sets `ProcessMode = Inherit` and applies an impulse straight away. The
engine had `ProcessMode.Disabled` but its bodies stayed in the space, so a held ball fell out of the hand and the ray
hit it.

## Decisions

1. **`Node.OnDisabledChanged(bool)`** (private protected, Godot's `NOTIFICATION_DISABLED`/`ENABLED`): called inside a
   tree when the resolved process mode becomes or stops being `Disabled`, from `ProcessMode`'s propagation. Entering
   and leaving the tree don't call it; they read `ResolvedProcessMode`.
2. **`CollisionObject3D`** joins its space on entering the tree only when not disabled, leaves it when disabled and
   joins again (at the node's current transform) when enabled. Only Godot's default mode (remove) exists; `MakeStatic`
   and `KeepActive` are not needed yet.
3. **`RigidBody3D`** keeps its velocities when it leaves for either reason (tree exit or disable), so an enable or
   re-entry continues from them. Joining is synchronous, so an impulse applied right after enabling is not lost.

## Consequences

- Held items behave as in Godot; nothing changes for nodes that are never disabled.
- Test: `Physics3DTests.DisabledBodiesLeaveTheSpaceAndRejoinWhereTheyAreWhenEnabled`.
- 2D bodies don't follow yet (no game needs it); add the same hook to `CollisionObject2D` when one does.
