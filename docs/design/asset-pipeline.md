# Asset Pipeline

## Purpose

This doc covers how source files (images, models) become engine resources:

- where paths resolve;
- how `.meta` sidecars carry UIDs and import settings;
- which importer handles which file;
- how imports are cached.

Scene and resource files (`.mscene`/`.mres`) are covered in [Scene serialization](scene-serialization.md). This page
covers everything `ResourceLoader` *imports*.

## Paths

- `ContentPaths` resolves engine files (shaders, built-in content) against the application folder. It never uses
  the working directory.
- `AssetDatabase.Current` is rooted at `ContentPaths.BaseDirectory`, the folder holding `Content/`. Since M3 it no
  longer uses the working directory either, so a game loads the same files however it is launched (`dotnet run`, an
  IDE, a test runner, a published app). The render-test host and games no longer change directory at startup.
- Tools assign `AssetDatabase.Current` to the open project. Tests assign it to a temporary project.
- Relative asset paths (`"Content/Models/Crate.gltf"`) are project-relative with `/` separators. UIDs resolve
  through the database first, and the path is only a hint.

## Importers

`ResourceLoader.Load` hands every file that is not `.mscene`/`.mres` to the `IAssetImporter` registered for its
extension (`AssetImporters`). The importer receives:

- the absolute path;
- the project path;
- the file's `.meta` sidecar (`AssetMeta`: `uid`, `importer`, `settings`).

The loader gives the result the file's `ResourcePath` and the meta's UID, and caches it like any other resource:
by UID and path, reference-counted.

| Importer | Extensions | Result | Settings type |
|---|---|---|---|
| `TextureImporter` (`texture`) | `.png .jpg .jpeg .tga .bmp` | `Texture2D` (pixels decoded on first use) | `TextureImportSettings` |
| `ModelImporter` (`model`) | `.gltf .glb .fbx .obj .dae` | imported `PackedScene` (`IsImported`) | `ModelImportSettings` |

Games can `AssetImporters.Register` their own importers.

### Textures

```json
{ "uid": "tex_7e57c4ec4e70", "importer": "texture",
  "settings": { "colorSpace": "auto", "mipmaps": true, "filter": "linear", "wrap": "repeat", "anisotropy": 8 } }
```

| Key | Values (default) | Effect |
|---|---|---|
| `colorSpace` | `auto`, `srgb`, `linear` (`auto`) | `auto`: sRGB in colour slots (albedo, emission), UNORM in data slots (normal maps) — see [Materials & meshes](materials-and-meshes.md#texture2d) |
| `mipmaps` | bool (`true`) | full mip chain generated on the GPU (blits) |
| `filter` | `linear`, `nearest` (`linear`) | sampler filter |
| `wrap` | `repeat`, `clamp`, `mirror` (`repeat`) | sampler addressing |
| `anisotropy` | 1–16 (`8`) | clamped to the device; 1 = off |
| `svgScale` | > 0 (`1`) | `.svg` only: rasterisation scale (Godot's `svg/scale`); SVGs are rasterised at load by `mfsvg` (ThorVG as in Godot, ADR 0112) |
| `fixAlphaBorder` | bool (`false`) | Godot's `process/fix_alpha_border`: nearly transparent pixels take their nearest opaque neighbour's colour |

Missing keys take the defaults. Invalid values are logged and replaced by the default, and unknown keys are
ignored with a warning. `TextureImportSettings.ToMetaSettings()` writes the object, for tools. Changing
`Texture2D.ImportSettings` at runtime re-uploads the texture.

### Models

```json
{ "uid": "mdl_7e57a55e7000", "importer": "model",
  "settings": { "scale": 1, "generateNormals": true, "importMaterials": true, "optimizeMeshes": false } }
```

Models import through **Silk.NET.Assimp** ([ADR 0013](../../memory/decisions/0013-assimp-for-model-import.md)). The
post-processing steps are:

- Triangulate, join identical vertices, sort by primitive type, improve cache locality, validate.
- `FlipUVs`, because Vulkan samples with a top-left UV origin, like glTF.
- Optionally, smooth normals and mesh optimisation.

| Assimp | Engine |
|---|---|
| file | root `Node3D` named after the file, `Scale` = `scale`; Assimp's synthetic root is skipped when it is empty |
| node | `Node3D`, or `MeshInstance3D` when it has meshes; transform decomposed into position / rotation / scale |
| the node's meshes | one `ArrayMesh`, one `MeshSurface` per Assimp mesh; nodes using the same meshes share it; points and lines are dropped |
| material | `StandardMaterial3D`: name, base colour or diffuse (linear → sRGB), opacity, emissive + intensity, shininess, two-sided, glTF alpha mode (`MASK` → cutout, `BLEND` → blend) and cutoff |
| textures | base colour or diffuse → albedo, normals → normal map, emissive → emission. Files next to the model load through `ResourceLoader`, so their own `.meta` applies; embedded images (`*N`, e.g. GLB, or by file name, e.g. FBX) decode in memory |

`PackedScene.Instantiate()` on an imported scene clones the imported template. Exported properties are copied, so
meshes, materials and textures are shared, and every node is owned by the instance root, as for scene files. A
scene that instances a model stores only the model's UID and path and the instance's overrides, never mesh data
(see [Scene serialization](scene-serialization.md#file-format)).

### Import cache

`ModelImporter` keeps a cache of imported templates, keyed by full path, file size, modification time and settings
(at most 32 entries, oldest dropped first). Loading a model again, or after the loader released it, does not re-run
Assimp. A changed file or `.meta` re-imports. `ResourceLoader.ClearCache()` clears it, and so does engine shutdown.
`ModelImporter.ImportCount` and `CacheCount` are for diagnostics and tests.

There is no on-disk cache of imported data: scenes stay JSON and never bake binary data
([ADR 0011](../../memory/decisions/0011-json-scenes-no-binary-bake.md)).

## The test model

`Tests/MainframeEngine.Tests/TestAssets/TestModel.cs` generates a small CC0 glTF (`test_model.gltf`/`.bin`, a
checker PNG and their `.meta` files). It contains a two-level node hierarchy, one box mesh shared by two nodes, a
two-primitive mesh with an opaque double-sided material and a blended one, and an empty node. The committed copy
lives in `Tests/Content/Models/TestModel/` (the shared test assets) and in `Examples/Demo/Content/Models/TestModel/`, where the Demo uses it. To
regenerate it, run `UPDATE_TEST_ASSETS=1 dotnet test Tests/MainframeEngine.Tests --filter TheCommittedTestModel`.
A unit test fails if the committed copy and the generator drift apart.

## Known issues

- Assimp is a native library from the `Ultz.Native.Assimp` package (loaded by Silk.NET's resolver). Where it
  cannot load, model imports throw `InvalidOperationException` and the import tests fail. See the M3 risks in the
  progress log for the Linux CI loader.
- Not imported: skinning, animations, cameras, lights, PBR metallic/roughness maps, glTF sampler settings (the
  image's `.meta` decides), and UV sets beyond the first.

## Related docs

[Materials & meshes](materials-and-meshes.md) · [Scene serialization](scene-serialization.md) ·
[GPU resources](gpu-resources.md) · [Build & platforms](build-and-platforms.md)
