# Plan: Surface shaders from the engine package

**Status:** done, see [log](../logs/2026-11-03-package-shader-modules.md)
**Docs:** [Shaders → Surface shaders](../../docs/design/shaders.md#surface-shaders) ([ADR 0237](../decisions/0237-slang-surface-shaders.md)),
[Distribution via NuGet](../../docs/design/distribution-nuget.md), [Game export](../../docs/design/game-export.md) (how
`mf-export` reaches games, the pattern this follows)
**Started:** 2026-11-03

## Goal

A game on the `MainframeEngine` package, with no engine checkout, can build and check its Slang surface shaders, and
VS Code's slangd resolves `import mainframe.spatial;` in them. Checkout games get the same paths.

## Not in scope

- Shipping slangc: games still need the Vulkan SDK or Slang on PATH to build shaders. Checking them needs neither.
- A ShaderMaterial inspector; editor-side shader builds.

## Steps

- [x] **Package the includes.** `MainframeEngine/Assets/Shaders/include/**/*.slang` goes into the package as
  `mainframe/shaders/include/`. Verified by: the packed `.nupkg` lists them.
- [x] **Package mf-shaders.** It goes into `tools/mf-shaders/` like `mf-export`, built first by `build/pack-nuget.sh`.
  mf-shaders takes `--include <dir>` and finds the package's include folder next to its own. Verified by: running the
  packed `mf-shaders.dll check` on the template's shader.
- [x] **Game properties and target.** `MainframeGame.props` gets `MainframeShadersTool` and `MainframeShaderInclude`
  (package or checkout). A `MainframeShaders` target runs `build` (or `check` with `-p:MainframeShadersCommand=check`) on
  the game's `Assets`. Every build of the game's executable copies the includes to `.mainframe/shaders/include/` next to
  `project.mfproj`. Verified by: template-smoke in both engine-source modes.
- [x] **Template.** A sample `Assets/Shaders/example.slang` with its committed SPIR-V, layout and lock, and
  `.vscode/settings.json` with `slang.additionalSearchPaths` set to `.mainframe/shaders/include`. The template folder
  joins `surface_shader_folders`, so `just shaders-check` keeps it current. Verified by: `just shaders-check`.
- [x] **Smoke.** `build/template-smoke.sh` checks that `.mainframe/shaders/include/mainframe/spatial.slang` exists after
  the build, and runs `-t:MainframeShaders -p:MainframeShadersCommand=check` (and `build` when slangc is found). With a
  package it also checks that `MainframeShadersTool` is the package's. Verified by: `just template-smoke` and
  `just template-smoke-package`.
- [x] **Docs and gates.** shaders.md (editor support, building from a game), distribution-nuget.md (contents, tools),
  the csproj's package-contents comment; then build, test, format, shaders-check and check.sh. Verified by: each passing.

## Done when

- `just template-smoke-package` passes: the game built from the package contains `example.slang`, its shader checks
  with the package's `mf-shaders`, and `.mainframe/shaders/include/mainframe/spatial.slang` exists.
- `just template-smoke` (checkout) passes with the same checks.
- `just shaders-check` covers the template's shader.

## Open questions

- None.
