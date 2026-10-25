# 3D particles

The current state of 3D particles: `GpuParticles3D` on the shared `ParticleProcessMaterial`, simulated on the CPU and
drawn by its own `ParticleRenderer` (G6.3, [ADR 0206](../../memory/decisions/0206-gpuparticles3d.md), issue #120). The 2D
emitter, `GpuParticles2D`, is in [2D canvas → Particles](canvas.md#particles). The G6 plan this replaces is
[future/rendering-features.md → 3D particles](future/rendering-features.md#3d-particles); what differs from it is listed
under [Differences from the plan](#differences-from-the-plan).

## `GpuParticles3D`

`MainframeEngine/Src/Scene/Nodes3D/GpuParticles3D.cs`, a `VisualInstance3D` (Godot's `GPUParticles3D` derives from
`GeometryInstance3D`; here the mesh batcher never sees it). Godot's names throughout. It is a `[Tool]` node, so the
editor's viewport previews it, and it casts no shadows (`CastShadows` defaults to false).

| Member | Default | Notes |
|---|---|---|
| `ProcessMaterial` | null | A `ParticleProcessMaterial`; without one nothing emits |
| `DrawPass1` | null | The mesh drawn per particle (Godot's `draw_pass_1`); null: a 1 × 1 `QuadMesh` facing +Z |
| `MaterialOverride` | null | Replaces every draw-pass surface's material |
| `DrawOrder` | `ViewDepth` | `Index`, `Lifetime` (oldest first), `ReverseLifetime`, `ViewDepth` (back to front). Godot defaults to `Index` |
| `Amount` | 8 | Particle slots; arrays are reallocated only when it changes |
| `AmountRatio` | 1 | Fraction of the slots that emit |
| `Lifetime` | 1 | Seconds per cycle |
| `OneShot` | false | Each slot emits once; then `Emitting` goes false and `Finished` fires |
| `Preprocess` | 0 | Seconds simulated when emission starts (or on `Restart`) |
| `SpeedScale` | 1 | Multiplies the simulation's time; 0 pauses |
| `Explosiveness` | 0 | 0 spreads emission over the lifetime, 1 emits every slot at once |
| `Randomness` | 0 | Jitters each slot's start by up to one slot, per cycle (Godot's `randomness`) |
| `FixedFps` | 30 | Simulation steps per second; 0: one step per frame, at most 1/30 s each |
| `Interpolate` | true | Draws particles between their last two fixed steps |
| `UseFixedSeed`, `Seed` | false, 0 | The same random streams every run; otherwise a new seed per `Restart` |
| `LocalCoords` | false | Particles move with the emitter; off, they stay where they were emitted |
| `VisibilityAabb` | ±4 | The culling box in the emitter's space; empty: never culled. An `Aabb` export (codec: min then max corner) |
| `Emitting` | true | Setting it on a one-shot fires it again (`Restart`) |
| `Restart()` | | Kills every particle and starts the schedule over; allocates nothing once sized |
| `EmitParticle(xform, velocity, color, custom, flags)` | | One particle now, in a free slot (else the oldest's); `EmitFlags.Position`, `RotationScale` (scale from the X axis), `Velocity`, `Color` replace the material's values; `custom` is ignored |
| `Finished` | | Signal: a one-shot's last particle has ended |
| `LiveCount` | | Particles alive now |

**Inert headless.** The node simulates only when its tree has a render server: under `HeadlessHost` (a dedicated server)
nothing is stepped, preprocessed or allocated, and `EmitParticle` does nothing. Unit tests set the internal
`SimulateWithoutRenderer`.

## `ParticleProcessMaterial`

`MainframeEngine/Src/Scene/Particles/ParticleProcessMaterial.cs`, shared with the 2D emitter (it moved out of
`GpuParticles2D.cs`; existing `.mres` and `.mscene` files load unchanged, and new members keep their defaults). Members
the 2D emitter ignores are marked "3D only".

| Group | Members |
|---|---|
| Emission shape | `EmissionShape`: `Point`, `Sphere` (uniform in volume), `SphereSurface`, `Box`, `Ring` (3D only, Godot's value 6); `EmissionBoxExtents`, `EmissionSphereRadius`, `EmissionRingAxis`, `EmissionRingRadius`, `EmissionRingInnerRadius`, `EmissionRingHeight` |
| Direction | `Direction`, `Spread` (degrees), `Flatness` (3D only), `InitialVelocityMin/Max` |
| Accelerations | `Gravity`, `LinearAccelMin/Max`, `DampingMin/Max` |
| Angle | `AngleMin/Max`, `AngularVelocityMin/Max` (degrees, degrees per second) |
| Scale | `ScaleMin/Max`, `ScaleCurve` (a `Curve` over the life) |
| Color | `Color` (sRGB, above 1 for HDR), `ColorInitialRamp` (3D only: a `Gradient` sampled at a random offset per particle), `ColorRamp` (over the life), `HueVariationMin/Max` (3D only: a fraction of a turn, −1..1) |
| Time | `LifetimeRandomness` |
| Turbulence (3D only) | `TurbulenceEnabled`, `TurbulenceNoiseStrength`, `TurbulenceNoiseScale`, `TurbulenceNoiseSpeed`, `TurbulenceNoiseSpeedRandom`, `TurbulenceInfluenceMin/Max` |
| Particle flags | `ParticleFlagDisableZ`, `ParticleFlagAlignY` |

**Gravity default.** The material's default `Gravity` is the 2D one, (0, 98, 0) in pixels with Y down. A 3D emitter reads
exactly that value as Godot's 3D default, (0, −9.8, 0) (`DefaultGravity2D`, `DefaultGravity3D`). Any other value is
used as authored (metres per second²). With `LocalCoords` gravity still pulls in world space: it is moved into the
emitter's space each step.

## Simulation

`ParticleSimulation3D` (`Src/Scene/Particles/ParticleSimulation3D.cs`, internal) is a sibling of the 2D simulation, not
a shared one: the 2D emitter keeps its `System.Random` stream and results exactly (`GpuParticles2DTests` are unchanged).

- **Schedule.** Slot *i* starts cycle *k* at `k × lifetime + lifetime × (i + randomness × hash(seed, i, k)) / amount ×
  (1 − explosiveness)` on the emitter's clock (Godot's restart phase). Each slot keeps its next start and cycle, so the
  per-cycle jitter needs no search. When the amount, lifetime, explosiveness, randomness or seed change on a running
  emitter, each slot resumes at its first start from now. A start inside a step emits the particle and advances it by the
  part of the step after its start; a start while emission is off (or a one-shot's second cycle) ends the particle.
- **Steps.** `FixedFps` steps of 1/FixedFps s from an accumulator (capped at 0.25 s of catch-up), or one step per frame
  split into ≤ 1/30 s. Each step stores the previous position and angle, and `Interpolate` draws
  `lerp(previous, current, accumulator / step)`.
- **A particle** has an age, an optional life (`LifetimeRandomness`, or a manual particle's), position, velocity, angle,
  scale, linear acceleration, damping, spin, a turbulence influence and its start colour (the material's `Color` × the
  initial ramp, hue-turned with the YIQ rotation Godot uses). Per step: gravity plus linear acceleration along the
  velocity, damping (speed lost per second), turbulence, spin, then the position.
- **Spread** is Godot's: one angle in the direction's horizontal plane, one in its vertical plane scaled by
  1 − `Flatness`, combined and turned onto `Direction`.
- **Turbulence** turns the velocity toward a smooth 3D value-noise flow (`ParticleNoise`: three channels, trilinear with
  smoothstep, drifting with `TurbulenceNoiseSpeed`, changing in place with `TurbulenceNoiseSpeedRandom`), keeping the
  speed. The influence is a share per 1/60 s (`1 − (1 − influence)^(dt × 60)`), so it does not depend on the step rate as
  Godot's per-frame mix does.
- **Random numbers** come from `ParticleRandom`, a PCG32 struct: deterministic for a fixed seed, reseedable without
  allocation. Godot's GPU streams are not reproduced.
- **`EmitParticle`** marks the particle manual: it lives one lifetime (or its randomized life) even while its slot is not
  emitting. While the emitter emits, the slot's next start reuses it, as in Godot.

## Drawing

`ParticleRenderer` (`Src/Rendering/Particles/`, one per `RenderServer`, `RenderServer.Particles`) is separate from the
mesh batcher.

- **Resources.** Draw-pass meshes are `MeshGpu`s (the batcher's upload code, not its instances) and materials have their
  own set 2: one sampler and the albedo and emission images (`TextureGpu`, white 1 × 1 when empty), from a
  `MaterialDescriptorAllocator`. Both are reference-counted by the emitters. The material's parameters ride in the
  128-byte push constants (`ParticlePush`: emitter matrix, linear albedo, emission × energy and alpha cutoff, UV
  transform, flags). Emitters prepare them in `OnProcess`, before the frame records, so uploads join the frame.
- **Instances.** Per view, `ParticleInstances.Write` orders the live particles, interpolates them, samples `ScaleCurve`
  and `ColorRamp` at age / life and writes 48-byte `ParticleInstance`s (position + scale, velocity + angle, linear
  colour) into a per-frame-slot ring (`ParticleInstanceRing`, doubling when full). `ViewDepth` sorts with
  `Span.Sort` over the emitter's preallocated key and index arrays, only when a surface mixes (additive and opaque
  passes need no order). Steady state allocates nothing.
- **One draw per surface.** `Particles/Particle.vk.vert` builds each particle's basis from the material's
  `BillboardMode` and the frame's camera basis (`frame.invViewRotation`):

  | `BillboardMode` | Basis |
  |---|---|
  | `Disabled` | The particle's own: turned by its angle about Y (about Z with `ParticleFlagDisableZ`), or Y along the velocity with `ParticleFlagAlignY`; then the emitter's matrix |
  | `Enabled` | The camera's |
  | `FixedY` | Upright, turned about world Y toward the camera (as `Billboard.Face`) |
  | `Particles` | The camera's, turned by the particle's angle in the view plane (Godot's `BILLBOARD_PARTICLES`); with `ParticleFlagAlignY`, Y along the velocity and turned about it to face the camera: sparks, rain streaks |

  The emitter's scale also sizes camera-facing particles with `LocalCoords`.
- **Materials.** A `StandardMaterial3D` (any other material type draws as the default: unshaded, mixed, vertex colour as
  albedo, `Particles` billboards). Used members: `AlbedoColor` × `AlbedoTexture` × (with `VertexColorUseAsAlbedo`, new,
  Godot's `vertex_color_use_as_albedo`) the particle colour; `ShadingMode` (`Unshaded`, else lit: ambient + diffuse from
  every light with shadows, no highlight, the normal turned toward the viewer); `EmissionColor` × `EmissionEnergy` ×
  `EmissionTexture`; `Transparency` (opaque, cutout with `AlphaCutoff`, blend); `BlendMode` (new, Godot's `blend_mode`:
  `Mix`, `Add`; `Add` blends whatever the transparency); `NoDepthTest`; `CullMode`/`DoubleSided` for unbillboarded
  particles; `BillboardMode` (new on the material, Godot's; `BillboardMode.Particles` is new); `UvScale`/`UvOffset`.
- **Fog and glow.** Fragments are fogged like other geometry (`applyFog`); additive ones fade to nothing in the fog
  instead of toward its colour. Colours and emission above 1 are HDR, so the glow post blooms them.
- **Order in the frame.** Blended emitters (`VisualInstance3D.DrawsTransparent`) draw after the blended meshes, in tree
  order; opaque and cutout emitters draw with the other self-drawn visuals, after the opaque meshes. Pipelines: depth
  test on (off with `NoDepthTest`), depth writes only for opaque and cutout, less-or-equal.
- **Depth prepass and TAA (ADR 0163).** Particles are not in the prepass and have no motion vectors. In the main view
  their pipelines mark TAA's reactive mask in the scene alpha, as water does (`BlendMode.AlphaReactive` for mixed and
  opaque, the new `AdditiveReactive`, `a = dst·(1 − a)`), so moving particles do not smear. Sub-viewports keep their
  alpha (`Alpha`, `Additive`, `Opaque`).
- **Culling.** `VisibilityAabb` through the emitter's world matrix, against each view's frustum.

## Performance

Issue #120's budget, 20 emitters × 200 particles plus a 4,000-particle rain box (`particles-stress`), on an Apple M5,
Release, a machine shared with other builds:

- `ParticleBenchmarks.SimulateStep` (one 1/30 s step of all 8,000): ≈ 0.1 ms; `WriteInstancesSorted` (one view, every
  particle sorted back to front): ≈ 0.35 ms; 0 B allocated.
- The render-test frame time held the display's 120 Hz (8.33 ms average, 9.4 ms p95), the same as an almost empty scene:
  MoltenVK paces frames to the display even with VSync off, so the measured CPU time includes the drawable wait and the
  benchmarks are the CPU cost.

## Tests

- **Unit** (`Tests/MainframeEngine.Tests/Scene/GpuParticles3DTests.cs`): emission shapes in bounds and uniform; spread
  cone and flatness; the gravity mapping (and local coordinates); hue rotation; the schedule (even emission, stopping),
  an explosive one-shot (`Finished`, allocation-free `Restart`), randomness within each slot's slice, seeded
  determinism; lifetime randomness, damping and turbulence; `FixedFps` stepping, interpolation and `SpeedScale` 0;
  `EmitParticle`; instance order (view depth, lifetime), curves, ramps and interpolation; 0 B steps and writes; inert
  under `HeadlessHost`; a scene round trip of every member; `.mres` load, including a 2D-era file. Editor:
  `InspectorModelTests.AnAabbEditsAsItsMinAndMaxCorners`.
- **Render** (`Tests/MainframeEngine.RenderTests/ParticleTests.cs`, scenes in `RenderTests.Host/Scenes/ParticleScenes.cs`):
  `particles-campfire` (additive flames and embers, lit smoke sorted back to front, glow on; golden), its glow-off run
  (the halo must be brighter with glow) and a TAA run (validation-clean, flames drawn), a determinism check, and
  `particles-rain` (4,000 streaks; golden). `particles-stress` gates 0 B per frame and the frame rate against an almost
  empty scene.
- **Benchmarks:** `ParticleBenchmarks` (not in `baseline.json` yet: re-record it on a quiet machine).

## Differences from the plan

- **A renderer of its own**, not the mesh batcher's instance spans: the batcher was being changed at the same time
  (skinning, spatial shaders), and particles need per-particle colour, billboarding in the vertex shader and their own
  order anyway. `MeshInstanceData` is unchanged.
- **A sibling simulation** (the plan's fallback) instead of one shared with 2D.
- `DrawOrder` defaults to `ViewDepth`, not Godot's `Index`, so blended particles sort without setup.
- The material's billboard modes apply to particle draw passes only; `MeshInstance3D` surfaces ignore `BillboardMode` and
  `BlendMode` (they always mix), and `Sprite3D` treats `Particles` as `Enabled`.

## Not yet

GPU (compute) simulation, trails, collision, attractors, sub-emitters, `CpuParticles3D`, particle shadows, picking an
emitter in the editor's viewport (it is not in the object-ID pass), motion vectors, sorting emitters against blended
meshes (they all draw after them, so an emitter behind water draws over it), `Sub`/`Mul`/premultiplied blend modes,
animated (flipbook) textures.

## Related

[ADR 0206](../../memory/decisions/0206-gpuparticles3d.md) · [ADR 0187](../../memory/decisions/0187-particle-life-features.md) ·
[2D canvas → Particles](canvas.md#particles) · [Materials & meshes](materials-and-meshes.md) ·
[Post-processing](post-processing.md) · [Rendering features (G6)](future/rendering-features.md)
