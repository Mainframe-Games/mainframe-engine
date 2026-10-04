# Mainframe Engine logo ("C3 Circuit")

| File | Use |
|---|---|
| `logo.svg` | Master artwork, full detail — use at **64 px and above** |
| `logo-small.svg` | Simplified artwork — use at **48 px and below** (title bars, favicons, small icons) |
| `logo-macos.svg` | `logo.svg` on Apple's app-icon grid: 1024 canvas, 824 px tile centred (100 px transparent margin), corner radius ≈ 185 px — for `logo.icns` and the macOS Dock icon |
| `png/logo-macos-{16…1024}.png` | Rendered from `logo-macos.svg` (the `.icns` iconset) |
| `png/logo-{16,24,32,48}.png` | Rendered from `logo-small.svg` |
| `png/logo-{64,128,256,512,1024}.png` | Rendered from `logo.svg` |
| `logo.ico` | Windows icon (16–256, 7 sizes) — the editor's `ApplicationIcon` |
| `logo.icns` | macOS icon, from `logo-macos.svg` — the editor app bundle's `CFBundleIconFile` |

Engine content carries copies for apps (`ContentPaths`: `Content/Brand/logo-{32,48,256,512}.png`,
`logo-macos-512.png`): window icons (`EngineOptions.IconPath` — `logo-macos-512.png` on macOS, where it is the Dock icon;
`logo-48.png` elsewhere) and the editor splash screen (RmlUi renders PNG, not SVG). Windows `.ico` and Linux PNGs stay
full-bleed, as is their convention.

## Colours

| Part | Colour |
|---|---|
| Tile | `#172554` |
| Grid dots | `#1E40AF` |
| "M" and edge nodes | `#38BDF8` |
| Core | `#F59E0B` |

## Regenerating

`just brand` re-renders every PNG, the `.ico` and (on macOS) the `.icns` from the SVGs and refreshes the engine content
copies. It needs [Inkscape](https://inkscape.org); a single size by hand:

```sh
inkscape logo.svg -w 256 -h 256 -o png/logo-256.png          # >= 64 px
inkscape logo-small.svg -w 32 -h 32 -o png/logo-32.png       # <= 48 px
```

The `.ico` is packed by `build/brand/make-ico.py` (no dependencies); the `.icns` by `iconutil -c icns` from an iconset.
Do not redesign the artwork in place — the SVGs are the source of truth.
