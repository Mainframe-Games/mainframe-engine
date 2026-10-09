# Procedural trees

## Purpose

The engine generates trees from a seed and a set of options: a C# port of
[Ez Tree](https://github.com/dgreenheck/ez-tree) (MIT, © 2024 Daniel Greenheck), so a seed and one of Ez Tree's 15
presets give the same tree as in Ez Tree, and artists can tune trees in Ez Tree's web app and import the JSON. Around
the literal port sit the engine's additions: a LowPoly style, per-vertex wind data, LOD errors and a trunk capsule.

This page covers the CPU side (G8b.1–G8b.2 of the [proposal](future/procedural-trees.md),
[ADR 0152](../../memory/decisions/0152-ez-tree-port.md)). `Tree3D`, `FoliageMaterial3D`, wind rendering, impostors and
the tree inspector are not built yet; the proposal has their design.

## Layout

| Where | What |
|---|---|
| [`Src/Trees/Generation/`](../../MainframeEngine/Src/Trees/Generation/) | The port, namespace `MainframeEngine.Trees`, no engine types: `TreeGenerator`, `TreeParams`, `TreeMeshDetail`, `TreeSkeleton`, `TreeMeshData`/`TreeSurfaceData`/`TreeTrunkCapsule`, the enums (`TreeType`, `TreeBillboard`, `TreeStyle`, `BarkUvMode`, `BlobShape`), `ITreeGrowthForce`; internal `EzRng`, `ThreeMath` (`Vec3d`, `Quatd`, `EulerXyz`), `LowPolyMesher` |
| [`Src/Trees/`](../../MainframeEngine/Src/Trees/) | Engine side: `TreeOptions` and `TreeLevel` (resources), `TreePresets`, `EzTreeJson`, `TreeMeshDataExtensions` (`ToSurface`, `ToArrayMesh`) |
| [`Content/Trees/Presets/`](../../MainframeEngine/Content/Trees/Presets/) | The 15 presets as `TreeOptions` `.mres` files |
| [`Content/Trees/Leaves/`](../../MainframeEngine/Content/Trees/Leaves/), [`Content/Trees/Bark/`](../../MainframeEngine/Content/Trees/Bark/) | Ez Tree's leaf PNGs (MIT) and the three ambientCG bark sets the presets use (CC0); [`LICENSE.md`](../../MainframeEngine/Content/Trees/LICENSE.md) |
| [`build/ez-tree-reference.mjs`](../../build/ez-tree-reference.mjs) | Runs Ez Tree itself to write the parity fixture (by hand, never in CI) |
| [`Tests/MainframeEngine.Tests/Trees/`](../../Tests/MainframeEngine.Tests/Trees/) | Parity, RNG, determinism, allocation, LowPoly, options and preset tests; fixture in `Tests/Content/Trees/` |

## Using it

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
- **`ToSurface()`** builds a `MeshSurface` from positions, normals, UVs and indices, sharing the arrays. `Custom0` and
  `Colors` stay in `TreeSurfaceData` until the surface's optional vertex streams carry them.

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

## Cost

- **Time** (Release, Apple Silicon, busy machine): skeleton plus three LODs takes about 3–5 ms for Oak Medium, Ash
  Large or Pine Medium in the Realistic style, and 5–15 ms in LowPoly (blob clustering dominates). Generation is
  synchronous; `TreeGeneratorTests.ReportsGenerationTime` prints the numbers.
- **Allocations.** A reused `TreeGenerator` and `TreeSkeleton` allocate only the output arrays: Oak Medium with three
  Realistic LODs allocates its 2.36 MB of arrays plus 576 B (the test's budget is + 16 KB). Arrays are counted first and
  allocated once at their exact size. LowPoly builds through reused lists and copies them out.

## Testing

`build/ez-tree-reference.mjs` runs Ez Tree's own generator (`git clone`, `checkout` the pin, `npm ci`, then
`node build/ez-tree-reference.mjs <checkout>`); it registers a small Node module hook because Ez Tree's sources are
written for a bundler. It writes `Tests/Content/Trees/ez-tree-reference.json` (about 350 KB): RNG vectors and a hash of a
million draws, each preset's skeleton tips, and per preset and LOD the counts, hashes and every 97th position and normal.
Re-run it only when the pin moves, and review the diff. The C# tests use `BarkUv = EzTree` and `Scale = 1`.

## Not yet

`Tree3D` (edit-mode regeneration, bake, `RuntimeGeneration`), `FoliageMaterial3D` and the wind shaders, wiring
`Custom0`/`Colors` into `MeshSurface`'s vertex streams, impostors, the tree inspector and its icons (`tree`, `trees`,
`leaf` are not in the editor's icon atlas yet, so the resources show the default resource icon), and the coverage-
preserving alpha mips for leaves.
