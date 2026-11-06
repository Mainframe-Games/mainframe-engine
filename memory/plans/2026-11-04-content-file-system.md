# Plan: Content file system (G19 phase 1)

**Status:** done (2026-11-04)
**Docs:** [Proposal: Protected exports](../../docs/design/protected-exports.md) (accepted 2026-11-04; "Content
file system (phase 1)", phase 1 scenarios and task list), [ADR 0239](../decisions/0239-protected-exports.md),
[Mobile → platform layer](../../docs/design/future/mobile.md#platform-layer-and-project-structure) (`IContentFileSystem`),
[Asset pipeline](../../docs/design/asset-pipeline.md), [Testing](../../docs/design/testing.md)
**Started:** 2026-11-04

## Goal

Every runtime content read in `MainframeEngine/Src` goes through `ContentFiles`, a read-only file system with mounts.
Without a mount, which is every build after this phase, it reads the disk exactly as today: no behaviour change, and
render goldens unchanged. A mount can serve any path under its root directory, which is what phase 2's
`Assets.mfpak` needs. An audit test keeps new `File.*` content reads out.

### Refinement to the proposal: mounts are anchored at a directory

The engine's content identity is an absolute path almost everywhere: `AssetDatabase.ToAbsolutePath`,
`Texture2D._filePath`, `Shader.VertexSpvPath`, `ContentPaths.Resolve`. Rather than converting all of them to content
paths, a mount covers a real directory (phase 2 mounts the pack at `ContentPaths.BaseDirectory`):

- a path, rooted or in any `ContentPaths.Resolve` form, is resolved to an absolute path;
- if it lies under a mount's root, mounts are asked in priority order, then the disk;
- otherwise it is read from the disk.

So callers keep their paths and only swap `File.X` for `ContentFiles.X`. This changes one proposal scenario: "rooted
paths bypass the mounts" becomes "paths outside every mount read the disk" (`PathsOutsideEveryMountReadTheDisk`), plus
`AbsolutePathsUnderAMountReadTheMount`. The proposal's "Changes from the proposal" section records it at ship.

## Not in scope

- The `.mfpak` format, `PackContentSource`, `GameHost` mounting, preset keys: phase 2.
- Encryption and the pack key: phase 3. NativeAOT: phase 4.
- Write paths: saves, settings, logs, crash reports, recordings, caches, bakes and editor saves stay on `File.*`.
- Game code that reads files itself: documented, not enforced.
- The editor and tools projects (`MainframeEngine.Editor`, `Tools/`): they read sources on disk.

## Steps

- [x] **`Src/Core/Content/`.** Add:
  - `IContentSource`: `Root` (an absolute directory) and `FileExists`, `DirectoryExists`, `OpenRead` (seekable),
    `GetInfo` (exists, length, last write; a pack reports a fixed time), `EnumerateFiles`, `EnumerateDirectories`. All
    take paths relative to `Root`, `/`-separated.
  - `ContentFiles` (static): `Mount(IContentSource, int priority)` returning an `IDisposable` unmount; `OpenRead`,
    `ReadAllBytes`, `ReadAllText`, `Exists`, `DirectoryExists`, `GetInfo`, `EnumerateFiles`, `EnumerateDirectories`
    (union of mounts and disk, de-duplicated, ordinal order). `ContentFileSystem` implements `IContentFileSystem` and
    `ContentFiles` forwards to `ContentFileSystem.Current`, so mobile can replace the stack.
  - `MemoryContentSource` (internal, `InternalsVisibleTo` the tests): an in-memory source for tests now and as a
    reference for phase 2.
  - Mounting is thread-safe: an immutable mount array swapped with `Volatile`, so the audio streaming thread and the
    loader worker can read while `GameHost` mounts.
  - `Exists`/`GetInfo` on an already-absolute, normalised path allocate 0 B when there are no mounts and when the path
    is under a mount (span prefix comparison, no `Path.GetFullPath`), because `VulkanCanvasRenderer` calls `Exists`
    while drawing.

  Verified by: `Tests/MainframeEngine.Tests/Core/Content/ContentFilesTests.cs` with
  `LooseSourceReadsAssetsPathsLikeContentPathsResolve`, `LooseSourceFallsBackToTheBaseDirectory`,
  `LegacyContentPathsReadTheAssetsFolder`, `PathsOutsideEveryMountReadTheDisk`, `AbsolutePathsUnderAMountReadTheMount`,
  `HigherPriorityMountsWinAndTheDiskIsLast`, `EnumerateUnionsMountsAndDisk`, `MissingContentNamesThePathAndTheMounts`,
  `UnmountingRestoresDiskReads`, and `ExistsOnAbsolutePathsAllocatesNothing` (`AllocationGate.SmallestWindow`).
- [x] **Resources and project.** `ResourceLoader`, the read side of `AssetDatabase` (index, `.meta`,
  `ReadEmbeddedUid`, `Scan`'s enumeration; meta creation and moves stay on disk), `ResourceSaver`'s existence check,
  `FontFallbackTable`, `ProjectSettings.Load`/`TryLoad`, and `ContentPaths.Resolve`'s project-directory probe. Verified
  by: the existing `Resources/`, `Project/` and `Localization/` tests, plus `ResourceLoaderReadsScenesFromAMount` (a
  `.mscene` with a sub-resource, served by a `MemoryContentSource`).
- [x] **Rendering.** `ShaderModuleCache`, `Engine` (line 523), `GpuTexture`, `SkyEnvironment`, `Texture2D` (including SVG
  sizing and the streamed decode), the canvas `Shader` and its layout, `VulkanCanvasRenderer`, `MeshRenderer.Spatial`,
  `Png`'s read paths, `LightProbeData`, the read side of `TerrainData`, and `TreeMaterials`. Verified by:
  `just test-render` with goldens unchanged, its validation gate and its 0 B allocation gate, plus
  `TextureLoadsFromAMount`.
- [x] **Text and UI.** `Font`, `Label3D`, `UiFileInterface` and `RmlContentFileInterface` (it returns
  `ContentFiles.OpenRead` streams), `RmlPaths`, and `UiServer`'s font folder enumeration. `UiHotReload` stays on disk
  (it watches source folders). Confirm by reading RmlUi 6.3's `FontProvider` that `LoadFontFace(path)` reads through
  the file interface; if it does not, load faces from memory instead (`LoadFontFace` from bytes), and write it into
  this plan. Verified by: the `UI/` tests (`SerialRmlUi`), plus `UiDocumentAndFontLoadFromAMount`.
- [x] **Audio.** `AudioStream` (`GetInfo` for the stream threshold), `AudioDecoder`'s stream, `MusicManifest`,
  `AdaptiveMusic`. Verified by: the `Audio/` tests, plus `StreamedAudioSeeksThroughAMount` (an `.ogg` in a
  `MemoryContentSource`, read and seeked by the streamer).
- [x] **Localization.** `Tr`'s locale folder enumeration and catalog checks, and `TranslationSet`. Verified by: the
  `Localization/` tests, plus `CatalogsAndLocalesComeFromAMount`.
- [x] **Spine.** `SpineNode` and `SpineSprite` use the vendored runtime's stream overloads:
  `Atlas(TextReader, imagesDir, loader)`, `SkeletonJson.ReadSkeletonData(TextReader)` and
  `SkeletonBinary.ReadSkeletonData(Stream)`. Also `SpineTextureLoader` and `SpineNode`'s folder listing. The vendored
  plugin is not modified. Verified by: the Spine tests and render tests, plus `SpineFolderLoadsFromAMount`.
- [x] **Models.** `ModelImporter` and `ModelAnimationImport`:
  - The template cache keys on `ContentFiles.GetInfo` (length + last write).
  - When the path lies under a mount, Assimp imports through an `aiFileIO` bridge (`[UnmanagedCallersOnly]` open,
    read, seek, tell, size and close callbacks over `ContentFiles.OpenRead` streams, a `GCHandle` per open file, under
    the existing `Gate`). Otherwise it passes the path, as today.

  Verified by: `ModelImporterLoadsAGltfWithExternalBuffersThroughTheContentFileSystem` (proposal scenario: a glTF with
  an external `.bin` and a PNG in a `MemoryContentSource`, compared with loading the same files from disk), and the
  existing import tests.
- [x] **Disk-only reads are marked.** Reads that must stay on disk get a trailing `// disk-io: <reason>` comment: crash
  reports (`CrashPrompt`), profiler traces (`ChromeTraceWriter`), `SteamAppIdFile`, editor saves (`TerrainData`'s copy
  of the previous carve records, `AssetDatabase` moves and meta writes), `UiHotReload`, `DebouncedFileWatcher`.
  Verified by: the audit test below.
- [x] **Audit test.** `RuntimeContentCodeDoesNotCallFileApis` scans `MainframeEngine/Src/**/*.cs` for the read APIs
  (`\bFile\.(Exists|Open|OpenRead|OpenText|ReadAll\w*|ReadLines)\(`, `new FileStream\(`, `new FileInfo\(`,
  `\bDirectory\.(Exists|Enumerate\w*|Get\w*)\(`, `new DirectoryInfo\(`). It fails on any hit outside `Core/Content/` and
  the write-side files (`Saves/`, `Recording/`, `DurableFile`, `ConfigFile`, `FileLogSink`, `SurfaceShaderBuild`,
  `GameAssemblyLoader`, `PipelineCache`, `TreeBakeCache`, `SilkNativeResolver`, `VulkanLoaderBootstrap`,
  `SteamworksLoader`), unless the line carries `disk-io:`. The message lists every `file:line`. Verified by: it passes,
  and it fails when a `File.ReadAllBytes` is added to `Texture2D` (checked once by hand, then reverted).
- [x] **Docs.**
  - `docs/design/asset-pipeline.md` gets a "Content file system" section: the API, mounts anchored at a directory, the
    disk fallthrough, `disk-io:`, and what is not content.
  - AGENTS.md: "Load content with `ContentPaths.Resolve`" becomes "read content through `ContentFiles`", and
    `Src/Core/Content/` goes into Project Structure.
  - `project-and-gamehost.md`: games read their own data files through `ContentFiles` so they keep working when packed.
  - The proposal's phase 1 task list item is ticked.

  Verified by: `bash .agents/mainframe-workflow/scripts/check.sh`, and `just docs-build` for the generated site.
- [x] **Gates.** `just build`; `dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false`;
  `just test`; `just test-render`; `just format-check`; `just shaders-check`; `check.sh`. Plus `just test-linux`
  (case-sensitive file system) and `just template-smoke` (an export still runs). Demo QA: `just demo-screenshots`
  matches `docs/images/demo` (no visual change expected).

## Done when

- `RuntimeContentCodeDoesNotCallFileApis` passes, and every new test named in Steps exists and passes.
- `just test-render` passes with no golden re-recorded, and its allocation gate is green.
- With no mount registered, `git diff` shows no behaviour change outside `Core/Content/`: every changed call site is a
  `File.X` → `ContentFiles.X` swap, the Spine stream overloads, or the Assimp bridge that only runs under a mount.
- All gates listed above pass, including `just test-linux` and `just template-smoke`.
- The asset pipeline doc, AGENTS.md and `project-and-gamehost.md` describe `ContentFiles`.

## Open questions

- Packs are case-sensitive. Should `ContentFiles` warn in Debug builds when a path only matches the disk case-
  insensitively, so Windows and macOS developers find packed-export breakage early? Agent decides in phase 2, with
  the owner told.
- Does any native library besides Assimp open content by path (ThorVG for SVG, FreeType through RmlUi)? Checked in the
  Text/UI and Rendering steps, and the answer is written here.
