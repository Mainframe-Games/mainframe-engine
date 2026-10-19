# ADR 0198 — Areas detect areas

- **Date:** 2026-10-20
- **Status:** accepted
- **Milestone:** —
- **Docs:** docs/design/physics.md
- **Supersedes:** the "areas never detect areas" clause of [ADR 0021](0021-collision-layer-semantics.md)

## Context

ADR 0021 ruled out area-vs-area detection because Box2D 3.1 sensors don't detect other sensors, and 3D matched for
parity. Godot games rely on it: Buy My Snake Oil's interaction system is an `Area3D` sensor on the player detecting
`Area3D` interactables (mainframe-engine#53). Without it, every interactable would have to become a body on its own
layer.

## Decision

- **Godot's API and rules** on `Area3D` and `Area2D`: `Monitorable` (default true), `AreaEntered`/`AreaExited`
  signals, `GetOverlappingAreas`, `HasOverlappingAreas` and `OverlapsArea`. Area A detects area B when A is
  `Monitoring`, B is `Monitorable` and `(A.mask & B.layer) != 0`. Each side is one-way, so two default areas report
  each other. Areas still never appear in `BodyEntered` or in `GetOverlappingBodies`.
- **Computed after the step, like the other area overlaps**, and diffed per receiver in a separate dictionary
  (`AreaOverlaps`). The dispatch order is exits, then enters, then sleep changes. Area-vs-area events come after
  body-vs-area events of the same kind.
  - **3D:** areas have no Jitter2 body, so no broadphase entry. Each step computes every area's bounds once. Each
    monitoring area then tests every other area: bounds first, then `NarrowPhase.Overlap` shape by shape. This is
    O(monitoring areas × areas), which is cheap for the dozens a scene has.
  - **2D:** Box2D sensor events still don't see sensors, and the custom filter still never pairs two sensors. Each
    monitoring area runs a `b2World_OverlapShape` per sensor shape and keeps only area shapes. Box2D 3.1's shape
    queries do return sensor shapes, and they test exactly rather than by bounds (checked against Box2D.NET 3.1.654).
- **Exits:** an area that moves away, stops being `Monitorable` or loses its shapes is reported exited after the next
  step. An area that leaves the tree is reported exited immediately, the same rule as bodies. Godot doesn't send
  `area_exited` for a freed area. We do, because a missing exit makes every game prune its own list.

## Consequences

- Scenes that use Godot's sensor/interactable pattern work unchanged.
- The steady-state allocation gates cover overlapping areas in both dimensions.
- There is no per-shape `area_shape_entered`. A space with hundreds of monitoring areas would want a real broadphase
  in 3D, such as adding area shapes to Jitter2's tree as proxies.
