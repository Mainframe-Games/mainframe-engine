# Procedural trees

## Purpose

The engine generates trees from a seed and a set of options: a C# port of
[Ez Tree](https://github.com/dgreenheck/ez-tree) (MIT, © 2024 Daniel Greenheck), so a seed and one of Ez Tree's 15
presets give the same tree as in Ez Tree, and artists can tune trees in Ez Tree's web app and import the JSON. Around
the literal port sit the engine's additions: a LowPoly style, per-vertex wind data, LOD errors and a trunk capsule.

This page covers the generator ([ADR 0152](../../memory/decisions/0152-ez-tree-port.md)) and the nodes that draw its
trees ([ADR 0158](../../memory/decisions/0158-tree3d-tree-materials-treescatter.md)): `Tree3D` (one tree, three levels
of detail, bake), the tree materials (PBR bark and leaves on `FoliageMaterial3D`, wind from
[`WorldEnvironment`](materials-and-meshes.md#foliagematerial3d-adr-0151)) and `TreeScatter` (forests as chunked
`MultiMesh` batches). Impostors and the tree inspector are not built yet; the [proposal](future/procedural-trees.md) has
their design.

## Layout

| Where | What |
|---|---|
| [`Src/Trees/Generation/`](../../MainframeEngine/Src/Trees/Generation/) | The port, namespace `MainframeEngine.Trees`, no engine types: `TreeGenerator`, `TreeParams`, `TreeMeshDetail`, `TreeSkeleton`, `TreeMeshData`/`TreeSurfaceData`/`TreeTrunkCapsule`, the enums (`TreeType`, `TreeBillboard`, `TreeStyle`, `BarkUvMode`, `BlobShape`), `ITreeGrowthForce`; internal `EzRng`, `ThreeMath` (`Vec3d`, `Quatd`, `EulerXyz`), `LowPolyMesher` |
| [`Src/Trees/`](../../MainframeEngine/Src/Trees/) | Engine side: `TreeOptions` and `TreeLevel` (resources), `TreePresets`, `EzTreeJson`, `TreeMeshDataExtensions` (`ToSurface`, `ToArrayMesh`); `Tree3D`, `TreeLod3D`, `TreeMesh` (the bake), `TreeMaterials`; `TreeScatter`, `TreeSpecies`, `TreePlacement`, `TreeScatterBatch3D` |
| [`Rendering/Resources/OrmPacker.cs`](../../MainframeEngine/Src/Rendering/Resources/OrmPacker.cs) | Packs separate occlusion/roughness/metallic maps into one ORM texture (the bark's `_Roughness.jpg`) |
| [`Content/Trees/Presets/`](../../MainframeEngine/Content/Trees/Presets/) | The 15 presets as `TreeOptions` `.mres` files |
| [`Content/Trees/Leaves/`](../../MainframeEngine/Content/Trees/Leaves/), [`Content/Trees/Bark/`](../../MainframeEngine/Content/Trees/Bark/) | Ez Tree's leaf PNGs (MIT) and the three ambientCG bark sets the presets use (CC0); [`LICENSE.md`](../../MainframeEngine/Content/Trees/LICENSE.md) |
| [`build/ez-tree-reference.mjs`](../../build/ez-tree-reference.mjs) | Runs Ez Tree itself to write the parity fixture (by hand, never in CI) |
| [`Tests/MainframeEngine.Tests/Trees/`](../../Tests/MainframeEngine.Tests/Trees/) | Parity, RNG, determinism, allocation, LowPoly, options and preset tests; fixture in `Tests/Content/Trees/`; `Tree3DTests`, `TreeScatterTests` |
| [`Tests/MainframeEngine.RenderTests/TreeTests.cs`](../../Tests/MainframeEngine.RenderTests/TreeTests.cs) | `tree-realistic`, `tree-lowpoly`, `tree-forest` (scenes in the host's `TreeScenes.cs`) |

## Using it

In a scene, a tree is a node and a forest is a scatter:

```csharp
// One tree: three levels of detail and a trunk capsule, generated when it becomes ready (or Bake it, below).
root.AddChild(new Tree3D { Preset = "Oak Medium", Seed = 1234, Style = TreeStyle.Realistic, Position = spot });

// Many trees: species (preset or options, the seeds of its variants) and placements, bucketed into 32 m chunks.
var forest = new TreeScatter
{
    Species =
    [
        new TreeSpecies { Preset = "Oak Medium", Seeds = [35729, 1201] },
        new TreeSpecies { Preset = "Pine Medium", Seeds = [13977, 52] },
    ],
};
forest.SetPlacements([new TreePlacement(position, yaw, scale, species: 1), ...]);
root.AddChild(forest);
```

The generator underneath, for tools and custom meshes:

```csharp
using MainframeEngine.Trees;

var options = TreePresets.Load("Oak Medium");       // a fresh, editable copy of the preset
options.Seed = 1234;                                // other seeds: other oaks of the same kind
var parameters = options.ToParams();

var generator = new TreeGenerator();                // reusable; one per thread
TreeMeshData[] lods = generator.Generate(parameters, TreeStyle.Realistic);   // skeleton + 3 LODs
ArrayMesh mesh = lods[0].ToArrayMesh(barkMaterial, leafMaterial);            // bark surface, then leaves
TreeTrunkCapsule trunk = lods[0].Trunk;             // Height (with caps) and Radius, metres
```

Finer control: `GrowSkeleton(parameters, seed, reuse)` does all RNG work and `Mesh(skeleton, parameters, detail)` meshes
it at any `TreeMeshDetail` (`TreeGenerator.RealisticLods`, `DefaultLods(parameters, style)`), so the levels of detail of
a tree come from one skeleton.

- **Units.** Ez Tree units are not metres. `TreeOptions.Scale` (0.3 by default; Oak Medium is then about 20 m tall)
  multiplies positions after meshing and never feeds the skeleton.
- **`TreeOptions`** mirrors Ez Tree's options with one `TreeLevel` sub-resource per branch level (`Level[0..3]`; level 0
  is the trunk), every generator input a `double`. Setters raise `Changed`, also for a level's properties. Edit a
  `Clone()`: `Resource.Duplicate` would share the `Level` array.
- **`EzTreeJson.Read(json)`** imports Ez Tree's preset JSON and the files its web app saves (keys missing from the JSON
  keep Ez Tree's defaults; the trellis is ignored with a warning).
- **`ToSurface()`** builds a `MeshSurface` from positions, normals, UVs, indices and the optional vertex streams
  (`Custom0`, and `Colors` for LowPoly blobs), sharing the arrays.

## The port

`tree.js` is followed function by function (`#generateSkeleton`, `#growBranch`, `generateChildBranches`,
`generateLeaves`, `#recordLeaf`, `#meshSkeleton`, `#meshBranch`, `#meshLeaf`, `shuffledIndices`), with Ez Tree's
comments kept. What keeps it exact:

- **RNG.** `EzRng` is `rng.js` bit for bit: the state is a signed `int` (JavaScript's `& 0xffffffff`), `>> 16` is an
  arithmetic shift, the multiplications run in `long` and wrap like `ToInt32`, and the result wraps to `uint`
  (`>>> 0`). Integer seeds only.
- **Doubles and three.js formulas.** The skeleton and the meshing run in doubles with three.js 0.167.1's own formulas
  (`ThreeMath`): `setFromEuler`/`setFromQuaternion` (through the rotation matrix, with the `0.9999999` gimbal threshold),
  `setFromAxisAngle`, `multiply`/`premultiply`, `slerp` with its edge cases, `applyQuaternion` (the r150+ form).
  JavaScript details that differ in .NET: `Math.round` rounds halves up (`ThreeMath.JsRound`), `Number.EPSILON` is
  2^-52, `divideScalar(s)` multiplies by `1 / s`, and `normalize()` divides a zero (or NaN) length by 1. Positions are
  rounded to float only when written, as Ez Tree's `Float32Array` does.
- **The branch queue is FIFO** (`Queue<T>`): its order is the RNG order.
- **Quirks kept**, so seeds match: the section length is not divided by `levels − 1` (Ez Tree compares the type with
  `'Deciduous'` while presets say `'deciduous'`); the orientation between two sections is `qB.slerp(qA, alpha)` (from
  B to A, opposite to the origin lerp); an evergreen's last ring has radius 0 and the step after it goes infinite or
  NaN, but its RNG draws still happen.
- **Not ported:** the trellis (its slot is `TreeParams.ExtraForce`, an `ITreeGrowthForce`, applied as Ez Tree's trellis
  force is), `bark.flatShading`, the materials and the GLB/PNG export.

The parity tests match Ez Tree's vertex and index counts and indices exactly for every preset and LOD, positions and
normals within 1e-4 (sampled), and rounded hashes of every position, normal and UV. On macOS (arm64) every float matches;
the hashes would name a platform where a one-ulp transcendental difference ever crosses a rounding boundary.

## Vertex data

| Stream | Bark | Leaf cards (Realistic) | Leaf blobs (LowPoly) |
|---|---|---|---|
| Normal | ring normals (Realistic) or split face normals (LowPoly) | Ez Tree's rounded normals (`LeafRoundedNormals`) | split face normals |
| UV | `u = j/segments · wrapsX`; `v` continuous (default) or Ez Tree's 0/1 per ring (`BarkUvMode`) | `(u, 1 − v)`: the engine's origin is top-left, so v = 0 at the leaf tip | `(0, 1 − h)`, `h` the height in the blob |
| `Custom0.x` wind weight | 0 at the trunk base, rising along each branch to 1 at the outermost tips | the branch's weight where the leaf sits | the blob's mean |
| `Custom0.y` | branch level / 4 (≤ 0.75) | 1 (foliage) | 1 |
| `Custom0.z` phase | a hash of the branch, inherited by terminal branches (no seam at junctions) | the branch's phase | a hash of the blob |
| `Custom0.w` AO | `0.6 + 0.4 · y / height` | 0.4 at the canopy centre to 1 on its hull (an ellipsoid around the leaf origins) | the same |
| `Colors` | empty | empty | `±8 %` grey shade per blob (multiplies the palette colour) |

The wind weight is a coordinate in "levels": a branch runs from its parent's value at the attachment point to the next
level (when a terminal branch continues it) or to `levels + 1` (a tip), divided by `levels + 1`. It is continuous at
every junction and monotonic along every branch. None of these values uses the RNG.

**Continuous bark V** is the arc length times `wrapsX / (2π · baseRadius)` (square texels at the branch base),
continued from the parent's V at the attachment point; the material's V repeat should be 1. `BarkTextureScale.y` is Ez
Tree's material repeat (`repeat.y = 1 / scale.y`) and only matches Ez Tree's look in `EzTree` mode.

## Styles and levels of detail

- **Realistic:** Ez Tree's meshing. LODs are its `defaultLODLevels`: LOD0 full; LOD1 every 3rd ring, 0.75 × sides,
  every 2nd leaf at 1.25 × size; LOD2 every 6th ring, 0.4 × sides, every 2nd leaf at 1.3 × size, single billboards.
  Oak Medium: 13 806 / 6 694 / 3 782 triangles.
- **LowPoly:** the same skeleton. Bark keeps every `LowPolySectionStride`-th ring and `LowPolySegmentFactor` of the sides
  (at least 3), drops branches thinner than `LowPolyMinBranchRadius` (the trunk always stays), and splits normals per
  triangle. Leaves become blobs: leaf origins are bucketed into `BlobSize` cells, the closest clusters (by centroid) merge
  until `MaxBlobs` remain (deterministic, ties by index), each becomes an ellipsoidal icosphere (`BlobDetail` 0: 20
  triangles, 1: 80), a `Hemisphere` (flat underside) or, for evergreens, a cone leaning half-way from up to its leaves'
  branches, sized from the leaves' spread plus half a leaf and jittered by a hash (`BlobJitter`). Triangles whose three
  vertices lie well inside another blob are dropped. LOD1 doubles the ring stride, takes 0.75 × the sides and halves the
  blobs (detail 0); LOD2 doubles the stride again, takes half the sides and quarters the blobs. Oak Medium: 3 024 /
  1 056 / 845 triangles.

`TreeMeshData.GeometricError` (engine units) is 0 at full Realistic detail and rises with the level: for the bark, the
largest distance of a dropped ring centre from the line between the kept rings plus the radius lost to fewer sides; for
leaf cards, the mean distance of the dropped leaves to the kept one before them; for blobs, the mean distance of the
leaves to their blob's centre. It is what G6.4's screen-space LOD selection will consume.

**Trunk capsule** (`TreeMeshData.Trunk`): the trunk radius 1 m above the ground, and a height from the ground to the
first side branch (at least 2 m or the trunk's height, whichever is lower, and at least the diameter). Godot's
`CapsuleShape3D` convention: the height includes the caps.

## Presets and content

The presets are `.mres` files, not embedded JSON: they load, duplicate, save and show in the inspector like any
resource, scenes can reference them by UID, and a `.mres` keeps doubles exactly (`69.60000000000001` stays). They were
made by `EzTreeJson.Read` from Ez Tree's JSON and `ResourceSaver.Save`; Ez Tree's JSON stays importable through
`EzTreeJson` for anything new. Engine-only fields (`Scale`, LowPoly) keep their defaults and are not written.
`TreePresets.Names` lists Ez Tree's display names in its order; `TreePresets.Load(name)` returns a fresh copy of the
engine's file (next to the application, also while the editor has a project open).

The textures are plain image files in the engine's `Content/Trees/` (Git LFS, like every PNG and JPG). Engine content
has no `.meta` sidecars: those only give project assets a UID for scene references. Code loads them with explicit import
settings, resolving against the application folder so an open project does not shadow them:

```csharp
var path = ContentPaths.Resolve("Content/Trees/Leaves/oak.png", ContentPaths.BaseDirectory);
var leaves = Texture2D.FromFile(path, new TextureImportSettings { FixAlphaBorder = true });
```

Bark normal maps are `_NormalGL` (+Y up, the engine's convention); `_Roughness` is single-channel.

## Tree3D

`Tree3D` (`[Tool]`, a `Node3D`) draws one generated tree:

| Property | Meaning |
|---|---|
| `Options` / `Preset` | The generator's inputs; with `Options` null, the preset of that name (trees naming a preset share one read-only copy: assign `Options = TreePresets.Load(name)` to edit) |
| `Seed` | −1 (default): the options' own seed |
| `Style` | `Realistic` or `LowPoly` |
| `BakedMesh` | A `TreeMesh` to draw as is: the generator never runs, and its style and options pick the materials |
| `BarkMaterial`, `LeafMaterial` | Replace the built-in materials (null: `TreeMaterials`) |
| `CastShadows`, `Collision` | Shadows of every level; the trunk capsule (default on) |
| `Lod1Distance`, `Lod2Distance`, `MaxDistance` | 30 m, 75 m (Ez Tree's 100 and 250 units × 0.3), 0 = drawn at any distance |

- **Levels of detail.** The tree generates its three `TreeMesh.Lods` when it becomes ready, in the editor and at run time,
  and again after a property or its `Options` change (`Changed`, also a level's edits), at most once per frame;
  `Regenerate()` does it now. Each level is an internal, unsaved `TreeLod3D` child (a `MeshInstance3D`: no owner, so the
  scene writer skips it) whose visibility range is `TreeMesh.LodRange(lod, …)`: [0, `Lod1Distance`),
  [`Lod1Distance`, `Lod2Distance`), [`Lod2Distance`, `MaxDistance`). The renderer measures the distance from the camera
  to the centre of an instance's bounds; the levels' meshes differ in bounds (fewer, larger leaves), so every level gets
  their union as `GeometryInstance3D.CustomAabb` and exactly one level draws at any distance — in the shadow maps too.
- **Sharing.** Trees with the same preset, seed and style share one generated `TreeMesh` (a weak cache), and every tree
  shares the `TreeMaterials` of its look, so equal trees batch into one instanced draw per surface.
- **Trunk.** An internal `StaticBody3D` with a `CapsuleShape3D` from `TreeMeshData.Trunk` (`TrunkShape`, `TrunkBody`),
  standing on the tree's origin.
- **Bake.** `Bake(path)` generates the tree from its options (ignoring a previous bake), makes the result its
  `BakedMesh` and saves it as `.mres`: a `TreeMesh` holds the three `ArrayMesh`es (bark surface 0, leaves surface 1, with
  `Custom0` and LowPoly `Colors`; no materials), the trunk capsule, the options (inline), seed, style and
  `TreeGenerator.GeneratorVersion`. A baked tree costs what any mesh costs: read and upload. There is no inspector UI
  yet and no staleness check (the version is stored for one).

## Materials

`TreeMaterials.Bark(options, style)` / `Leaves(options, style)` return shared materials, cached by what they read
(bark set, tint, textured; leaf texture, tint, cutoff; palette colours):

| Surface | Realistic | LowPoly |
|---|---|---|
| Bark | `FoliageMaterial3D`, `ShadingMode.Pbr`, opaque, `BackFace.Cull`, translucency 0: `_Color` (sRGB), `_NormalGL`, and `_Roughness` packed by `OrmPacker` into the new `FoliageMaterial3D.OrmTexture` (R = AO 1, G = roughness, B = metallic 0); `AlbedoColor` = `BarkTint` | `StandardMaterial3D`, PBR, `BarkPaletteColor`, roughness 0.9 |
| Leaves | `FoliageMaterial3D`, PBR, cut out at `LeafAlphaCutoff`, `BackFace.Flip`, translucency 0.5, roughness 0.65: the leaf PNG (`FixAlphaBorder`, clamped); `AlbedoColor` = `LeafTint` | `StandardMaterial3D`, PBR, `LeafPaletteColor` × the blobs' vertex colours (±8 % per blob), roughness 0.8 |

- **Bark sways.** Realistic bark uses the foliage wind with the same branch bend as the leaves. The bend is a lean
  downwind by `Custom0.x²`, and a leaf's `Custom0.x` and phase are its twig's, so leaves stay on their twigs while
  branches move; the trunk (weight 0 at the base, about 0.25 at the top of a three-level tree) barely moves. A static
  bark would leave the leaves bending off the twigs (up to 0.35 m per unit of wind strength), or need the leaves' bend
  turned off (Ez Tree's look: leaves flutter, nothing else moves). Bark shadows sway the same way (foliage casters).
- **ORM.** ambientCG ships roughness as a greyscale JPG; `OrmPacker.Pack(occlusion, roughness, metallic)` reads each
  map's red channel (a missing one is a constant: AO 1, metallic 0) into a linear, mipmapped RGBA texture, once per
  bark set at load. In PBR, `Foliage.vk.frag` multiplies the material's roughness and metallic and the vertex AO by
  `materialOrm` (a 1×1 white fallback without the map).
- `BarkTextureScale.y` is not applied (`FoliageMaterial3D` has no UV scale; continuous bark V already has square
  texels). LowPoly has no wind (`StandardMaterial3D`).

## TreeScatter

`TreeScatter` (`[Tool]`) draws forests: `Species` (`TreeSpecies` resources: `Options` or `Preset`, the `Seeds` of its
variants or `Baked` `TreeMesh` variants, `Style`, material overrides) and placements (`TreePlacement`: position, yaw,
uniform scale, species index; `SetPlacements`, saved with the scene as `PlacementData`, 6 floats each).

- **Bucketing.** Each placement goes to the chunk `ChunkOf(position, ChunkSize)` (scatter-local XZ ÷ 32 m by default,
  floored) and to a variant of its species by a hash of its position bits (`VariantOf`). Every non-empty (chunk, species,
  variant) gets one internal `TreeScatterBatch3D` (a `MultiMeshInstance3D`) per level of detail, all sharing the union of
  their bounds as `CustomAabb`, with the level's visibility range: a chunk draws one level, chosen by the camera's
  distance to the chunk's centre. Variants generate once per species (a few ms each) and are shared by every chunk.
- **Shadows.** Batches cast up to `ShadowMaxLod` (default 1): far chunks (level 2, from 75 m) do not cast. They sit at
  the end of the sun's default 100 m shadow range, where their leaf cards filled most of the last cascade (about 500 of
  the 2 000 trees) for shadows the distance and fog hide. The renderer culls casters per cascade by the batch's bounds:
  level-2 batches never reach cascade 0 (a self-check of the fly-through), but a 32 m chunk is all or nothing, so the
  near cascades draw every tree of the chunks they touch (about 180 trees in cascade 0 of the 2 000-tree fly-through;
  16 m chunks halve that for 3× the batches and no measurable gain). Leaf shadows sway (the foliage casters).
- **Collision.** One internal `StaticBody3D` per chunk with a `CollisionShape3D` per tree: the variant's trunk capsule
  (shared), scaled by the placement. Static shapes cost the physics step nothing while nothing moves near them, so every
  tree gets one (measured below).
- **Rebuild.** On ready, after a change (at most once per frame) or `Rebuild()`. Nothing runs per frame afterwards: the
  batches are static multimeshes (one comparison per frame each).
- No impostors yet: the far level is Ez Tree's LOD2 (Oak Medium 3 782 triangles), and `MaxDistance` ends the forest.

## Cost

- **Time** (Release, Apple Silicon, busy machine): skeleton plus three LODs takes about 3–5 ms for Oak Medium, Ash
  Large or Pine Medium in the Realistic style, and 5–15 ms in LowPoly (blob clustering dominates). Generation is
  synchronous; `TreeGeneratorTests.ReportsGenerationTime` prints the numbers.
- **Allocations.** A reused `TreeGenerator` and `TreeSkeleton` allocate only the output arrays: Oak Medium with three
  Realistic LODs allocates its 2.36 MB of arrays plus 576 B (the test's budget is + 16 KB). Arrays are counted first and
  allocated once at their exact size. LowPoly builds through reused lists and copies them out.
- **A 2 000-tree forest** (`tree-forest --count 2000`: 256 m square, oaks, pines and aspens with two seeds each, 32 m
  chunks: 64 chunks, 1 144 batches; Release, Apple M5, MoltenVK, 640×480, a fly-through at eye height, other GPU work
  running at times):

  | Sun shadows | Frame (wall clock, VSync off) | Shadow pass (GPU) |
  |---|---|---|
  | High (4 cascades × 2048², 100 m), far chunks not casting (default) | 26–27 ms | 23–24 ms |
  | High, every level casting (`ShadowMaxLod = 2`) | 29–30 ms | 26–27 ms |
  | 3 cascades × 2048², 100 m | 21 ms | 18 ms |
  | 2 cascades × 2048², 60 m | 15.6 ms | 13 ms |
  | 4 cascades × 1024², 100 m | 12.8–13.4 ms | 10.2–10.7 ms |
  | None | 8.4 ms (the display's 120 Hz) | — |

  At 1920×1080 the main pass grows by a few ms: 30 ms with the default shadows, 17.8 ms with 4 × 1024² (shadow pass
  10.9 ms), still display-capped without shadows.

  The CPU costs 0.75–0.9 ms per frame (p95 1.1–1.3 ms; 223 draws, 396 shadow draws) and allocates nothing. The GPU
  time is the shadow pass: alpha-tested leaf cards defeat the tile GPU's hidden-surface removal, so every leaf texel of
  every cascade runs the cut-out shader, and the cost follows cascades × resolution² × canopy overdraw (halving the
  resolution cuts it 2.5×; the wind's vertex work is about 10 %). A forest scene should use 2–3 cascades or 1024²
  maps; impostor or coarser-level shadow casters are the next step.
- **Collision** (Debug, `TreeScatterTests.ReportsCollisionCost`): the trunks of 2 000 trees (64 bodies) add about 15 ms
  to a scatter's build and 0.01 ms to a physics frame with a character walking among them.

## Testing

`build/ez-tree-reference.mjs` runs Ez Tree's own generator (`git clone`, `checkout` the pin, `npm ci`, then
`node build/ez-tree-reference.mjs <checkout>`); it registers a small Node module hook because Ez Tree's sources are
written for a bundler. It writes `Tests/Content/Trees/ez-tree-reference.json` (about 350 KB): RNG vectors and a hash of a
million draws, each preset's skeleton tips, and per preset and LOD the counts, hashes and every 97th position and normal.
Re-run it only when the pin moves, and review the diff. The C# tests use `BarkUv = EzTree` and `Scale = 1`.

The nodes (ADR 0158):

- **Unit:** `Tree3DTests` (streams on the surfaces, `LodRange`, unsaved levels with one shared `CustomAabb` and one
  level per distance, the trunk capsule hit by a ray and removed with `Collision`, the materials per style and their
  sharing, regeneration after seed and options edits, the bake round trip and a tree drawn from a bake alone);
  `TreeScatterTests` (chunk flooring, the variant hash, bucketing per chunk/variant/level with every tree in its
  chunk, materials and shared variants, `ShadowMaxLod`, a capsule per tree, saving the placements, the collision cost
  report); `OrmPackerTests`.
- **Render** (MoltenVK and lavapipe goldens): `tree-realistic` (an Oak Medium and a Pine Medium at level 0 at t = 1.5 s in a fixed
  wind; the still run must differ), `tree-lowpoly`, `tree-forest` (160 trees seen from the forest's edge; every chunk
  draws one level); `tree-forest --count 2000` as an allocation gate (0 B over 120 frames of the fly-through) and a CPU
  bar (< 4 ms in Release), with the per-cascade caster counts printed by the host. On a CPU device (lavapipe) the
  fly-through places at most 120 trees without sun shadows: the software rasterizer would need minutes for 2 000.

## Not yet

Impostors (G8b.6), `RuntimeGeneration` modes and bake staleness, `ForceLod`, the tree inspector (Bake and variants
buttons, LOD table) and its icons (`tree`, `trees`, `leaf` are not in the editor's icon atlas yet: `Tree3D` and
`TreeSpecies` use `feather`, `TreeScatter` `stack-2`, `TreeMesh` `package`), screen-space LOD selection from
`GeometricError` (G6.4), visibility-range fades, coarser shadow casters per level, LowPoly wind, `BarkTextureScale.y`,
generation on worker threads, and the coverage-preserving alpha mips for leaves.
