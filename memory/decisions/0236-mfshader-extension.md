# ADR 0236 — Shader files are `.mfshader`, not `.gdshader`

- **Date:** 2026-11-03
- **Status:** superseded by [ADR 0237](0237-slang-surface-shaders.md) (game shaders are `.slang` Slang surface shaders;
  `.mfshader` and `.gdshader` no longer load)
- **Supersedes:** the file extension in [ADR 0113](0113-canvas-shaders-godot-language.md) and [ADR 0205](0205-spatial-shaders.md)
  (their shading language stays)
- **Docs:** docs/design/shaders.md#surface-shaders, docs/design/canvas.md#canvas-shaders

## Context

Custom shaders (canvas_item, ADR 0113; spatial, ADR 0205) kept Godot's `.gdshader` extension because the first ones
were ported from a Godot game (Crash Site Defense). The owner does not want the engine's files to look like Godot's:
every other engine format has an engine prefix (`.mscene`, `.mres`, `.msave`, `.mmusic`, `.mfproj`, `.mfcut`).

## Decision

- **Shader files are `.mfshader`** (`Shader.Extension`), with the owner's `mf` prefix, as in `.mfproj`. Their generated
  sidecars follow: `x.mfshader.{vert,frag}.spv` (and a spatial shader's other passes), `x.mfshader.spvlock`,
  `x.mfshader.meta`. The engine's, the Demo's, the Neon's and the render tests' shaders were renamed with `git mv`, the
  `.meta` files too, so UIDs and the references through them survive. Their `.spvlock` hashes were refreshed (they cover
  the generated Slang, whose `#line` directives name the file); the SPIR-V bytes do not depend on the name (a shader built
  under both names gives identical files), so the committed SPIR-V is unchanged.
- **The language does not change.** It is still Godot's `shader_type canvas_item`/`spatial` subset, translated to Slang
  (ADR 0144); docs keep calling it Godot's shading language.
- **Old `.gdshader` files still work, with a warning** (`Shader.LegacyExtension`): `ShaderImporter` loads both
  extensions and `Shader.Load` logs a deprecation warning for the old one; `AssetDatabase.IsBuildOutput` and
  `SpirvInvariance` know both sidecar names; `mf-shaders build|check` builds both and warns on each old file. A game
  project renames its files (with their sidecars) when it likes.
- The justfile's folder list is `mfshader_folders`.

## Ruled out

- `.mshader` (the one-letter prefix of `.mscene`/`.mres`): the owner chose `.mfshader`.
- A hard cut (only `.mfshader` loads): game projects with `.gdshader` files would break on the next engine version
  with "no importer" and nothing pointing at the rename. The fallback is a few lines.
- Keeping `.gdshader` for editor syntax highlighting: `files.associations` in an editor's settings maps `*.mfshader` to
  a Godot shader language mode (docs/design/shaders.md).

## Consequences

- Shader files no longer read as Godot assets, and editors no longer highlight them out of the box: a Godot shader
  extension needs one `files.associations` line.
- Two extensions to know about until the fallback goes.

## Revisit when

- No known game project has `.gdshader` files left: drop `Shader.LegacyExtension` and its branches.
