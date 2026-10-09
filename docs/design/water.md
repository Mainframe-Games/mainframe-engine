# Water

The current state of water in the engine: `Curve3D`, `River3D` geometry and the world's water queries (G8c.2–G8c.3,
[ADR 0155](../../memory/decisions/0155-curve3d-river3d-forest-project.md)); carving, `WaterMaterial3D` (the look without
`SceneTextures`) and ponds from the terrain's water layer (G8c.1, G8c.4, G8c.7 subsets,
[ADR 0159](../../memory/decisions/0159-watermaterial3d-and-stream-carving.md)); refraction, screen-space reflections and
caustics through a [scene copy](#scene-textures), falls with spray (G8e.6,
[ADR 0173](../../memory/decisions/0173-water-scene-copy-refraction-ssr-falls.md)). The full design (pond joins, the
editor tool, audio) is the proposal [future/water.md](future/water.md); what is not built is listed under
[Not yet](#not-yet).

## `Curve3D`

`MainframeEngine/Src/Resources/Curve3D.cs`: a `Resource` with a subset of Godot's `Curve3D` API and two per-point values
Godot does not have.

| Member | Notes |
|---|---|
| `AddPoint(position, in, out, index)`, `RemovePoint`, `ClearPoints`, `PointCount` | `in`/`out` are control offsets relative to the point (Godot) |
| `Get/SetPointPosition`, `In`, `Out`, `Tilt` | Godot's |
| `Get/SetPointWidth` (default 1 m), `Get/SetPointDepth` (default 0 m) | extensions: the **full** width and the centreline water depth |
| `Sample(index, t)` | the Bézier on one segment; out-of-range indices return the end points |
| `BakeInterval` (0.2 m), `GetBakedLength`, `GetBakedPoints`, `GetBakedDistances` | equal-arc-length baked points |
| `SampleBaked(offset, cubic)`, `SampleBakedWithRotation(offset, cubic, applyTilt)` | offsets clamp to 0 … length; cubic is Catmull-Rom through the baked points; the transform looks along the curve with **−Z** (the engine's forward) |
| `SampleBakedTilt/Width/Depth(offset)` | smoothstep between points |
| `GetClosestOffset(point)`, `GetClosestPoint(point)` | projection onto every baked segment |

- **Storage:** one exported `float[] Points`, 12 floats per point (position, in, out, tilt, width, depth). Every setter
  raises `Resource.Changed` and marks the bake dirty.
- **Baking** is lazy. Each Bézier segment is subdivided adaptively (at least 8, at most 1024 pieces, until the curve's
  midpoint is within 1 % of the interval of the chord's), then resampled at equal arc length (`ceil(length / interval)`
  steps, so the step is at most `BakeInterval`). Baked arrays are reallocated only when the baked count grows.
- **Determinism:** baking uses only `+ − × ÷` and `MathF.Sqrt` on floats, with scalar dot products in a fixed order
  (never `Vector3.Dot`, `Length` or `Lerp`, whose SIMD forms may round differently per CPU), so a curve bakes to the same
  bits on x64 and arm64. `Curve3DTests.FixedCurveBakesToACommittedHash` holds a hash of a fixed curve's bake.
- Sampling and closest-point queries allocate nothing.

## `River3D`

`MainframeEngine/Src/Scene/Nodes3D/Water/River3D.cs` (generation in `RiverBuilder.cs`): a `Node3D` holding a `Curve3D`
whose anchor Y is the water surface. It generates a ribbon and implements `IWaterBody3D`.

| Group | Exports (defaults) |
|---|---|
| — | `Curve`, `Material` (null: a default [`WaterMaterial3D`](#watermaterial3d)) |
| Shape | `SectionLength` 1 m, `CrossSegments` 4, `UvLength` 4 m, `EnforceDownhill` on |
| Flow | `MinSpeed` 0.3 m/s, `MaxSpeed` 4 m/s, `SpeedPerSqrtSlope` 6, `SlopeWindow` 6 m |
| Falls | `FallSlope` 0.7 (0 = no falls), `FallMinHeight` 1 m, `FallMaterial` (null: a default [`WaterfallMaterial3D`](#falls)), `Spray` on, `SprayMaterial` (null: default) |
| Carve | `TerrainPath` (empty), `BankWidth` 2 m, `ShoreLift` 0.05 m, `CarveId` (set by the first carve) |

Also `[Signal] Regenerated`, `Regenerate()`, `MeshData` (`RiverMeshData`), `Length`, `FlowSpeedAt(offset)`,
`SurfaceHeightAtOffset(offset)`, `Ribbon` (the generated child), `Falls` (`RiverFall`: lip, foot, width, drop,
downstream), `FallsNode` and `SprayNodes` (generated children), and for [carving](#carving) `Terrain` (code-set, not
saved), `Carve()`, `Recarve()`, `Uncarve()`, `FitToTerrain(depthBelow)` and `LastCarveEdit`.

**Generation** runs when the curve raises `Changed` or a Shape/Flow export changes (immediately when the river is ready
in a tree, else on entering it, or lazily on the first query); never per frame. `RiverBuilder` is plain C#:

1. **Downhill clamp:** the baked heights are clamped so they never rise downstream (`y[i] = min(y[i], y[i−1])`); the
   curve itself is unchanged.
2. **Sections** every `SectionLength` of arc length (`ceil(length / SectionLength)` equal steps), each with the clamped
   surface height, the XZ tangent (the previous one where the curve is vertical), half the interpolated width, the depth
   and the flow speed.
3. **Flow speed:** `slope = (y(s − W/2) − y(s + W/2)) / arc distance` over `W = SlopeWindow` (shortened at the ends),
   `speed = clamp(MinSpeed + SpeedPerSqrtSlope × √slope, MinSpeed, MaxSpeed)`: 1 % ≈ 0.9 m/s, 10 % ≈ 2.2 m/s. The slope
   is per metre of arc, not of horizontal run.
4. **Ribbon:** `CrossSegments + 1` vertices per section at `x = −1 … 1` across (left bank to right bank, facing
   downstream), normals up, triangles counter-clockwise seen from above. **UV.u = 0 … 1 across, UV.v = arc length /
   `UvLength`.**
5. **`RiverMeshData`** (`Positions`, `Normals`, `UVs`, `Custom0`, `Indices`): `Custom0` is x = column depth
   `depth × (1 − x²)`, yz = flow along local X/Z (`tangent × speed × (1 − 0.6x²)`, slower at the banks), w = foam
   (`0.6 × saturate((speed − 2) / 2)`, rapids). The `ArrayMesh` gets positions, normals, UVs, indices and `Custom0`
   (`MeshSurface.Custom0`, the second vertex stream). Arrays are reused while the section count holds.
6. **Grid:** a uniform 4 m grid (river-local XZ) maps cells to the section segments whose quads overlap them.
7. **Falls** ([below](#falls)): runs of sections steeper than `FallSlope` that drop at least `FallMinHeight` leave the
   ribbon; a jet replaces them.

The ribbon is an internal `MeshInstance3D` child named `Ribbon`, created in `OnReady` and **unowned**, so scenes never
save it. Rivers are meant to be moved and turned about Y; queries assume no tilt or scale.

### Falls

G8e.6 ([ADR 0173](../../memory/decisions/0173-water-scene-copy-refraction-ssr-falls.md)), the subset of G8c.3 the Forest
needs:

- **Detection** (`RiverBuilder.FindFalls`): over the clamped section centres, a run of consecutive segments each dropping
  at least `FallSlope` per horizontal metre (0.7 ≈ 35°) is a fall when its total drop is at least `FallMinHeight`. Its
  first section is the **lip**, its last the **foot**. The water test's stream and existing rivers have none, so their
  ribbons are unchanged.
- **Ribbon:** the quads between lip and foot are left out (the vertices stay; the index count shrinks). Sections below
  the foot get **plunge foam** in `Custom0.w`: 0.8 at the foot, fading over 1.5 widths.
- **Jet** (`RiverBuilder.FallMesh`, drawn by the unowned child `Falls` with `FallMaterial`): a ballistic sheet from the
  lip to the foot, `x = run · s`, `y = −drop · s²` (a horizontal launch that lands exactly at the foot), cross-sections
  every 0.25 m of arc, `CrossSegments + 1` columns, half width easing from the lip's to the foot's (12 % narrower in the
  middle). The first and last rows coincide with the ribbon's lip and foot rows, so there is no seam. Normals
  `cross(right, tangent)`: up at the lip, downstream on the vertical part. UV.u across, UV.v the ribbon's arc length
  continued. `Custom0`: x = how far down (0 … 1), y = the speed `√(v_lip² + 2 g · drop · s²)` in UV units per second
  (the water vertex shader rotates yz as a flow, which keeps its length), w = aeration `0.15 + 0.85 smoothstep(0, 0.55, s)`.
- **Spray:** with `Spray`, one [`SprayCards3D`](#spray) per fall (unowned children `Spray`, `Spray2`, …) at the foot,
  0.25 widths downstream and 0.35 m up, turned to the flow, `Extents` from the width, `CardSize` `0.6 + 0.35 · drop`
  (0.8–3 m), `Count` `round(1.5 · drop · width) + 4` (6–16).
- Queries are unchanged: between lip and foot they still interpolate the sloped sections (under the jet).

## Carving

`River3D.Carve()` cuts the channel into a `Terrain3D` as **one** height edit (`TerrainEdit.Begin(Height)` … `End()`,
returned as `LastCarveEdit`; inside an edit someone else opened it joins that one). The terrain is `Terrain` if set, else
`TerrainPath`, else the first `Terrain3D` in the tree whose map holds the curve's first point; `Carve` returns false
without one or when the river lies off the map.

The sections are moved into terrain-local space (the river's translation and yaw, then the terrain's origin). For every
height vertex within the widest half width + `BankWidth` of the river, the **nearest centreline segment** (ties: the
first in river order) gives the distance `r`, and interpolated half width `hw`, depth `D` and clamped surface `y`
(`RiverCarver`, plain C#, a fixed loop order):

| Where | New height |
|---|---|
| channel, `r ≤ hw` (`x = r / hw`) | `y + ShoreLift − (D + ShoreLift)(1 − x²)`: the bed meets the surface at `x² = D / (D + ShoreLift)` (x ≈ 0.96 for 0.6 m) |
| bank, `hw < r ≤ hw + BankWidth` | `lerp(y + ShoreLift, original, smoothstep((r − hw) / BankWidth))` |
| past either end | the bank blend from the end section's lip, over the distance beyond the end and beyond the half width |

So the ribbon (at `y`) always sits `ShoreLift` below its banks: the shoreline is a clean cut. Streams made in code put
their points in the ground first: `FitToTerrain(0.3f)` sets every point to `HeightAt − 0.3 m` in one curve change (call
it before carving; it samples the current ground).

**Carve records.** The terrain data keeps, per `CarveId`, the original heights of the rectangle the carve wrote
(`TerrainCarveRecord`). `Carve` with a record (and `Recarve`) first writes them back, then carves the current curve, so
carves never stack; `Uncarve` only restores and clears `CarveId`. The records are saved with the terrain's layers
(`TerrainData.SaveLayers`, so on scene save) as `carve_<id>.json` beside them: the rectangle and the heights as the
terrain's 16-bit values (base64). Undo of the edit restores the heights bit for bit; so does `Uncarve`. Sculpting inside
a carved channel is lost on re-carve.

## `WaterMaterial3D`

`MainframeEngine/Src/Rendering/Resources/WaterMaterial3D.cs`, shaders `Water/Water.vk.vert` and `Water/Water.vk.frag`
(`ShaderSetId.MeshWater`, always the `MeshInstancedExt` layout). By default the proposal's look **without `SceneTextures`**
(below; with `RefractionEnabled` see [Scene textures](#scene-textures)): no refraction; the surface is alpha-blended over
the bed. Blended (drawn back to front after the opaques), back faces culled,
no depth writes, casts no shadow. In the main view it blends with `BlendMode.AlphaReactive` (ADR 0166): the colour as
before, the scene alpha × (1 − its alpha), which TAA reads as a reactive mask (the flow animates under the bed's static
motion vectors; TAA keeps less history there instead of smearing the ripples).

| Group | Exports (defaults) |
|---|---|
| Surface | `NormalMap` (null: built-in), `NormalScaleNear` 3 m, `NormalScaleFar` 11 m, `NormalStrength` 1, `Roughness` 0.06 |
| Flow | `FlowCycle` 1.6 s, `FlowScale` 1, `WindDrift` 0.05 |
| Colour | `Absorption` (0.45, 0.09, 0.06) /m linear, `ScatterColor` sRGB (13, 56, 61), `ScatterStrength` 0.6 |
| Reflection | `ReflectionStrength` 1 |
| Foam | `FoamTexture` (null: built-in), `FoamScale` 2 m, `ShoreFoamDistance` 0.15 m, `FoamStrength` 1 |
| Edges | `SoftEdgeDistance` 0.1 m |

It reads `Custom0` per vertex: x = water column depth (m), yz = flow along the mesh's local X/Z (m/s; the vertex shader
turns it into world XZ), w = foam. The fragment shader, in order:

1. **Flow-advected ripples** (Vlachos, "Water Flow in Portal 2"): flow = `Custom0.yz × FlowScale` + the world's wind ×
   `WindDrift`; `t = time / FlowCycle + noise(worldXZ)`, phases `frac(t)` and `frac(t + ½)` shift world-XZ UVs by
   `−flow × phase × FlowCycle` and blend with weights `1 − |2p − 1|`. Two layers of the normal map (near scale, and far
   scale rotated 36.87°, half as fast, 0.6 as strong), whiteout-blended, in a tangent frame from the vertex normal.
2. **Fresnel** (Schlick, F0 = 0.02) and **reflection**: `iblSpecular(R, roughness) × (0.02·x + y)` with `iblBrdf` (R's
   Y folded up: ripples never reflect the ground) × `ReflectionStrength`.
3. **Glints:** GGX from every light through `pbrLight` with F0 = 0.02, shadowed like `shadeLightsPbr` (the first
   directional light's shadow also darkens the scattered light and the foam).
4. **Water body:** Beer–Lambert `T = exp(−Absorption × column / cos θ)` (θ from the vertex normal), in-scatter
   `ScatterColor × ScatterStrength × (sky diffuse + sun × shadow) × (1 − T)`.
5. **Composition:** added light `scatter (1 − F) + reflection + glints`; the background shows through by
   `mean(T) × (1 − F)`.
6. **Foam:** mask = max(shallow column (`1 − column / ShoreFoamDistance`), the ribbon's UV.u edge band, `Custom0.w`,
   fast flow above 1.5 m/s) × `FoamStrength`, thresholding the flow-advected foam texture; lit as rough white.
7. **Soft edges:** everything fades in over the first `SoftEdgeDistance` of column.
8. `applyFog`; out: colour = added ÷ alpha, alpha = 1 − (share of the background).

**Packing.** Water reuses `StandardMaterial3D`'s set 2 layout (`MaterialParams.From(WaterMaterial3D)`): albedo = scatter
colour + strength, emission = absorption + reflection strength, uvTransform = 1 / near and far scale, normal strength,
roughness; params = flow cycle, flow scale, shore foam distance, foam strength; pbr = 1 / foam scale, soft edge, wind
drift; the albedo slot holds the foam mask (linear), the normal slot the normal map. All textures share the first
texture's sampler (the foam's): custom maps should repeat.

**Built-in textures** (`WaterTextures`, generated on first use, cached per process, 256², linear, mipmapped, repeating):
`Normal`, 32 sine waves with whole-period wave vectors (`|k|` 3–14 per tile, amplitude ∝ `|k|^−1.2`) normalised to an
RMS slope of 0.2; `Foam`, the edges of two scales of tileable Worley cells, broken up.

### Scene textures

With **`RefractionEnabled`** (default off; ADR 0173) the water reads what lies behind it from a copy of the scene, the
proposal's `SceneTextures` on ADR 0163's post system:

| Group | Exports (defaults) |
|---|---|
| Refraction | `RefractionEnabled` off, `RefractionStrength` 1 (water's IOR; 0 = no bend), `RefractionRoughness` 0.15 |
| Reflection | `ScreenSpaceReflections` on (with refraction; quality `rendering.waterSsr`) |
| Caustics | `CausticsTexture` (null: built-in), `CausticsScale` 3 m, `CausticsStrength` 0.5, `CausticsMaxDepth` 3 m |

**The split.** A view whose sorted draws contain refracting water (`MeshViewDraws.SceneReaders`, counted in `Prepare`)
splits its scene pass in `RenderServer.DrawWorld` after the opaque half (sky, opaques and cutouts, the remaining
visuals): `WaterSceneTextures.SplitScenePass` ends the pass on its target (`RenderTarget.End`: the explicit barrier),
records the copy, and begins `ResumePass` (colour and depth `LOAD`, from and back to their sampled layouts; render-pass
compatible with the scene pass: the same attachments and dependencies), so every scene pipeline and the target's
framebuffer keep working; the blended draws, debug lines and `OnRenderMainPass` follow. The main view (`vk.SceneTarget`)
and every `SubViewport` (`targets.Hdr`, so the editor shows it as the game) have their own copy, created the first frame
they draw refracting water. Views without refracting water are untouched.

**The copy** (`WaterSceneTextures`, `Src/Rendering/Water/`): one `R16G16B16A16_SFLOAT` image of up to 6 levels at the
target's size, rgb = HDR colour, a = linear view depth in metres (the sky is 60 000), built with fragment passes
(`Post/Fullscreen.vk.vert`):

- `Water/SceneCopy`: level 0 = `Load` of the scene colour and depth; the depth is linearised through the view's
  inverse projection (set 0, so perspective, orthographic and jittered projections are exact);
- `Water/SceneDownsample`: each further level averages the colour of the 2 × 2 below it and keeps the **minimum** depth
  (a ray against a coarse level never steps through thin geometry).

It reads the **scene pass's own depth** (what the opaque half drew, prepass or not), so water needs no depth prepass.
Recreated through the deletion queue when the target's size or images change; recording allocates nothing.

**Bindings.** Refracting water draws with `ShaderSetId.MeshWaterScene` and a pipeline layout of its own: sets 0–2 are
the mesh layout's (frame, shadows, the material's usual set 2; the caustic pattern in the emission slot) and **set 3**
is the copy (one combined image sampler, trilinear clamp). The fragment stage: frame 5 + shadows 6 + material 4 + copy 1
= 16 sampled images, 14 samplers (MoltenVK's 16 each; G8e.1's set 0 changes must keep set 0 at 5 images or fewer). The
material's extra parameters and the view's quality ride in a 64-byte push block (`MeshRenderer.WaterScenePush`,
`include/water_scene.slang`), pushed per material run. Set 3 is bound again with every such material (binding set 2
with the mesh layout disturbs it). Without a copy (the object-ID pass, a view that did not split) the same material
draws its ADR 0159 look.

**The shader** (`Water/WaterScene.vk.frag`, helpers shared with `Water.vk.frag` in `include/water.slang`):

1. **The true column.** The copy's depth at the pixel gives the bed straight behind; the column (surface − bed height)
   drives the shore foam (so rocks breaking the surface get foam rings) and the soft edge.
2. **Refraction.** The view ray bends at the rippled surface (`refract`, η = 1/1.33, blended towards the unbent ray by
   `RefractionStrength`) and travels the straight path's length (≤ 8 m) to the bed; that point is projected and read
   from the copy, unless the copy there is in front of the surface (a foreground object), in which case the unbent pixel
   is kept. The bed point is rebuilt from the bent pixel's depth. Level `RefractionRoughness × last level ×
   saturate(path / 2 m)` blurs deep water. A flat surface already lifts the bed (water looks shallower), the ripples
   wobble it.
3. **Caustics.** Where the sunbeam reaching the bed entered the water (the bed's XZ moved along the sun by the depth,
   plus the ripple's tilt × depth), two scrolled layers of the pattern (`min` blend, the far layer rotated 36.87° and
   1.37 × denser) × 2.5 brighten the refracted colour: `× (1 + strength · pattern · the bed's sun shadow · fade ·
   exp(−absorption · depth / L.y))`, fading in over 8 cm and out towards `CausticsMaxDepth`. Multiplicative, so dark
   beds get dark caustics; it shows only through the water, with no decal pass.
4. **Absorption** per channel over the refracted path (`exp(−Absorption · path)`), in-scattering where light is absorbed.
5. **Reflection:** [screen-space reflections](#screen-space-reflections) where they hit, the sky's radiance by the rest
   of the confidence; × the split-sum BRDF and `ReflectionStrength`; Schlick Fresnel; GGX glints as before.
6. **Foam, fog, soft edge:** foam lit as before, `applyFog`, then the soft edge blends towards the unbent copy (no hard
   shoreline). Fog is applied over the refracted bed too, which the copy already fogged to the bed: a slight double fog
   over long paths.

**Output and TAA.** The water composes the background itself, so the colour replaces the pixel:
`BlendMode.ColorOnlyReactive` in the main view (`rgb = src`, `a = dst · (1 − src.a)`) with `src.a` =
`MeshRenderer.WaterSceneReactivity` (0.85) × the soft edge. TAA's resolve reads `1 − scene alpha` as reactivity as
before (ADR 0166): feedback ≈ 0.31 on water instead of 0.94, so the flowing ripples do not smear while the reflection's
jittered march still averages over a few frames. Other views use `BlendMode.ColorOnly` (destination alpha kept).

#### Screen-space reflections

`include/water_scene.slang` `sceneReflection`, per water pixel: the reflected ray (view space) is clipped to the near
plane, `MaxDistance` and the screen, and marched in **equal steps of screen distance** (perspective-correct depth along
the projected segment) against **level 1** of the copy (half resolution, nearest depth), from an interleaved-gradient-
noise start that moves with the TAA sample. A step hits when it crosses the scene's surface: `rayZ > sceneZ` and
`sceneZ > min(previous rayZ, rayZ) − thickness` (`thickness` + 3 % of the depth), so long steps never jump over a trunk
and rays passing behind something go on. Then bisection; the colour comes from the copy at level `1 + 4 · roughness`.
Confidence fades over the last 15 % towards the screen edges, the last 20 % of the march, rays turning back towards the
camera (view z 0 … 0.4) and roughness 0.3 … 0.6; misses keep the sky's radiance. Quality is
`RenderServer.WaterSsr` (`rendering.waterSsr`, the editor viewport follows the project): `Off`, `Low` (default: 16 steps
+ 4 refinements, 40 m, 0.5 m), `High` (28 + 6, 60 m, 0.35 m; the Forest). It runs per water pixel at full resolution
against the half-resolution level, rather than as a separate half-resolution pass: water is forward-shaded and not in
any G-buffer, so a separate pass would need its normals first.

### Falls look: `WaterfallMaterial3D`

`MainframeEngine/Src/Rendering/Resources/WaterfallMaterial3D.cs`, `Water/Water.vk.vert` + `Water/Waterfall.vk.frag`
(`ShaderSetId.MeshWaterfall`): blended, double-sided, no depth writes, no shadows, `AlphaReactive` in the main view.

| Group | Exports (defaults) |
|---|---|
| Sheet | `FoamTexture` (null: the foam cells), `StreakScale` 4, `StreakLength` 5, `FlowScale` 1, `Roughness` 0.2 |
| Colour | `WaterColor` sRGB (46, 74, 70), `FoamColor` sRGB (232, 238, 236), `Opacity` 0.7, `Aeration` 1 |

Two layers of the texture stretched along UV.v fall at the vertex speed (× 1 and × 1.35); the sheet whitens with the
aeration and the streaks, thins out and breaks up over the outer 14 % of its width, and is opaque where white. Lit by the
sky and the sun (two-sided, shadowed), a glow where the sun shines through it, glints on the clear water. Packing:
albedo = foam colour + opacity, emission = water colour + aeration, uvTransform = streak scale, length, flow scale,
roughness.

### Spray

`SprayCards3D` (`Src/Scene/Nodes3D/Water/`), a `GeometryInstance3D`: `Count` (8) camera-facing soft cards scattered
deterministically (`Seed`) in a box of half-size `Extents` (1.5, 0.4, 1) m, `CardSize` 1.6 m, `Drift` 0.6 m, drawn with
`Material` (`SprayMaterial3D`, null: default). The mesh holds four vertices per card at its centre: the normal stream is
the corner (±1) and size, `Custom0` the phase, rise scale and drift; `CustomAabb` covers the cards grown, risen and
drifted. Not saved: rebuilt from the exports.

`SprayMaterial3D`: `NoiseTexture` (null: `WaterTextures.Mist`), `Color` sRGB (236, 240, 240), `Opacity` 0.28, `Cycle`
3.5 s, `Rise` 1.2 m, `Growth` 1.8, `SoftDistance` 0.6 m. `Water/Spray.vk.vert` billboards each card with the view's
right and up and moves it over its cycle (rise, growth, drift, fade `sin(π t)`); `Water/Spray.vk.frag` cuts the alpha
from two drifting layers of the noise in a round falloff, fades it over `SoftDistance` against the scene copy's depth
(when the view has a copy; spray uses the water-scene layout with a white fallback set) and near the camera, and lights
it by the sky and the sun, brighter looking towards the sun. Blended, `AlphaReactive` in the main view, no shadows.

This is the cheap stand-in the proposal ships first; G6.3's `GpuParticles3D` replaces it when it lands.

**Built-in textures** (`WaterTextures`, as below): `Caustics`, bright curved lines around 6 × 6 tileable Worley cells in a
domain warped by whole-period sines; `Mist`, four octaves (4–32 per tile) of tileable value noise.

## Ponds

`Terrain3D` draws the water layer (`water.png`): one internal, unowned `MeshInstance3D` per chunk with water
(`GetChunkWater(cx, cz)`, null when dry), built by `TerrainWaterMesh`:

- the chunk's render triangles (same diagonal rule) with **any** wet vertex, LOD 0 only;
- vertices at the stored height (the water surface) − `ShoreDrop` (0.02 m; 0.04 m Faceted), so a dry shore vertex's bank
  stands above the water and the shoreline is a clean cut; normals up; UV (0.5, 0) (no ribbon-edge foam);
- `Custom0` = (column depth in metres, 0, 0, 0): 0 at dry vertices, so the soft edge fades the shore;
- material `Terrain3D.WaterMaterial` (null: `Terrain3D.DefaultWaterMaterial`, a `WaterMaterial3D`); no shadows cast.

They rebuild with their chunk on every height or water edit and are freed when the chunk dries.

## Water queries

`MainframeEngine/Src/Scene/Nodes3D/Water/WaterQueries.cs`. Every `IWaterBody3D` registers with its world's
`World3D.Water` while in the tree (`River3D` and `Terrain3D` do so in `OnEnterTree`/`OnExitTree`; game types call
`Register`).

```csharp
public interface IWaterBody3D { Aabb WaterBounds { get; } bool TrySample(Vector3 position, out WaterSample sample); }
public readonly record struct WaterSample(float SurfaceHeight, float ColumnDepth, Vector3 Flow);

var water = GetWorld3D()!.Water;
water.TrySample(p, out var sample);  // false when dry; the highest surface wins where bodies overlap
water.WaterDepthAt(p);               // bed-to-surface column, 0 when dry
water.SurfaceHeightAt(p);            // NaN when dry
water.FlowAt(p);                     // m/s, zero when dry or still
water.ImmersionAt(p);                // surface − p.Y; ≤ 0 above the water, 0 when dry
water.IsUnderwater(p, margin: 0.05f);
WaterQueries.WadeSpeedScale(immersion, start: 0.3f, full: 1.2f, minScale: 0.4f); // smoothstep 1 → 0.4
```

- A river sample is **inside** when the point lies between two section lines (each perpendicular to its section's
  tangent) and within the interpolated half width of the interpolated centreline, so bends have no gaps. Column depth
  and flow follow the ribbon's lateral profile.
- A **terrain** sample is over a wet point of the map: surface = `HeightAt + WaterDepthAt`, column = `WaterDepthAt`, no
  flow. Its `WaterBounds` cover the wet vertices (± one quad) and are empty for a dry map, recomputed on water edits.
- Bodies are skipped by their `WaterBounds` in XZ first. Main thread only; no query allocates (tested).

A character controller slows while wading and drifts with the flow (the Forest's `FirstPersonController`):

```csharp
var water = GetWorld3D()!.Water;
var scale = WaterQueries.WadeSpeedScale(water.ImmersionAt(GlobalPosition));
target = wish * (WalkSpeed * scale) + water.FlowAt(GlobalPosition) * FlowPush;   // FlowPush ≈ 0.3
```

## Not yet

From [future/water.md](future/water.md): pond joins, plunge pools carved below falls and the `Faceted` look (the
palette sheet; Faceted ponds draw with `WaterMaterial3D` for now) (G8c.3); the River3D editor tool and its Carve/Re-carve
buttons (G8c.4); audio (G8c.5); back-face (underwater) shading and water depth writes (G8c.6–8);
`rendering.sceneTextures` (`Off`/`Half`/`Full`; the copy is always full resolution); G8e.6's wet banks and the probes'
sky visibility on SSR misses (G8e.1); particles for spray (G6.3). Refracted transparent surfaces behind water are not
in the copy (as in Godot). Pond surfaces have no LOD levels. Buoyancy stays a sample.

## Tests

`Tests/MainframeEngine.Tests/Water/`: `Curve3DTests` (Bézier values, a quarter circle's length within 0.1 %, clamped
sampling, closest offset, width/depth/tilt interpolation, rotation, a scene round trip, `Changed` on every setter, 0 B
sampling, the committed bake hash), `RiverBuilderTests` (section count, UVs, normals and winding, bank order, the downhill
clamp, speed from slope, `Custom0`, array reuse, lateral sampling), `WaterQueriesTests` (registration, the unowned and
unsaved ribbon, regeneration on curve changes, inside/outside/edge, transforms, bends, overlaps, `WadeSpeedScale`, 0 B
queries). `RiverCarveTests` (the channel and bank profile against the formulas, closed ends, the bed under the surface,
undo and `Uncarve` bit for bit, re-carving equals carving once and equals carving a moved river into the original
ground, terrain resolution by path and position, `FitToTerrain`, carve records through the layer folder),
`TerrainWaterTests` (pond surfaces on wet chunks only, at the stored height − `ShoreDrop`, `Custom0` depths, freed when
dry, the terrain as a water body, empty bounds when dry), `WaterMaterial3DTests` (render state, setters, the parameter
packing, the built-in textures deterministic and tiling).

ADR 0173 (`WaterSceneTests`): the refraction defaults and setters, the push block's size and contents per quality, the
copy's level count, a pipeline slot for every shader set, the caustic and mist textures deterministic and tiling, the
falls' and spray's packing, falls found on a step (jet from lip to foot, speeds and aeration rising, facing downstream,
plunge foam, the ribbon's quads left out) and not on gentle rivers or with `FallSlope` 0, a river in a tree with its
`Falls` and `Spray` children (unowned; following `Spray` and `FallSlope`), deterministic spray cards, `rendering.waterSsr`
round trips. Render ([`WaterSceneTextureTests`](../../Tests/MainframeEngine.RenderTests/WaterSceneTextureTests.cs),
goldens on both drivers): `water-refraction` (a striped bed under a clear pool: frame 10 the bed alone, 20 the pool flat
and unbent, which must show the bed straight behind, 30 rippled and bent, which must move the stripes while the bed
beside the pool stays), `water-ssr` (a red pillar beyond a dark mirror pool: its mirror image is red with SSR and not
without; again under TAA, and in a post-processed sub-viewport with TAA), `water-fall` (a stream over a 4 m cliff: one
fall, its jet and spray, refracting water with caustics), 0 B per frame with TAA for `water-refraction` (orbiting) and
`water-fall`.

Render ([`WaterRenderTests`](../../Tests/MainframeEngine.RenderTests/WaterRenderTests.cs), scene `water`): a 64 m
terrain with a fitted and carved stream, a pond from the water layer and a pillar shadowing the stream, procedural sky
for ambient and reflections, the sun ahead (glints); golden at frame 30 (fixed step, so a fixed flow phase), self-checks
(carve record, water at the centre, bed under the surface, the pond's surface height and node, the material), and a 0 B
allocation gate while the camera orbits.

## Related

[Physics](physics.md) · [Materials & meshes](materials-and-meshes.md) · [Forest](forest.md) ·
[future/water.md](future/water.md) · [ADR 0149](../../memory/decisions/0149-terrain-trees-water-engine-features.md) ·
[ADR 0155](../../memory/decisions/0155-curve3d-river3d-forest-project.md) ·
[ADR 0159](../../memory/decisions/0159-watermaterial3d-and-stream-carving.md) ·
[ADR 0173](../../memory/decisions/0173-water-scene-copy-refraction-ssr-falls.md) · [Post-processing](post-processing.md)
