# ADR 0018 — Remove Box3d/Quad; upgrade removed node types on load

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M3 (W3 lane m3b)

## Context

`ShapeBase`, `Box3d` and `Quad` each built a pipeline per instance and drew a flat colour. M3 replaced them with
`MeshInstance3D` + primitive meshes + `StandardMaterial3D`. Two options:

- keep them as thin compatibility wrappers;
- remove them and migrate every usage.

`[SerializedVersion]` migrations can only rewrite the properties of a type that still exists.

## Decision

- Remove the three types. Port the Sandbox (`SpinningBox : MeshInstance3D`), the render-test scenes and the tests,
  and regenerate `Sandbox.mscene`.
- Add `RemovedNodeTypes`: a scene entry whose type is unknown but registered as removed is upgraded into its
  replacement node. The upgrade consumes the properties it translated from a `PropertyBag`; the rest (transform,
  `CastShadows`, …) apply as usual.
  - `Box3d` becomes a `MeshInstance3D` with a `BoxMesh` and a material with its colour.
  - `Quad` becomes a `MeshInstance3D` with a `QuadMesh` with `FlipFaces` (the old quad faced −Z) and a material
    with `CullMode.Disabled`.
- Games can register upgrades for their own retired types.
- Related fix: `MissingNode`/`MissingResource` now keep the resources their raw properties reference, re-keyed on
  save. Before, a game type that was not loaded dropped its inline resources when its scene was re-saved.

## Consequences

- The old per-instance pipeline path is gone; there is one way to draw meshes.
- Old scenes load and render the same: the lit-shapes, multi-light and Spine goldens are unchanged. Saving writes
  the new types.
