# ADR 0112 — SVG through Godot's ThorVG (mfsvg)

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** port of Crash Site Defense (Godot 4.7) — PM1 / E8 (the game's `docs/porting.md`)

## Context

The game's art (props, buildings, icons, the emblem) is SVG, imported by Godot at scale 1 through ThorVG with the 2D
texture preset's `fix_alpha_border`. The engine imported PNG/JPEG/TGA/BMP only. Any other rasteriser antialiases edges
differently. A new native dependency was approved through the port plan (row E8).

## Decisions

1. **`mfsvg`**: a small C ABI (`mfsvg_rasterize`, `mfsvg_size`, `mfsvg_free`, ABI 1.0) over **the exact ThorVG sources
   Godot 4.7.2 vendors** (`thirdparty/thorvg`, 1.0.3 + Godot's patch, copied to `Native/Svg/thorvg`), compiled with
   Godot's source list and defines (`TVG_STATIC`, `THORVG_FILE_IO_SUPPORT`) minus the PNG loader (it uses Godot's
   libpng; no game SVG embeds images). The shim is Godot's `ImageLoaderSVG::create_image_from_utf8_buffer`: software
   canvas, `ABGR8888S` (straight-alpha RGBA bytes), picture resized to `round(size × scale)`, 1 thread.
2. **Import at load:** `.svg` is a texture extension; `Texture2D` rasterises it with `svgScale` (meta) and applies
   `ImageOps.FixAlphaEdges` (Godot's `Image::fix_alpha_edges`) when `fixAlphaBorder` is set (any image type).
3. **Built like the other natives** (CMake, export lists, static runtime, universal macOS, `natives.lock`). Linux and
   Windows binaries were first built locally in Docker (Ubuntu 22.04 GCC; mingw-w64 for Windows, static runtime) until
   `natives.yml` rebuilds them with MSVC.

## Consequences

- Verified: every game SVG rasterised by mfsvg + `FixAlphaEdges` is byte-identical to Godot's imported texture (lossless
  WebP in `.godot/imported/*.ctex`), except the emblem (13 bytes off by 1).
- One more native library per platform (~300 KB).
