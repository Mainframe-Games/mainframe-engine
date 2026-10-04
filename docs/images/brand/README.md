# Mainframe Engine logo ("C3 Circuit")

| File | Use |
|---|---|
| `logo.svg` | Master artwork, full detail — use at **64 px and above** |
| `logo-small.svg` | Simplified artwork — use at **48 px and below** (title bars, favicons, small icons) |
| `png/logo-{16,24,32,48}.png` | Rendered from `logo-small.svg` |
| `png/logo-{64,128,256,512,1024}.png` | Rendered from `logo.svg` |
| `logo.ico` | Windows icon (16–256, 7 sizes) — the editor's `ApplicationIcon` |
| `logo.icns` | macOS icon — the editor app bundle's `CFBundleIconFile` |

Engine content carries copies for apps (`ContentPaths`: `Content/Brand/logo-{32,48,256,512}.png`): window icons
(`EngineOptions.IconPath`) and the editor splash screen (RmlUi renders PNG, not SVG).

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
