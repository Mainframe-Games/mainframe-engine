---
summary: >-
  Surface shaders from the engine package: the package ships mf-shaders and the shader include folder, games get a
  MainframeShaders target and a .mainframe/shaders/include copy for editors, and the template has an example shader.
---

# 2026-11-03: Surface shaders from the engine package

Branch `feature/g18-package-shader-modules`, stacked on #160 (G18). Plan:
[Surface shaders from the engine package](../plans/2026-11-03-package-shader-modules.md).

## What changed

- **The package.**
  - `tools/mf-shaders/` is packed like `mf-export`. `build/pack-nuget.sh` builds it first, and `MFPACK003` fires when
    it hasn't been built.
  - `mainframe/shaders/include/**` holds the engine's `Assets/Shaders/include/**/*.slang`, 42 files.
- **mf-shaders** takes `--include <dir>`. Without it, it uses the package's include folder next to itself, then a
  checkout's.
- **`MainframeGame.props`** (shared by package and checkout games):
  - `MainframeShadersTool` and `MainframeShaderInclude` point at the package's or the checkout's copies.
  - A `MainframeShaders` target runs `build`, or `check` with `-p:MainframeShadersCommand=check`, over the `Assets`
    folder next to `project.mfproj`.
  - `_MainframeCopyShaderInclude` runs after every executable build. It copies the include folder to
    `.mainframe/shaders/include/`, which the template's and the repository's `.gitignore` already ignore.
- **The template.**
  - `Assets/Shaders/example.slang` comes with its SPIR-V, layout and lock.
  - `.vscode/settings.json` sets `slang.additionalSearchPaths` to `.mainframe/shaders/include`, and
    `.vscode/extensions.json` recommends the shader-slang extension.
  - `.gitattributes` marks `*.spv` binary and `*.slang` LF. `template.json` copies `*.spv` without processing.
  - The template folder joined `surface_shader_folders`, so `just shaders-check` covers it.
- **`build/template-smoke.sh`**, in both engine-source modes, now:
  - requires the example's SPIR-V and layout in the output, the copied includes, and `.vscode/settings.json`;
  - runs `-t:MainframeShaders` with `check`;
  - when slangc is on PATH, builds a new `smoke.slang` and checks again;
  - for a package game, also requires `MainframeShadersTool` to be the package's.
- **Docs.** shaders.md ("Editor support", "Building from a game", the proposal's changes), distribution-nuget.md
  (package contents, build integration, "Surface shaders"), both package READMEs, and the csproj's package-contents
  comment.

## Decisions

- slangc is not shipped: building needs the Vulkan SDK or a Slang release, and checking needs nothing. The template's
  shader ships its SPIR-V, so a new game builds without slangc.
- The include copy runs only for the executable project, not the game library, so two parallel builds don't copy at
  once. Checkout games (the Demo, the Neon, the Forest) get the copy too, so the template's one setting works for both.
- It is a target in `MainframeGame.props`, not a dotnet tool, like `MainframeExportTool`.

## Dead ends

- **Checking slangd with a hand-rolled LSP client.** slangd 2026.8 (from the VS Code extension) published no
  diagnostics and answered hover with nothing, with or without the search path, and returned -32601 for pull
  diagnostics. It needs a real client's handshake; left as a human check.

## Verified

- **`just template-smoke`** (checkout) passes with slangc on PATH: the example checks, `smoke.slang` builds, and both
  check again.
- **`just template-smoke-package`** passes. The `.nupkg` lists `tools/mf-shaders/mf-shaders.dll` and the 42 include
  files. The package's mf-shaders, with the packaged includes, checked the example (its lock's `include:` lines matched
  the packaged files) and built `smoke.slang`.
- **The Demo** builds `.mainframe/shaders/include/` (ignored by git), and `-t:MainframeShaders` with `check` passes on
  it.
- **Gates:**
  - `just build` and the Release `-warnaserror` build, 0 warnings. `just format-check` passes.
  - `just shaders-check`: 20 surface shaders current.
  - `just test`: engine 3235, editor 1805 and video 460 pass.
  - `check.sh` passes.

## Not verified

- slangd resolving `import mainframe.spatial;` in VS Code. Open a new game in VS Code with the shader-slang extension
  after one build.
- Windows paths in the `MainframeShaders` Exec and the include copy. The smoke test runs on Linux here; CI's template
  job runs on Linux too.

## Next

- Nothing open for G18.
