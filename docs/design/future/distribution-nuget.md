# Future: distribution via NuGet — how games consume the engine

**Status:** proposal; the template part has shipped (M10). Today games reference `MainframeEngine.csproj` from a
checkout of this repo — `dotnet new mfgame --engine-path <checkout>` sets that up
([Projects & GameHost → Template](../project-and-gamehost.md#template)); the [release workflow](../release.md)
ships only the editor.

## Problem

A game should be able to depend on a **versioned** engine without cloning the repo, and the editor's
"new project" wizard must produce a project that builds against the same engine version the editor runs.
The engine is more than a DLL: it has native libraries per platform (`runtimes/<rid>/native`: RmlUi shim,
ENet, plus Silk/SoundFlow natives from dependencies), engine content (`Content/Shaders`, UI widgets, fonts),
a Roslyn source generator that game code must run (`[Export]`, `[Signal]`, `[Replicated]`, `[Rpc]`), and
build steps (shader compilation, `.po → .mo`).

## Goals

- `dotnet new mfgame -n MyGame` → a project that builds and runs with **no engine checkout**.
- One version number for the editor and the engine packages; a game pins it.
- Natives resolve for `dotnet run`, `dotnet test` and `dotnet publish -r <rid>` on all supported RIDs.
- Private first (org feed), public later without changing package ids.

## Packages

| Package | Contents |
|---|---|
| `MainframeEngine` | `lib/net10.0/MainframeEngine.dll` (+ xml docs, snupkg symbols) · `runtimes/<rid>/native/*` (RmlUi shim, ENet) · `analyzers/dotnet/cs/MainframeEngine.Generators.dll` (generator bundled, so games get it automatically) · `contentFiles`/`buildTransitive/MainframeEngine.targets` that copies engine `Content/**` to the game's output and publish folder |
| `MainframeEngine.Templates` | `dotnet new` templates: `mfgame` (class library `MyGame` + `MyGame.Launcher` exe running `GameHost` + `project.mfproj` + `Content/`), `mfnode` (node script) |
| `MainframeEngine.Sdk` *(later)* | MSBuild SDK (`<Project Sdk="MainframeEngine.Sdk/X.Y.Z">`) owning shader compilation, localization compile, content rules and RID defaults, so game projects stay one-liners |

Dependencies (Silk.NET, ImGui.NET, SoundFlow, Jitter2, Box2D.NET, GetText.NET, NVorbis, Steamworks.NET,
StbImageSharp, spine-csharp) flow as normal package dependencies. `spine-csharp` is a project reference
today: it ships inside the `MainframeEngine` package as a second assembly
(`PrivateAssets` + `IncludeAssets` trick) until it is published separately.

### Natives

`MainframeEngine.csproj` already packs `runtimes/<rid>/native` at the standard path (M5). For **RID-less**
`dotnet run/test`, the package's `buildTransitive` targets copy the host OS's natives flat next to the app
— the same thing the repo build does for project references today — so behaviour is identical whether a
game references the project or the package. `SilkNativeResolver` keeps Silk-loaded natives working on
distro-specific RIDs.

### Content

Engine content is resolved through `ContentPaths` relative to `AppContext.BaseDirectory`. Packed as
`content/Mainframe/**` and copied by `buildTransitive` targets into `<output>/Content/Mainframe/**`, which
`ContentPaths` searches after the game's own `Content/`.

## Versioning

- The editor release tag `vX.Y.Z` (see [Release & versioning](../release.md)) is also the package version.
  `publish.yml` gains a `packages` job: `dotnet pack -c Release -p:Version=X.Y.Z` for the three packages,
  then push.
- The editor's new-project wizard writes `<PackageReference Include="MainframeEngine" Version="X.Y.Z" />`
  with **its own** version; opening a project built against another version shows an "upgrade project"
  prompt (bump the reference; scene `format` migrations already handle data).
- Pre-release builds from PRs are out of scope; a `-preview.N` suffix can be added later from `main`.

## Feeds

1. **GitHub Packages** (`https://nuget.pkg.github.com/Mainframe-Games/index.json`), pushed with the
   workflow's `GITHUB_TOKEN` (`packages: write`); consumers authenticate with a PAT (`read:packages`) in
   `nuget.config`. Private, zero extra infrastructure.
2. **nuget.org** when the engine goes public: same ids, add an API-key secret and a second push step.

## Engine changes needed

- `IsPackable`, `PackageId`, description, licence expression, icon, README in `MainframeEngine.csproj`;
  `IsPackable=false` everywhere else.
- `buildTransitive/MainframeEngine.targets` (flat native copy + content copy) — extract the logic that
  lives in `MainframeEngine.csproj`/`Directory.Build.targets` today so repo and package builds share it.
- Ship `MainframeEngine.Generators` inside the package's `analyzers` folder.
- `ContentPaths` search order: game `Content/` → `Content/Mainframe/` (engine).
- ~~Template project under `Templates/` with a template test in CI~~ — done (M10): `Templates/MainframeEngine.Templates`,
  CI job `template`. Its `--engine-source package` option already writes
  `<PackageReference Include="MainframeEngine" Version="$(MainframeEngineVersion)" />`; once the package exists, CI
  should smoke-test that branch too.

## Tasks

- [ ] Packaging metadata + `buildTransitive` targets; `dotnet pack` produces a package that a scratch
      game (outside the repo) can build, run and publish for each RID — CI job.
- [x] `MainframeEngine.Templates` (`mfgame`: `MyGame` + `MyGame.Launcher` running `GameHost` + `project.mfproj` +
      `Content/`) + CI template test (project-reference mode; `just template-smoke`, `just template-pack`).
- [ ] Template smoke test in package mode once the engine package exists; `mfnode` item template.
- [ ] `packages` job in `publish.yml` → GitHub Packages.
- [ ] Editor new-project wizard uses `dotnet new mfgame` with the editor's version; upgrade prompt.
- [ ] Later: `MainframeEngine.Sdk`; nuget.org.

## Open questions

- Ship `spine-csharp` inside the engine package, or publish the fork as its own package?
- Should engine content be embedded resources instead of copied files (simpler packaging, but the editor
  and hot-reload want files)?
