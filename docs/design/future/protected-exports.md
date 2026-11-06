# Proposal: Protected exports

**Status:** accepted (2026-11-04)
**Milestone:** G19

## Problem

An exported game is easy to copy wholesale. Its `Assets/` folder ships as loose files (`.mscene` and `.mres` JSON,
`.rml`, PNGs, OGGs, `project.mfproj`), and the game's code is a managed assembly that ILSpy turns back into near-source
C#. Anyone, or any AI agent, pointed at the install folder gets the game's structure, data and code in minutes. Every
game made with the engine has this problem, so the engine should fix it in `mf-export`, not each game.

This is deterrence. The key that decrypts a pack ships inside the game, so a determined reverse engineer still gets
in. The goal is to remove the low-hanging fruit: no readable files, no decompilable code.

## Goals

- One content file-system seam that every runtime content read goes through. Mobile needs it anyway
  ([`IContentFileSystem`](mobile.md#platform-layer-and-project-structure), M12.1).
- Exports put the game's and the engine's `Assets/` and `project.mfproj` in one `Assets.mfpak` beside the executable.
  The format is the one [mobile](mobile.md#asset-pipeline) describes (M12.5), pulled forward for desktop.
- Packs are encrypted by default, with a stable key per project, so rebuilds are byte-identical and Steam patches stay
  small.
- A `nativeAot` preset key, once the engine is trim- and AOT-clean, so shipped game code is machine code.
- Nothing changes for game code, the editor or Play. Only exports change.

## Non-goals

- Anti-tamper, anti-cheat, DRM or licence checks. Steam's DRM wrapper remains the game's own choice.
- Resisting a determined reverser, a memory dump or a GPU capture. RenderDoc still sees textures and SPIR-V.
- Protecting game design. Gameplay can always be recorded and cloned.
- An IL obfuscator. The owner chose NativeAOT instead (it removes the IL, and obfuscation would break every
  name-based contract: `[Export]`, `[Signal]`, `[Rpc]`, save schemas).
- Cooking (KTX2 textures, binary scenes, `.mmesh`). That is mobile's `mf-cook` (M12.5). Packs carry the source formats.
- A mod-loading policy. A game that wants moddable files sets `pack: false` or lists them in `loose`.

## Design

### From a game developer's view

Export presets gain these keys (rows for [Game export → Preset schema](../game-export.md#preset-schema-projectmfproj--export)):

| Key | Targets | Default | Meaning |
|---|---|---|---|
| `pack` | all | `true` | Stage `Assets/` and `project.mfproj` into `Assets.mfpak`; no loose `Assets/` ships. `false` is today's layout. |
| `encrypt` | all | `true` | Encrypt the pack with the project's pack key. Ignored when `pack` is false. |
| `loose` | all | `[]` | Globs of `Assets/` files kept as loose files beside the pack (still readable at the same content path). |
| `nativeAot` | desktop | `false` | Phase 4: `-p:PublishAot=true`. Overrides `readyToRun`, `singleFile` and `trim`. |

A packed export folder holds the executable, the runtime and natives, `Assets.mfpak` and any `loose` files. Game code
does not change: it already loads content through `ResourceLoader`, `UiDocument.Source`, `AudioStream` paths and so on.

The project keeps one pack key beside its export presets, `export.packKey` in `project.mfproj` (32 random bytes,
base64). Project format 6's migration generates it once. The key is committed with the project: it ships inside the game anyway, and a committed
key makes CI exports reproduce. `MF_PACK_KEY` overrides it, like `MF_SIGN_IDENTITY`. The exported `project.mfproj`
(inside the pack) has no key and no presets.

### Content file system (phase 1)

Built (2026-11-04; see "Changes from the proposal, phase 1" below). `ContentFiles` (`MainframeEngine/Src/Core/Content/`)
is a static, read-only file system with mounts:

```csharp
public static class ContentFiles
{
    public static IDisposable Mount(IContentSource source, int priority = 0); // dispose to unmount
    public static Stream OpenRead(string path, FileShare share = FileShare.Read, int bufferSize = 4096);
    public static bool Exists(string? path);
    public static bool DirectoryExists(string? path);
    public static ContentFileInfo GetInfo(string path);              // exists, length, last write
    public static byte[] ReadAllBytes(string path);
    public static string ReadAllText(string path, Encoding? encoding = null);
    public static IEnumerable<string> EnumerateFiles(string directory, string pattern = "*", bool recursive = false);
    public static IEnumerable<string> EnumerateDirectories(string directory, string pattern = "*", bool recursive = false);
}
```

- Paths are what `File` takes: callers resolve `"Assets/…"` with `ContentPaths.Resolve` first, and other relative
  paths resolve against the working directory. Every call is a one-for-one swap of the `File`/`Directory` call.
- An `IContentSource` is mounted **at a directory** (its `Root`) and serves the absolute paths under it, so code that
  keeps absolute paths (`AssetDatabase.ToAbsolutePath`, `Texture2D`, shader paths) works unchanged. A path is looked up
  in the mounts whose root holds it, highest priority first, then on the disk. Paths outside every mount read the disk.
- With nothing mounted, every call is the `File`/`Directory` call it replaced. `Exists`/`GetInfo` on absolute paths
  allocate 0 B, mounted or not.
- `PackContentSource` (phase 2, built) reads a `.mfpak` and is mounted at `ContentPaths.BaseDirectory`. Loose files
  beside the executable (`loose`) are the disk fallthrough.
- `GameHost` mounts `Assets.mfpak` when it exists next to the executable, before it reads `project.mfproj`. Otherwise it
  mounts nothing and reads loose files, as before.

Every runtime **content** read moved to it. That means `ResourceLoader`, `AssetDatabase` (it reads the shipped
`assets.index.json`; `ToAbsolutePath` callers become `OpenRead` callers), `Texture2D`/`Png`, `Font`, `ShaderModuleCache`
and the canvas `Shader`, `UiFileInterface` and `RmlContentFileInterface` (RmlUi already reads through a managed
`Stream`, fonts included), `Tr`/`TranslationSet`/`FontFallbackTable`, `AudioStream`/`AudioDecoder` (streaming needs
seekable streams), `MusicManifest`/`AdaptiveMusic`, `SpineNode`/`SpineTextureLoader`, `SkyEnvironment`, `TerrainData`,
`LightProbeData`, `TreeMaterials`, `Label3D` and `ProjectSettings` loading.

- **Assimp.** `ModelImporter` imports at run time with `ImportFileExWithProperties(fullPath, …, (FileIO*)null, …)`. It
  gets an `aiFileIO` bridge over `ContentFiles` streams (`AssimpContentIO`, used only when the model lies under a
  mount), so glTF files with external `.bin` buffers and textures
  load from a pack.
- **Not content, so unchanged:** saves and `settings.json` (`DurableFile`, `ConfigFile`), logs, crash reports,
  recording, the Vulkan pipeline cache, bake caches, `steam_appid.txt`, native libraries (the OS loads them by path),
  and editor-only builds (`SurfaceShaderBuild`, `GameAssemblyLoader`).
- **Spine** reads through the vendored runtime's stream overloads (`SpineContent`). RmlUi's `LoadFontFace(path)` reads
  through the managed file interface, so fonts need no change.
- An audit test fails when `MainframeEngine/Src` calls `File.*`, `FileStream` or `Directory.*` outside an allowlist of
  those files, unless the line carries `// disk-io: <reason>`. It is a plain source scan, so no analyzer package is
  needed.

### Pack format (phase 2)

Built (2026-11-06); see "Changes from the proposal, phase 2" below and [Asset pipeline → Packs](../asset-pipeline.md#packs).

`.mfpak` v1 is little-endian and versioned:

- **Header.** Magic `MFPK`, format version, flags (encrypted), index offset and length, the index nonce, and a 16-byte
  key check (the first keystream block of a fixed nonce), so a wrong key fails at mount, not mid-load.
- **Entries.** Sorted by path. Each entry is stored or Brotli-compressed (`System.IO.Compression`, built in, so no new
  package). Already-compressed or streamed formats (`.ogg`, `.png`, `.jpg`, `.ktx2`, video) are stored and 16 KB aligned,
  as mobile needs, so they stay seekable. Compressed entries are decoded whole on open; they are small text and data
  files.
- **Index.** Every content path (`Assets/…`, `/`-separated, case-sensitive), with offset, stored and original length,
  codec and nonce. Directory listings come from it. It is compressed and, when the pack is encrypted, encrypted too, so
  file names are not visible.
- **Deterministic.** The same input and key give the same bytes: sorted order, no timestamps, nonces derived from
  content. A change to one asset changes only its entry, the index and the offsets after it.

`PackWriter` lives in the export library (`Tools/MainframeEngine.Export`); `PackReader` lives in the engine
(`Src/Core/Content/`). The export's stage step changes:

1. As today: `ContentFilter`, the exported `project.mfproj`, `assets.index.json`. `ContentFilter.Ships` already refuses
   the editor-only folder (`ContentPaths.IsEditorOnly`: `Assets/Video`, ADR 0241), and the build never copies it to the
   stage; the pack writer must not read the project's `Assets/` directly, or it would pack video mode's media.
2. With `pack`: write `<stage>/Assets.mfpak` from `Assets/` + `project.mfproj` minus `loose`, then delete what was
   packed. The engine's own `Assets/` (shaders, widgets, fonts) go in the same pack, under the same paths.

### Encryption (phase 3)

- **Cipher.** AES-256-CTR, with the keystream from `Aes.EncryptEcb` (built in; supported on every desktop and mobile
  target, with hardware AES on x64 and arm64). CTR can decrypt any byte range, so audio streaming and seeking work on
  encrypted entries. There is no authentication tag, because tamper resistance is a non-goal; the key check catches a
  wrong key.
- **Nonces.** Each entry's 12-byte nonce is the first 12 bytes of SHA-256(path ‖ content). It is unique per content and
  stable across builds. The 4-byte block counter limits an entry to 64 GB.
- **No allocation.** `PackStream` decrypts in place into the caller's buffer, using a pooled keystream buffer per stream.
  Reads allocate 0 B, so audio streaming stays inside the allocation gate.
- **Getting the key into the game, not the engine.** `mf-export` publishes with `-p:MainframePackKey=<base64>`.
  `MainframeGame.props` makes it a `CompilerVisibleProperty`, and `MainframeEngine.Generators` (already an analyzer of
  every game library) emits a `[ModuleInitializer]` in the game assembly. It registers the key with `ContentFiles`,
  split into XOR-masked constants. The module initializer runs when `GameHost.Run(args, typeof(X).Assembly)` loads the
  game assembly.
  - Never put the key in the runtimeconfig, an AppContext switch or any file beside the executable: those are plain
    JSON.
  - Builds without the property (the editor, Play, `dotnet run`) register no key and read loose files.
  - Until phase 4 the key is recoverable from the IL. That is accepted: the pack still defeats "read the folder", and
    NativeAOT buries the key in machine code.

### NativeAOT (phase 4)

- Set `IsAotCompatible` on `MainframeEngine` (and the generator's output), turning the trim and AOT analyzers on, then
  fix the warnings. Known sites: `TypeRegistry` fallbacks, JSON outside source-generated contexts, `SteamworksLoader`
  (loads a shim assembly by path; under AOT the shim must be linked statically per RID), Assimp and Silk.NET interop,
  SoundFlow, GetText.NET, Jitter2 and Box2D.NET.
- `nativeAot: true` publishes with `-p:PublishAot=true`. NativeAOT does not cross-compile between OSes, so the validator
  errors when the host OS is not the target's, and CI builds each OS on its own runner.
- Symbols: NativeAOT's `.pdb`/`.dbg`/`.dSYM` go into the `symbols: separate` zip, and `mf-symbolicate` learns native
  frames.
- This is M12.6's work ("trim/AOT analyzers clean") and C0's "desktop NativeAOT smoke". If either lands first, this phase
  shrinks to the preset key and its tests.

## Scenarios

### Phase 1: content file system

- Given a loose game, when `ContentFiles.OpenRead(ContentPaths.Resolve("Assets/UI/hud.rml"))` runs, then it returns
  the file's bytes, exactly as `ContentPaths.Resolve` would locate it. Test: `LooseSourceReadsAssetsPathsLikeContentPathsResolve`.
- Given `ContentPaths.ProjectDirectory` is set and a file exists only under `BaseDirectory`, when it is opened, then
  the base copy is read. Test: `LooseSourceFallsBackToTheBaseDirectory`.
- Given a legacy `"Content/x.png"` path, when it is opened, then `Assets/x.png` is read. Test:
  `LegacyContentPathsReadTheAssetsFolder`.
- Given a source is mounted, when a path outside its root is opened, then the disk is read and the source is not
  consulted. Test: `PathsOutsideEveryMountReadTheDisk`.
- Given a source is mounted, when an absolute path under its root is opened, then the source is read. Test:
  `AbsolutePathsUnderAMountReadTheMount`.
- Given a missing content path, when it is opened, then `FileNotFoundException` names the path and each mount
  searched. Test: `MissingContentNamesThePathAndTheMounts`.
- Given the engine sources, when the audit scans `MainframeEngine/Src`, then no file outside the allowlist calls
  `File.*`, `FileStream` or `Directory.*`. Test: `RuntimeContentCodeDoesNotCallFileApis`.
- Given an in-memory test source holding a glTF scene with an external `.bin` and a PNG, when
  `ResourceLoader.Load<PackedScene>` loads it, then the meshes and texture match loading the same files from disk. Test:
  `ModelImporterLoadsAGltfWithExternalBuffersThroughTheContentFileSystem`.

### Phase 2: packs

- Given a staged `Assets/` folder, when `PackWriter` writes it twice, then the two packs are byte-identical. Test:
  `PackWriterIsDeterministic`.
- Given a pack of a staged folder, when every file is read back, then contents, `Exists`, `DirectoryExists` and
  `Enumerate` (with patterns, recursive or not) match the folder. Test: `PackRoundTripsAStagedFolder`.
- Given an `.ogg` in a pack, when its stream seeks to random offsets and reads, then the bytes match the file, and the
  entry is stored and 16 KB aligned. Test: `StoredEntriesAreAlignedAndSeekable`.
- Given a template game with default presets, when it is exported, then the output has `Assets.mfpak`, no `Assets/`
  folder and no loose `project.mfproj`, and the game runs headless with `--max-frames` and exits 0. Test: the existing
  `ExportsATemplateGameForThisMachineAndRunsIt`, extended.
- Given a preset with `pack: false`, when it is exported, then the output layout is today's. Test:
  `PackFalseKeepsLooseAssets`.
- Given `loose: ["Assets/Mods/**"]`, when the game reads `Assets/Mods/a.json`, then the loose file wins over the pack,
  and the pack does not contain it. Test: `LooseGlobsShipBesideThePackAndStillLoad`.
- Given a packed export of a game that uses RmlUi documents, fonts, `.mo` catalogs and streamed music, when it starts,
  then its UI, translations and music load from the pack. Test: `PackedGameLoadsUiFontsCatalogsAndStreamsFromThePack`
  (a headless test host over a pack built in the test).

### Phase 3: encryption

- Given an encrypted pack of files containing known strings, when the raw pack bytes are searched, then no file path
  and no known plaintext appears. Test: `EncryptedPackContainsNoPlaintextPathsOrContent`.
- Given an encrypted pack, when it is mounted with a different key, then the mount fails with `PackKeyMismatch` before
  any read, and `GameHost` logs it and exits non-zero. Test: `WrongKeyFailsAtMountWithAClearError`.
- Given a stable key, when the pack is written twice, then it is byte-identical; when one asset changes, then only that
  entry, the index and later offsets change. Tests: `EncryptedPackIsDeterministicForAStableKey`,
  `ChangingOneAssetChangesOnlyItsEntryAndTheIndex`.
- Given a format-5 project, when it is migrated, then it gains a pack key; when the saved project is loaded again,
  then the key is unchanged. Test: `FormatSixMigrationAddsAPackKeyOnce`.
- Given a project file on disk without a pack key, when `mf-export` runs a preset with `encrypt: true`, then validation
  fails with `PackKeyMissing` and a fix (`mf-export --init-pack-key`, or open the project in the editor). Test:
  `ExportWithoutASavedPackKeyFailsWithAFix`.
- Given `MF_PACK_KEY` is set, when exporting, then the pack is encrypted with it, not the project's key. Test:
  `PackKeyEnvironmentVariableOverridesTheProject`.
- Given an encrypted export, when every output file is searched for the key's raw bytes and its base64, then none
  contains them, and the packed `project.mfproj` has no key and no presets. Tests:
  `ExportedFilesDoNotContainThePackKey`, `ExportedProjectFileOmitsThePackKey`.
- Given a game library built without `MainframePackKey`, when it is run from the editor or Play, then no key is
  registered and loose files load as today. Test: `BuildsWithoutAPackKeyReadLooseFiles`.

### Phase 4: NativeAOT

- Given `nativeAot: true` on a host of the target OS, when the template game is exported, then the output has no
  managed game or engine assembly, and the game runs headless and exits 0. Test: `NativeAotExportHasNoManagedAssemblies`.
- Given `nativeAot: true` for another OS's target, when validating, then it fails with `NativeAotNeedsTargetHost`. Test:
  `NativeAotNeedsAHostOfTheTargetOs`.
- Given `nativeAot: true` with `readyToRun` or `singleFile` set, when validating, then a warning says they are
  ignored. Test: `NativeAotIgnoresReadyToRunAndSingleFile`.

## Testing

- **Allocation gate:** `PackStream` reads (stored, encrypted and compressed entries) allocate 0 B after open, measured
  with `AllocationGate.SmallestWindow`, because audio streaming reads them on its worker.
- **Benchmark:** a new `ContentLoadBenchmarks`: scene, texture and UI load from loose files, a stored pack, a compressed
  pack and an encrypted pack. Load time from a pack must not regress against loose files by more than 10 % (to confirm
  on the baseline machine).
- **Render tests** stay on loose files (unchanged). The Demo gets a packed export smoke test: `just demo-screenshots`
  run against a packed, encrypted export must match its loose screenshots (a new `just export-smoke` recipe, run
  locally; CI's template job covers the template game).
- **CI:** the template job's export (`build/template-smoke.sh`) becomes packed and encrypted by default, so every PR
  exercises the default path on Linux. Phase 4 adds a NativeAOT export per OS runner. GitHub Actions minutes are scarce,
  so phase 4's runs are one job per OS, not per preset.
- Gates as usual: `just build`, the Release build, `just test`, `just test-render`, `just format-check`,
  `just shaders-check`.

## Human checklist

- [ ] Point ILSpy and an AI agent at a packed, encrypted export folder, before and after NativeAOT, and record what each
      recovers (the point of this work).
- [ ] Upload two builds that differ by one asset to a Steam depot branch and check the patch size SteamPipe reports.
- [ ] Load times of the Forest and Neon showcases from a pack feel no slower than from loose files, on a Mac and on
      Windows.
- [ ] Music streaming and seeking from an encrypted pack sound clean (no glitches at seek points).

## Task list

- [x] Phase 1: `ContentFiles`, `IContentSource`, `MemoryContentSource`; move every runtime content read; the
      Assimp `aiFileIO` bridge; the audit test; update `docs/design/asset-pipeline.md` and AGENTS.md's project structure.
- [x] Phase 2: `.mfpak` v1 (`PackWriter` in the export library, `PackReader`/`PackContentSource` in the engine);
      `GameHost` mounting; the `pack` and `loose` preset keys; stage changes; `ContentLoadBenchmarks`; `just export-smoke`;
      `game-export.md` rows.
- [ ] Phase 3: AES-256-CTR `PackStream`; project format 6 with the pack key and its migration; `--init-pack-key`;
      `MF_PACK_KEY`; `MainframePackKey` through `MainframeGame.props` and the generator's module initializer; the
      `encrypt` preset key; validator issues.
- [ ] Phase 4: `IsAotCompatible` and the analyzer fixes (shared with M12.6/C0); the static Steamworks shim under AOT; the
      `nativeAot` preset key; NativeAOT symbols in `symbols: separate` and `mf-symbolicate`; a NativeAOT export per OS in
      CI.

## Changes from the proposal, phase 1

- **Mounts are anchored at a directory.** The engine's content identity is an absolute path almost everywhere, so
  rather than converting it to content paths, a source serves the absolute paths under its root. "Rooted paths bypass
  the mounts" became "paths outside every mount read the disk" plus "absolute paths under a mount read the mount".
- **No `IContentFileSystem`/`ContentFileSystem.Current` or `LooseContentSource`.** The static `ContentFiles` with
  mounts is the seam; the disk fallthrough is the loose source. Mobile can put its own source behind a mount.
- **Paths resolve like `File`, not like `ContentPaths.Resolve`.** Resolving inside `ContentFiles` changed the meaning
  of repo-relative paths (`mf-shaders check` reported every shader stale), so callers resolve first, as they did.
- **`SurfaceShaderBuild`** stays on the disk for the build and check, but its run-time lock read goes through
  `ContentFiles`.

## Changes from the proposal, phase 2

- **The writer is in the engine** (`Src/Core/Content/PackWriter.cs`), not the export library: engine tests and
  benchmarks build packs with it, and mobile's `mf-cook` will too. The export's stage step calls it.
- **Anchors instead of plain packing** ([ADR 0247](../../../memory/decisions/0247-pack-anchors-for-steampipe.md)). This
  answers the SteamPipe open question. Valve's uploading guide says SteamPipe diffs in ~1 MB chunks and recommends a
  1 MB padding alignment and per-asset compression. Laying entries out back to back would shift every later chunk after
  one change, and even "never cross a page" re-flows every later page. So entries of a page or more, and about one
  entry per 16 MB picked by its path's hash and its own size, start on a page boundary: a change re-lays out entries
  only up to the next anchor. `InsertingAnAssetShiftsEntriesAfterTheNextAnchorByWholePages` checks it.
- **Codec choice.** Media extensions are always stored; other files are Brotli only when it saves 1/16 or more.
- **Header.** The flags field is `PackOptions` (CA1711). The phase 3 fields (index nonce, key check) are written now,
  so the format does not change for encryption.
- **The packed-game scenario** is the existing mounted-content tests (UI documents and fonts, catalogs, streamed audio,
  glTF with external buffers, Spine, scenes) run as theories over the in-memory source and a real pack, rather than one
  new test host.
- **`mf-export --pack-list` and `--pack-extract`**, not in the proposal: the template smoke checks a packed export
  through them, and developers can see what shipped.
- **`just export-smoke`** compares 8 of 12 Demo scenes pixel for pixel (all 8 match exactly): the physics, particle and
  tile-map scenes differ between two runs of the same loose build, so they are run from the pack and checked for errors
  only.
- **`ContentLoadBenchmarks`** compares loose files and a pack (scene text, PNG decode, UI text); the stored-versus-
  compressed split is the codec each file gets, and the encrypted case comes with phase 3.
- **`assets.index.json` and the UID map** live only inside the pack: `AssetDatabase` reads them through `ContentFiles`,
  and the packed template export runs (`ExportsATemplateGameForThisMachineAndRunsIt`).

## Open questions

- Is Brotli enough, or is a zstd or LZ4 package (needs the dependency discussion) worth it for load time? Owner.
  Phase 2's `ContentLoadBenchmarks` on the M5 MacBook (2026-11-06, not re-checked on a fully quiet machine): a 512² PNG
  decode 2.79 → 2.85 ms (+2 %, stored); a UI document 14.2 → 8.5 µs (faster); a 200 KB scene's text 103 → 125 µs
  (+21 %, about 22 µs: the Brotli entry is decoded whole into its own buffer, ~200 KB more allocated). Only Brotli text is
  over the 10 % target, and by microseconds per file.
- ~~SteamPipe chunking~~: answered in phase 2 (anchors, ADR 0247); the human checklist's depot upload confirms it.
- Does the editor save a project right after migrating it, so the format-6 key persists without an explicit save?
  Agent checks in phase 3; if not, the editor saves after generating the key.
- Does phase 4 stay here, or move to M12.6 with this proposal only adding the preset key? Owner.
- ~~Should `assets.index.json` and the UID map live only inside the pack?~~ Yes; confirmed in phase 2 (nothing reads
  them by OS path).

## Related

- [Game export](../game-export.md), [ADR 0229](../../../memory/decisions/0229-export-runner-in-csharp.md),
  [ADR 0230](../../../memory/decisions/0230-export-open-questions.md)
- [Mobile](mobile.md): `IContentFileSystem`, `.mfpak`, `mf-cook` and AOT;
  [ADR 0100](../../../memory/decisions/0100-mobile-strategy.md)
- [Consoles](consoles.md) (C0: desktop NativeAOT smoke)
- [Crash reporting → Symbols](../crash-reporting.md#symbols), [Asset pipeline](../asset-pipeline.md),
  [Distribution (NuGet)](../distribution-nuget.md)
