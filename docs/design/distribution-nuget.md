# Distribution via NuGet — how games consume the engine

Games reference the engine as the **`MainframeEngine` NuGet package** (nuget.org), with no checkout of this repo
and no editor needed; **`MainframeEngine.Templates`** (same version) provides `dotnet new mfgame`. Both are owned by the
[Mainframe-Games](https://www.nuget.org/profiles/Mainframe-Games) organization on nuget.org. The editor is a separate, optional download whose New Project creates package-based games
at its own version. Project references into a checkout remain for working on the engine itself
(`dotnet new mfgame --engine-source project`). Decision: [ADR 0188](../../memory/decisions/0188-engine-nuget-package.md).

```bash
dotnet add package MainframeEngine          # any .NET 10 project
dotnet new install MainframeEngine.Templates  # or the template: a whole game project
dotnet new mfgame -n MyGame                   # references MainframeEngine of the template's version
```

## The package

The engine is more than a DLL: it has per-platform natives, engine content (compiled shaders, UI, fonts), a Roslyn
source generator that game code must run, and build steps. The package carries all of it:

| Path | Contents | How a game gets it |
|---|---|---|
| `lib/net10.0/` | `MainframeEngine.dll` + `.xml` docs; `spine-csharp.dll` (the vendored Spine runtime, no package of its own) | compile + run references |
| `runtimes/<rid>/native/` | `mfrmlui`, `mfsvg`, `enet` and Valve's `steam_api` (vendored, [Steamworks → Natives](steamworks.md#natives)) for `win-x64`, `linux-x64`, `osx-arm64`, `osx-x64` (the same universal files: NuGet does not pick osx-x64 assets for arm64) | NuGet runtime assets: listed in the app's `deps.json` for RID-less `dotnet run/test`, copied for `dotnet publish -r <rid>` |
| `analyzers/dotnet/cs/` | `MainframeEngine.Generators.dll` | runs on every project that references the package: `[Export]`, `[Signal]`, `[Replicated]`, `[Rpc]` |
| `mainframe/Content/**` | exactly what the engine project copies to `<output>/Content` in a checkout build: `.spv` shaders (not the `.slang` sources), UI widgets, fonts, trees, brand | `buildTransitive/MainframeEngine.targets` copies it to the app's `Content/` (build and publish) |
| `mainframe/steamworks/{win64,posix}/` | `MainframeEngine.Steamworks.dll` (the shim) + `Facepunch.Steamworks.Win64.dll` / `.Posix.dll`: two builds of one assembly, so neither is in `lib/` ([Steamworks → Layers](steamworks.md#layers)) | `buildTransitive/MainframeEngine.targets` copies them to the app's `steamworks/` like Content (a build with a RID gets only that platform's folder); the engine's `SteamworksLoader` loads this OS's |
| `tools/mf-l10n/` | `mf-l10n` (managed files + runtimeconfig; no `deps.json`, so the host loads the folder's assemblies and none of the natives it never calls) | `Localization.targets` compiles `Content/locale/**/*.po` to `.mo` with it |
| `tools/package-game.sh`, `tools/macos/png-to-icns.sh` | player builds: `build/package-game.sh` and the icon converter it runs ([Player builds](#player-builds)) | `MainframePackageGameScript` (set by `MainframeGame.props`) names the script |
| `buildTransitive/` | `MainframeEngine.props` (imports `MainframeGame.props`), `MainframeEngine.targets`, `Localization.targets`, `macos/` | imported by every project that references the package, directly or through a game library |
| `README.md`, `icon.png` | the nuget.org page ([`MainframeEngine/PACKAGE.md`](../../MainframeEngine/PACKAGE.md)) and the logo | — |

Dependencies (Silk.NET, SoundFlow, Jitter2, Box2D.NET, GetText.NET, NVorbis, StbImageSharp, ENet-CSharp without its
natives and build files) are ordinary package dependencies with the versions from `Directory.Packages.props`; exact pins
(`[1.4.1]`) stay exact. Facepunch.Steamworks is not a dependency: it is vendored, and the package carries it in
`mainframe/steamworks/`.

### Content and natives: the same layout as a checkout

Engine content lands in the **same `Content/` folder** as the game's, exactly where a checkout build puts it, so
`ContentPaths` and every engine path work unchanged. The package's targets only copy into **app projects**
(Exe/WinExe, `IsTestProject`, or `MainframeEngineCopyContent=true`; `false` turns it off). A game library needs no
engine content, and the desktop project that references it would otherwise receive it twice.

The `mainframe/Content` list is not maintained by hand. The engine's pack target (`_MainframePackContent`) takes this
project's copy-to-output items whose target path is under `Content/`, the same items a project reference copies, minus
`.pot` sources. A new engine content file therefore ships in the package automatically.

Natives need no targets. A checkout build copies them flat because project references cannot express RID-specific
assets; for a package, NuGet's `runtimes/` handling does the job (`SilkNativeResolver` and `ModelImporter` probe
`runtimes/<rid>/native` as well).

### Build integration

The checkout's game-facing build files are packed unchanged, so package and checkout games build the same way:

- **`MainframeGame.props`**, which checkout games import from their `Directory.Build.props`, provides the `DEMO` define
  and `AssemblyMetadata` from `"isDemo"`, the app name and icon from `project.mfproj`, the macOS development `.app`
  bundle, and `CopyLocalLockFileAssemblies`. All of it is inert without a `project.mfproj` above the project. It also
  sets `MainframePackageGameScript`: the checkout's `build/package-game.sh`, or the package's `tools/package-game.sh`.
- **`Localization.targets`** sees `MfL10nPath` already set (to `tools/mf-l10n`), so it skips the `mf-l10n` project
  reference it uses in a checkout. Same `LocaleContentRoot`, same fallback to committed `.mo` files.

### Player builds

A package game makes the same player archives as a checkout game ([Release → Games](release.md#games)), with the
script the package ships. `MainframePackageGameScript` gives its path, so nothing hard-codes the package cache:

```bash
script="$(dotnet msbuild MyGame.Desktop -getProperty:MainframePackageGameScript)"
bash "$script" MyGame.Desktop/MyGame.Desktop.csproj osx-arm64 builds   # or win-x64, linux-x64; --sign / --notarize
```

Run it with `bash`: NuGet drops the executable bit. The archives are `<Exe>-<version>-osx-arm64.zip` (the `.app`),
`-win-x64.zip` and `-linux-x64.tar.gz`, with this RID's natives (Valve's `steam_api` included) and `steamworks/` folder.

### Packing

`build/pack-nuget.sh [version] [out]` (`just pack`) packs both packages at one version in three steps:

1. It builds `mf-l10n`. That project references the engine, so the engine's own pack cannot build it: the reference
   would wait on the project instance that is packing.
2. It runs `dotnet pack MainframeEngine -c Release -p:Version=…`.
3. It runs `dotnet pack MainframeEngine.Templates` at the same version. The packed `template.json` defaults
   `EngineVersion` to that version (`_MainframeStampTemplateVersion`; the source keeps `0.0.0-dev`). A template installed
   from `MainframeEngine.Templates` X.Y.Z therefore creates games on `MainframeEngine` X.Y.Z with no options.

In the engine csproj:

- `IsPackable` is false in `Directory.Build.props`; only the engine and `MainframeEngine.Templates` opt in.
- The spine-csharp project reference is `PrivateAssets="all"` only while packing (`_IsPacking`). Its DLL goes into
  `lib/`, and checkout projects still compile against Spine types (`SpineNode.State`).
- `GenerateDocumentationFile` is on. CS1591 (undocumented member), CS1573 (unlisted parameter) and CS0419 (a cref to an
  overload group) are suppressed; every other doc-comment check is an error.
- Symbols ship as a `.snupkg` with SourceLink (`PublishRepositoryUrl`, `EmbedUntrackedSources`).
- Packing is always Release, so the Debug-only `MainframeContentSource` attribute (UI hot reload from the checkout)
  never reaches a package.

## Versioning and release

One version for the editor, the engine and the templates: the release tag `vX.Y.Z` ([Release & versioning](release.md)).
[`publish.yml`](../../.github/workflows/publish.yml) works as follows:

- Its `package` job packs `MainframeEngine.X.Y.Z.nupkg` + `.snupkg` and `MainframeEngine.Templates.X.Y.Z.nupkg`.
- The `release` job attaches the `.nupkg` files to the GitHub Release.
- After the tag and the release exist, the same job pushes both packages to nuget.org. nuget.org packages can only be
  unlisted, so a failed release must never leave one behind.

**Trusted Publishing, no stored API key.** The `release` job has `id-token: write`. `NuGet/login` sends GitHub's OIDC
token (repository, workflow, environment) to nuget.org, which checks it against a trusted publishing policy and returns
an API key valid for one hour. The policy on nuget.org (username → **Trusted Publishing**) is set as follows:
- **Policy owner:** `Mainframe-Games`, the organization.
- **Repository owner:** `Mainframe-Games`.
- **Repository:** `mainframe-engine`.
- **Workflow file:** `publish.yml`.
- **Environment:** `release`.
- **Scopes:** new packages and new versions, glob `MainframeEngine*`.

There are no secrets. `NUGET_USER` in `publish.yml` (`brogan`) is the nuget.org username of the member who created the
policy. A policy stays active while that member belongs to the organization; if someone else re-creates it, change
`NUGET_USER` to their username.

**Ownership.** On nuget.org a package belongs to the owner that first pushes its id, here the policy's owner. The
`Authors` metadata ("Mainframe Games") is only display text. Both ids therefore belong to the organization, and its
members manage them. Reserving the `MainframeEngine` id prefix (a request to nuget.org) is optional: it adds the
verified check mark and stops others from using `MainframeEngine.*`.

## Template and editor

- **Template.** `dotnet new install MainframeEngine.Templates` (nuget.org), from a checkout, or the copy the editor
  ships. `dotnet new mfgame` defaults to `--engine-source package`, which writes
  `<PackageReference Include="MainframeEngine" Version="$(MainframeEngineVersion)" />`. The version comes from
  `--engine-version`, which defaults to the template package's own version. From a checkout's source template the
  default is `0.0.0-dev`, so pass a release, or use `--engine-source project --engine-path …`.
  - The game's `Directory.Build.props` sets `ImportDirectoryPackagesProps=false`, so a game inside a repository that
    uses central package management still builds.
  - Details: [Projects & GameHost → Template](project-and-gamehost.md#template).
- **Editor.**
  - The published editor ships the template (`Templates/mfgame`).
  - New Project's Engine row chooses between two sources:
    - **package**: MainframeEngine at the editor's own version. It is the default in a release editor and needs no
      checkout.
    - **engine checkout**: forced in development builds (`0.0.0-dev` has no package).
  - Details: [Editor → Projects](editor.md#projects).

## Tests

- `build/template-smoke.sh` runs in two modes. `ENGINE_SOURCE=project` is `just template-smoke`.
  `ENGINE_SOURCE=package` (`just template-smoke-package`, CI job `template` on Linux/lavapipe) works like this:
  1. It packs this checkout as `0.0.0-smoke.<timestamp>` into a local feed (`build/pack-nuget.sh`: both packages).
  2. It installs the template from the packed `MainframeEngine.Templates` and creates the game with no options. It
     checks that the game references `MainframeEngine` of that version (the stamped default) and adds a `nuget.config`
     for the feed.
  3. It builds the game, runs 30 frames (no `[ERROR]`, a screenshot) and publishes it for the host RID.
  4. It checks the engine content, the compiled `.mo`, `mfrmlui` and the `steamworks/` shim builds in both outputs.
  5. It packages the game for linux-x64, win-x64 and osx-arm64 with the script `MainframePackageGameScript` names (it
     must be the package's `tools/package-game.sh`) and checks each archive: executable, natives, Valve's `steam_api`
     for that platform, its `steamworks/` folder, none of the other platforms' Steam files, no `steam_appid.txt`.
  6. It removes the version from the package cache.
- Editor tests cover `NewProjectValidation.IsPackageVersion` and the per-source validation and `dotnet new` arguments.

## Not yet

- **An "upgrade project" action.** Moving a package game to another engine version means editing
  `MainframeEngineVersion` in its `Directory.Build.props`; scene `format` migrations already handle the data.
- **The Demo/Forest example downloads** still reference a checkout (`EnginePathRewriter`). Shipping them package-based
  is a follow-up.
- **`MainframeEngine.Sdk`** (`<Project Sdk="MainframeEngine.Sdk/X.Y.Z">`), which would hold the content rules,
  localization and RID defaults so that game projects become one-liners.
- **Pre-release packages** from `main` (`-preview.N`).

## Related docs

[Release & versioning](release.md) · [Projects & GameHost](project-and-gamehost.md) · [Editor](editor.md) ·
[Native libraries](natives.md) · [Localization](localization.md)
