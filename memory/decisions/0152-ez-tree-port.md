# ADR 0152 — Ez Tree port: a literal, bit-exact C# generator with engine additions around it

- **Date:** 2026-10-08
- **Status:** accepted (CPU side built: generator, presets, parity tests; `Tree3D` and rendering in a later wave)
- **Milestone:** Gameplay toolkit G8b (G8b.1–G8b.2 CPU parts), forest slice wave 1 lane B
- **Spec:** docs/design/future/procedural-trees.md; current state in docs/design/procedural-trees.md; follows ADR 0149

## Context

ADR 0149 made procedural trees an engine feature built on a port of [Ez Tree](https://github.com/dgreenheck/ez-tree)
(MIT, 1.1.0, commit `dcf309b`). The proposal settled the shape of the port: literal, in doubles, with a bit-exact RNG,
Ez Tree's quirks kept, `Continuous` bark UVs by default, `TreeLevel` sub-resources, `Scale` 0.3 and no trellis. This
ADR records how that was built and the choices the proposal left open.

## Decision

- **Literal port in `MainframeEngine/Src/Trees/Generation/`** (namespace `MainframeEngine.Trees`, no engine types).
  `TreeGenerator` follows `tree.js` function by function. `EzRng` reproduces `rng.js` bit for bit (signed int32 state,
  arithmetic shifts, `long` products wrapped like `ToInt32`, `>>> 0` as a wrap to `uint`). `ThreeMath` ports the
  three.js 0.167.1 math the generator calls in doubles, operation for operation, including JavaScript's `Math.round`,
  `Number.EPSILON` and three.js's `divideScalar`/`normalize` behaviour. The branch queue is FIFO. Ez Tree's quirks stay
  (the never-matching `'Deciduous'` divisor, `qB.slerp(qA, alpha)`, the evergreen tip's NaN step).
- **Parity is checked against Ez Tree itself.** `build/ez-tree-reference.mjs` (run by hand, never in CI) loads Ez Tree's
  own sources through a Node module hook and writes `Tests/Content/Trees/ez-tree-reference.json`. The tests match counts
  and indices exactly, sampled positions and normals within 1e-4, and rounded hashes of all positions, normals and UVs.
  On macOS every float matches.
- **Skeleton and meshing are split** (`GrowSkeleton` does all RNG work; `Mesh` none), as Ez Tree's `generateLODs`
  does, so every level of detail comes from one skeleton. Ez Tree's `defaultLODLevels` are the Realistic LODs.
- **Engine additions consume no RNG:** `Custom0` (wind weight, level / 4, phase, AO) from skeleton data and hashes;
  LOD geometric errors; the trunk capsule; the LowPoly mesher (faceted bark, greedy centroid-merged leaf blobs, cones for
  evergreens); continuous bark V. So they cannot disturb parity.
- **Presets are `.mres` files** (`MainframeEngine/Content/Trees/Presets/`), not embedded JSON: they use the resource
  system (loading, UIDs, inspector, duplication) and keep doubles exactly. They were converted once with `EzTreeJson`,
  which also imports any JSON Ez Tree's web app saves.
- **Content lives in the engine's `Content/Trees/`** (as the lane brief asked): Ez Tree's four leaf PNGs (MIT) and only
  the three ambientCG bark sets the presets use (CC0), through Git LFS, with a `LICENSE.md` and `THIRD_PARTY_NOTICES.md`
  entries (Ez Tree, three.js, ambientCG). No `.meta` files: engine content is loaded by path with explicit settings.
- **Output stays engine-agnostic until the streams exist.** `TreeSurfaceData` keeps positions, normals, UVs, `Custom0`,
  `Colors` and indices; `ToSurface()`/`ToArrayMesh()` build `MeshSurface`s from the first four, and the second vertex
  stream (lane A2) gets `Custom0`/`Colors` at merge.
- **Allocation discipline:** output arrays are counted and allocated once at their exact size; a reused generator and
  skeleton add 576 B for Oak Medium with three LODs (budget + 16 KB).

## Deviations from the proposal

- `Custom0.y` is `level / 4` (the forest-slice contract) rather than `level / (levels + 1)`; leaves stay 1.
- Leaf and bark textures are in the engine's content, not the editor's (the lane brief's choice); the editor-copies-into-
  projects flow is not built.
- `TreeOptions`/`TreeLevel` carry no `[EditorIcon]`: `trees` and `leaf` are not in the editor's icon atlas yet.
- `TreeMeshDetail` is a record struct with `init` properties and a parameterless constructor (a positional record
  struct's `new()` would zero its defaults); it gained `MaxBlobs` for LowPoly LODs.
- The trellis slot is an `ITreeGrowthForce` on `TreeParams`, applied the way Ez Tree applies its trellis force.
- The trunk capsule is at least 2 m tall (or the trunk's height), so pines branching near the ground still block.
- `Resource.Duplicate(deep: true)` does not deep-copy resource arrays, so `TreeOptions.Clone()` exists for editable
  copies (`TreePresets.Load` returns one).

## Consequences

- Seeds and presets give Ez Tree's trees; moving the pin means re-running the fixture script and reviewing the diff.
- Wave 2 (`Tree3D`, `FoliageMaterial3D`) consumes `TreeMeshData` and maps `Custom0`/`Colors` into `MeshSurface.Custom0`/
  `Colors`; LowPoly runs on `StandardMaterial3D` with `Colors` as the per-blob shade.
- Generation is cheap enough to run synchronously in the editor (Realistic: about 3–5 ms for the skeleton and three
  LODs in Release; LowPoly 5–15 ms).
