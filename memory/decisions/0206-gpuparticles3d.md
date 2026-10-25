# ADR 0206 — GpuParticles3D: a CPU simulation and a renderer of its own

- **Date:** 2026-10-25
- **Status:** accepted
- **Milestone:** G6.3 (Crash Site Defense's move to 3D, issue #120)
- **Docs:** docs/design/particles-3d.md, docs/design/future/rendering-features.md#3d-particles

## Context

Crash Site Defense is moving to full 3D, and much of its mood is particles: campfire flames, embers and smoke, wreck smoke
and sparks, dust, chips, weather (acid rain, spores, dust storms), glowing spores at night. The engine had only
`GpuParticles2D` (ADR 0187), simulated on the CPU despite its name. The G6 plan put 3D particles on the mesh batcher:
a simulation shared with 2D, particle instances as `MeshInstanceData` spans, and `BillboardMode` as a pipeline
specialization of the mesh shaders. While this was built, the mesh batcher was also being changed for skinned meshes
(#119) and spatial shaders (#121). The engine has no compute pipelines (G6 lists GPU simulation as a non-goal until M11).

## Decision

- **`GpuParticles3D`** with Godot's API: `Amount`, `Lifetime`, `OneShot`, `Explosiveness`, `Randomness`, `Emitting`,
  `LocalCoords`, `SpeedScale`, `FixedFps` + `Interpolate`, `Preprocess`, `AmountRatio`, `VisibilityAabb`, `DrawOrder`,
  `UseFixedSeed`/`Seed`, `DrawPass1`, `MaterialOverride`, `Restart()`, `EmitParticle()`, `Finished`. It derives from
  `VisualInstance3D`, not `GeometryInstance3D`, so the batcher never sees it. It is a `[Tool]` node so the editor previews
  it.
- **Simulated on the CPU** like the 2D emitter, in a sibling `ParticleSimulation3D` (the plan's fallback): the 2D
  emitter keeps its random stream and results untouched. The 3D schedule is Godot's (per-cycle randomness), steps are
  fixed (`FixedFps`, default 30) and interpolated, and random numbers come from an allocation-free PCG32.
- **One `ParticleProcessMaterial`** for both, moved to its own file, gaining ring emission, `Flatness`,
  `ColorInitialRamp`, hue variation and turbulence (value-noise flow, frame-rate independent influence). The material's
  2D default gravity reads as Godot's 3D default in a 3D emitter.
- **A renderer of its own**, `ParticleRenderer` (one per render server): draw-pass meshes through `MeshGpu`, a small
  material set (sampler, albedo, emission) with the parameters as push constants, a per-frame-slot instance ring, and
  one instanced draw per draw-pass surface. The vertex shader builds each particle's basis from the material's
  `BillboardMode` (`Enabled`, `FixedY`, `Particles`; with `ParticleFlagAlignY` the `Particles` mode aligns with the
  velocity and faces the camera). `ViewDepth` sorts blended particles back to front on the CPU.
- **New `StandardMaterial3D` members** (Godot's names): `BlendMode` (`Mix`, `Add`), `VertexColorUseAsAlbedo`,
  `BillboardMode`; `BillboardMode.Particles`. Particle draw passes honour them; mesh surfaces do not yet.
- **Frame placement:** blended emitters draw after the blended meshes (`VisualInstance3D.DrawsTransparent`), fogged,
  and their HDR colours feed the glow. They are not in the depth prepass; in the main view they mark TAA's reactive mask
  in the scene alpha like water (new `BlendMode.Additive`/`AdditiveReactive` pipeline blends).
- **Inert headless:** nothing is simulated or allocated without a render server.
- `Aabb` becomes a serializable export type (6-float codec, the generator's built-in types) with a six-field inspector
  editor.

## Consequences

- The budget of issue #120 (20 × 200 + 4,000 rain) costs about 0.1 ms of simulation per step and 0.35 ms of sorted
  instance writing per view on an M5, allocation-free; the frame held 120 Hz.
- No change to `MeshRenderer`, `MeshInstanceData` or the mesh shaders; two new shaders (`Particles/Particle.vk.*`) and an
  include (`particle_common.slang`).
- Emitters are not sorted against blended meshes, are not pickable in the viewport and cast no shadows; GPU simulation,
  trails, collision and sub-emitters are later work. Render tests `particles-campfire` and `particles-rain` (goldens),
  allocation and frame-rate gates, `ParticleBenchmarks`.
