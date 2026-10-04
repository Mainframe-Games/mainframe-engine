# ADR 0084 — Brand logo assets and window icons (byte order, macOS icon grid)

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 (lane m10a)
- **Spec:** docs/images/brand/README.md

## Decisions

1. **The "C3 Circuit" logo is the source of truth as SVG** (`logo.svg`, `logo-small.svg` for ≤ 48 px,
   `logo-macos.svg` on Apple's icon grid: 824 px tile on a 1024 canvas). PNG/ICO/ICNS are generated (`just brand`:
   Inkscape, a dependency-free ICO packer, `iconutil`) and committed; engine content carries the PNGs apps load.
2. **Window icons per platform**: macOS uses the padded grid variant at Dock resolution (`logo-macos-512.png`) — SDL's
   window icon is the Dock icon there; Windows/Linux use the full-bleed small artwork (`logo-48.png`). The editor's
   `.ico` (Windows `ApplicationIcon`) is full-bleed; the `.app` bundle gets `logo.icns` (`CFBundleIconFile`).
3. **Window icon pixels are reordered for Silk's SDL surface.** `SdlWindow.SetWindowIcon` creates the surface with masks
   R 0xFF000000, G 0x00FF0000, B 0x0000FF00, A 0x000000FF over native-endian 32-bit pixels, i.e. it reads A, B, G, R
   bytes on little-endian machines. Passing decoded RGBA swapped every channel (alpha from red: opaque/cyan corners,
   amber turned pink). `WindowIcon.ToSdlByteOrder` reorders the bytes; a unit test reads pixels back through an SDL
   surface built with Silk's exact masks.
4. **The editor splash** uses `logo-512.png` (RmlUi renders PNG, not SVG) on the tile navy `#172554`.
