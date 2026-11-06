# Asset Pipeline

## Purpose

This doc covers how source files (images, models) become engine resources:

- where paths resolve;
- how `.mfmeta` sidecars carry UIDs and import settings;
- which importer handles which file;
- how imports are cached.

Scene and resource files (`.mfscene`/`.mfres`) are covered in [Scene serialization](scene-serialization.md). This page
covers everything `ResourceLoader` *imports*.

## Paths

- `ContentPaths` resolves engine files (shaders, built-in content) against the application folder. It never uses
  the working directory.
- `AssetDatabase.Current` is rooted at `ContentPaths.BaseDirectory`, the folder holding `Assets/`. Since M3 it no
  longer uses the working directory either, so a game loads the same files however it is launched (`dotnet run`, an
  IDE, a test runner, a published app). The render-test host and games no longer change directory at startup.
- Tools assign `AssetDatabase.Current` to the open project. Tests assign it to a temporary project.
- Relative asset paths (`"Assets/Models/Crate.gltf"`) are project-relative with `/` separators. UIDs resolve
  through the database first, and the path is only a hint.
- The content folder is `Assets/` (ADR 0197; `ContentPaths.FolderName`). Before project format 4 it was `Content/`;
  `ContentPaths.UpgradeLegacyPath` still reads `Content/…` as `Assets/…`, in `ContentPaths.Resolve`, the RmlUi file
  interface and `AssetDatabase.ToAbsolutePath`. The editor renames an old project's folder when it opens it
  ([Projects](editor.md#projects)).

## File extensions

Every extension the engine, editor, video library or tools define starts with `.mf` (ADR 0249). The constants are in
`FileExtensions`, and `FileExtensionRuleTests` fails on a new extension constant without the prefix, unless the test's
exempt list names it with a reason.

| Extension | What it is | Before ADR 0249 |
|---|---|---|
| `.mfscene` | a scene (JSON, its own UID) | `.mscene` |
| `.mfres` | a resource (JSON, its own UID) | `.mres` |
| `.mfmusic` | an adaptive music manifest | `.mmusic` |
| `.mfsong` | an editor song | `.msong` |
| `.mfsave` | a save slot | `.msave` |
| `.mfmeta` | an imported asset's sidecar: UID and import settings | `.meta` |
| `.mfprobes` | a light-probe bake's coefficients (the data file of an `.mfres`) | `.probes` |
| `x.slang.mfspvlock` | a surface shader's build lock | `x.slang.spvlock` |
| `x.slang.mflayout.json` | a surface shader's reflected layout | `x.slang.layout.json` |
| `.mfproj` | the project file | |
| `.mfpak` | an export's content pack | |
| `.mftimeline` | a video timeline | |
| `.mfcap.json` | a recording's capture sidecar | |
| `.mftranscript.json` | a media file's cached transcript | |

Third-party formats keep their names (`.slang`, `.spv`, `.rml`, `.rcss`, `.po`/`.mo`, Spine's `.atlas`/`.skel`, media),
as do temporary and fixed names (`.tmp`, `.bakN`, `.partial`, `.stems`, `shaders.lock`).

Projects made before ADR 0249 still load:

- `FileExtensions.Matches` accepts the legacy name wherever a loader dispatches on the extension (`ResourceLoader`, the
  asset scan, export staging, the l10n extractor).
- `FileExtensions.FindExisting` reads a legacy sidecar (`x.png.meta`, `x.slang.layout.json`, `x.slang.spvlock`) when
  there is no current one.
- `AssetDatabase.ToAbsolutePath` and `ContentPaths.Resolve` send a legacy path whose file is gone to the renamed file,
  so `ChangeSceneToFile("Assets/Scenes/Main.mscene")` and an old `mainScene` keep working after the migration.
- `SaveServer` renames `slot.msave` and its copies to `.mfsave` once at start.
- The editor offers `FileExtensionMigration` when it opens such a project ([Projects](editor.md#projects)).

## Content file system

Every runtime content read goes through `ContentFiles` (`Src/Core/Content/`, G19 phase 1,
[ADR 0239](../../memory/decisions/0239-protected-exports.md)): shaders and their locks, textures, scenes and resources,
the asset index and `.mfmeta` files, RmlUi documents, stylesheets and fonts, audio (streamed too), music manifests, `.mo`
catalogs and locale folders, Spine exports, terrain and light-probe data, models, and `project.mfproj`. It replaces the
`File`/`Directory` read calls one for one: `Exists`, `DirectoryExists`, `GetInfo` (length and last write),
`OpenRead` (seekable), `ReadAllBytes`, `ReadAllText`, `EnumerateFiles`, `EnumerateDirectories`.

- **Paths stay what they were.** It takes the paths `File` takes, so each call site is a one-for-one swap: absolute
  paths (what `ContentPaths.Resolve` and `AssetDatabase.ToAbsolutePath` return, `Texture2D`'s file path, a shader's
  SPIR-V paths) are used as they are, and any other relative path resolves against the working directory, as `File`
  would (tools such as `mf-shaders check` pass repository-relative paths). Resolve `"Assets/…"` paths first.
- **Mounts.** `ContentFiles.Mount(IContentSource, priority)` serves the paths under the source's `Root` directory until
  the returned handle is disposed: a path under a mounted root is looked up in the mounts holding it (highest priority
  first, then mount order), then on the disk. Paths outside every mount, and every path when nothing is mounted, read the
  disk exactly as before. Enumeration unions the mounts and the disk, ordinal-sorted. An export's
  `Assets.mfpak` is mounted at `ContentPaths.BaseDirectory` ([Packs](#packs)); tests mount a `MemoryContentSource` or
  a pack.
- **Native readers.** RmlUi reads documents and font faces through the managed file interface, which opens
  `ContentFiles` streams. Assimp is given an `aiFileIO` (`AssimpContentIO`) when the model lies under a mount, so a glTF's
  `.bin` buffers and images come from the mount too; otherwise it opens the path itself. Spine exports are read through
  the vendored runtime's stream overloads (`SpineContent`).
- **Threads and allocation.** Mounts are an immutable array swapped under a lock, so the loader worker and the audio
  streamer read while a mount changes. `Exists` on an absolute path allocates nothing (the canvas renderer asks while
  drawing); sources take `ReadOnlySpan<char>` paths for it.
- **Not content.** Writes (saves, `settings.json`, logs, crash reports, recordings, the pipeline cache, bake caches,
  editor saves) and native libraries stay on `File`. A read that must see the disk carries a `// disk-io: <reason>`
  comment, and `ContentIoAuditTests.RuntimeContentCodeDoesNotCallFileApis` fails on any other `File`/`Directory` read in
  `MainframeEngine/Src` outside its list of user-data and build files.
- **Games** read their own data files through `ContentFiles` too, so they keep loading once an export packs them.

### Packs

An export's `Assets.mfpak` (G19 phase 2, [Game export → Packed content](game-export.md#packed-content)) is read by
`PackReader` and mounted as a `PackContentSource` (`Src/Core/Content/`). `PackWriter` writes it; the export's stage step,
tests and `ContentLoadBenchmarks` use it.

- **Format v1** (`PackFormat`, little-endian): a 64-byte header (`MFPK`, version, options, the index's offset, stored
  and decoded lengths, the index nonce, 4 reserved bytes, a 16-byte key check), the entries in ordinal path order, then
  the Brotli-compressed index (count; per entry its UTF-8 path, offset, stored length, length, codec and a 12-byte
  nonce). The nonce is the first 12 bytes of SHA-256(path ‖ content), and the key check is zero until phase 3 encrypts
  packs.
- **Codecs.** Media that is already compressed or is streamed with seeks (`.ogg`, `.wav`, `.mp3`, `.flac`, `.opus`,
  `.png`, `.jpg`, `.webp`, `.ktx2`, video, `.zip`) is stored 16 KB aligned. Everything else is Brotli (quality 9, window
  22) when that saves at least 1/16 of the file, else stored. Stored entries are read straight from the pack; Brotli
  entries are decoded whole when opened.
- **Layout for patches** ([ADR 0247](../../memory/decisions/0247-pack-anchors-for-steampipe.md)). SteamPipe diffs in
  ~1 MB chunks. An entry of a page (1 MB) or more, and about one entry per 16 MB chosen from its path's hash and its own
  size (`PackFormat.IsAnchor`), starts on a page boundary. A changed, added or removed asset re-lays out only the entries
  up to the next anchor; later entries move by whole pages, so their chunks still match. The padding costs about 3 %.
- **Deterministic.** The same files give the same bytes: sorted paths, no timestamps, content-derived nonces.
- **Reads.** One read-only handle per pack and positional reads (`RandomAccess`), so the loader worker and the audio
  streamer read at once. A stored entry's stream reads and seeks without allocating, and lookups take
  `ReadOnlySpan<char>` (0 B). Every packed file reports the same last write time.
- **Errors.** A file that is not a pack, a newer format, an encrypted pack (until phase 3) or a damaged index or entry
  throws `InvalidDataException` naming the pack when it is opened, not mid-load.

## Importers

`ResourceLoader.Load` hands every file that is not `.mfscene`/`.mfres` to the `IAssetImporter` registered for its
extension (`AssetImporters`). The importer receives:

- the absolute path;
- the project path;
- the file's `.mfmeta` sidecar (`AssetMeta`: `uid`, `importer`, `settings`).

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
  "settings": { "scale": 1, "generateNormals": true, "importMaterials": true, "optimizeMeshes": false,
                "importAnimations": true, "optimizeAnimations": true, "clips": [ { "name": "run", "loop": "Linear" } ],
                "shading": "blinnPhong", "lodErrors": [], "generateLods": false } }
```

Models import through **Silk.NET.Assimp** ([ADR 0013](../../memory/decisions/0013-assimp-for-model-import.md)). The
post-processing steps are:

- Triangulate, join identical vertices, sort by primitive type, improve cache locality, validate.
- `FlipUVs`, because Vulkan samples with a top-left UV origin, like glTF.
- Optionally, smooth normals and mesh optimisation.
- `LimitBoneWeights` (four influences per vertex), and FBX pivots not preserved (`IMPORT_FBX_PRESERVE_PIVOTS` = 0).

| Assimp | Engine |
|---|---|
| file | root `Node3D` named after the file, `Scale` = `scale`; Assimp's synthetic root is skipped when it is empty |
| node | `Node3D`, or `MeshInstance3D` when it has meshes; transform decomposed into position / rotation / scale |
| the node's meshes | one `ArrayMesh`, one `MeshSurface` per Assimp mesh; nodes using the same meshes share it; points and lines are dropped |
| material | `StandardMaterial3D`: name, base colour or diffuse (linear → sRGB), opacity, emissive + intensity, shininess, two-sided, glTF alpha mode (`MASK` → cutout, `BLEND` → blend) and cutoff |
| textures | base colour or diffuse → albedo, normals → normal map, emissive → emission. Files next to the model load through `ResourceLoader`, so their own `.mfmeta` applies; embedded images (`*N`, e.g. GLB, or by file name, e.g. FBX) decode in memory |
| bones (ADR 0208) | one `Skeleton3D` at the armature node (bones, rests); nodes under bones → `BoneAttachment3D`s |
| skinned meshes | a `MeshInstance3D` under the skeleton with a `Skin` (inverse bind matrices) and `MeshSurface.Joints`/`Weights` |
| animations | `Animation`s (named after the actions) in an `AnimationLibrary` on an `AnimationPlayer` child of the root; loop modes and events from a `-loop` suffix, glTF animation `extras` and the `.mfmeta` `clips` |

Skeletons, skins and clips are described in [Animation and skinning → Import](animation.md#import).

#### PBR materials (G6.1)

`"shading": "pbr"` imports materials as `ShadingMode.Pbr` (the default `"blinnPhong"` keeps the old look until the owner
flips it, [Rendering features](rendering-features.md#open-questions)):

- `Metallic` from `$mat.metallicFactor` (0 without one), `Roughness` from `$mat.roughnessFactor`, or from the shininess
  (√(2 / (n + 2))) for files without it (FBX, OBJ);
- glTF's `metallicRoughnessTexture` (Assimp's `Metalness`/`DiffuseRoughness`, `Unknown` in older builds) and
  `occlusionTexture` (`Lightmap`, or `AmbientOcclusion`) become one `OrmTexture`: the image itself when both are the same
  packed ORM file, else `OrmPacker.PackGltf` (occlusion's R, the metallic-roughness map's G and B; nearest resampling to
  the larger size).

#### Authored levels of detail (G6.4)

Sibling mesh nodes named `<name>_LOD0` … `<name>_LOD7` merge into one `MeshInstance3D` `<name>` whose surfaces carry the
other levels as `MeshLod`s with their own geometry; `"lodErrors"` gives their errors as fractions of the LOD0 bounds
radius (default 1 %, doubling). `"generateLods"` is reserved (no simplifier yet). Details:
[Rendering features → Authored LODs on import](rendering-features.md#authored-lods-on-import).

`PackedScene.Instantiate()` on an imported scene clones the imported template. Exported properties are copied, so
meshes, materials and textures are shared, and every node is owned by the instance root, as for scene files. A
scene that instances a model stores only the model's UID and path and the instance's overrides, never mesh data
(see [Scene serialization](scene-serialization.md#file-format)).

### Import cache

`ModelImporter` keeps a cache of imported templates, keyed by full path, file size, modification time and settings
(at most 32 entries, oldest dropped first). Loading a model again, or after the loader released it, does not re-run
Assimp. A changed file or `.mfmeta` re-imports. `ResourceLoader.ClearCache()` clears it, and so does engine shutdown.
`ModelImporter.ImportCount` and `CacheCount` are for diagnostics and tests.

There is no on-disk cache of imported data: scenes stay JSON and never bake binary data
([ADR 0011](../../memory/decisions/0011-json-scenes-no-binary-bake.md)).

## The test model

`Tests/MainframeEngine.Tests/TestAssets/TestModel.cs` generates a small CC0 glTF (`test_model.gltf`/`.bin`, a
checker PNG and their `.mfmeta` files). It contains a two-level node hierarchy, one box mesh shared by two nodes, a
two-primitive mesh with an opaque double-sided material and a blended one, and an empty node. The committed copy
lives in `Tests/Assets/Models/TestModel/` (the shared test assets) and in `Examples/Demo/Assets/Models/TestModel/`, where the Demo uses it. To
regenerate it, run `UPDATE_TEST_ASSETS=1 dotnet test Tests/MainframeEngine.Tests --filter TheCommittedTestModel`.
A unit test fails if the committed copy and the generator drift apart.

`SkinnedCharacter.cs` next to it generates the skinned character (`Tests/Assets/Models/Character/character.glb`, also
in the Demo): a 31-bone Blender-style rig, a two-primitive skinned mesh of about 6 000 triangles and ten clips
([Animation and skinning → Testing](animation.md#testing)).

## Known issues

- Assimp is a native library from the `Ultz.Native.Assimp` package (loaded by Silk.NET's resolver). Where it
  cannot load, model imports throw `InvalidOperationException` and the import tests fail. See the M3 risks in the
  progress log for the Linux CI loader.
- Not imported: morph targets, cameras, lights, glTF sampler settings (the image's `.mfmeta` decides), and UV sets beyond
  the first. PBR data is read only with `"shading": "pbr"`; generated LODs need a mesh simplifier (not chosen yet).
- Silk.NET's animation key structs are larger than the bundled Assimp's; the importer detects the key stride from the
  data (ADR 0208).

## Related docs

[Materials & meshes](materials-and-meshes.md) · [Scene serialization](scene-serialization.md) ·
[GPU resources](gpu-resources.md) · [Build & platforms](build-and-platforms.md)
