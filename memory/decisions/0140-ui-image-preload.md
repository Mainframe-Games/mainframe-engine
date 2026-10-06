# ADR 0140 — UI images load with their document

- **Date:** 2026-10-07
- **Status:** accepted (Driving Range port: the 6.7 MB first phone open)
- **Amends:** the texture loading of the game UI (`VulkanUiRenderer.LoadTexture`, M8)

## Context

The first time Driving Range's phone opened, one frame allocated 6.7 MB and spent 14–17 ms in `UiServer.Process`. RmlUi
asks the render interface for an image the first time it lays the element out. The phone's seven `<img>` elements
were hidden until then, so all seven were read and decoded together on the main thread in that frame. That's about
6.6 MB of RGBA (wallpaper 640×960, phone frame 766×500, three 512×512 icons) plus the file bytes. Each decoded array is
over 85 KB, so all of it went straight to the large object heap as garbage. Later opens were free (RmlUi keeps the
textures). Godot loads a scene's textures with the scene, so the Godot build had no such hitch. The first F12 (dev
overlay) has the same shape at 5 ms.

## Decision

1. When a `UiDocument` loads or reloads, `UiServer.PreloadImages` walks its `<img>` elements, hidden ones included.
   Each `src` is joined with the document path by `RmlPaths.Join`, the same join RmlUi's `JoinPath` callback uses, and
   `RmlRenderInterface.PreloadTexture(source)` is called with the result.
2. `VulkanUiRenderer` decodes and uploads the image right then and keeps it by that source string. When RmlUi asks for
   the source in `LoadTexture`, the renderer hands over the ready texture. Keys are source strings, not file paths,
   because RmlUi caches textures by source: two spellings of one file are two textures to RmlUi, and two preloads here.
3. A source RmlUi already holds is not preloaded again. Unrequested preloads are disposed in `RmlCore.ReleaseTextures`
   (through `OnReleasingTextures`; hot reload re-reads files) and when the renderer is disposed.
4. There is no native change: the shim's `load_texture` callback is unchanged.

## Consequences

- The decode moves to document load, which is game start-up for a HUD. Driving Range's first phone open went from
  6.7 MB (the UI update alone took 14–17 ms) to 75 KB. Its frames take 9.7–12.4 ms in all, against 8.3 ms at
  120 Hz; the rest is the phone's first layout and the texture uploads.
- Images that are already visible at load are requested inside `LoadDocument`, before the preload runs, and decode as
  before (no gain is possible there).
- Not covered: sources set later by data bindings (`data-attr-src`) and RCSS image decorators. Driving Range's app icon
  background (`AppIconBG.png`, 64 KB) still decodes on first open.
- Every `<img>` a document holds is now decoded and kept in GPU memory from load, even one that is never shown.
- Diagnostics: `VulkanUiRenderer.PendingPreloads`, `ImagesFromPreload` and `ImagesDecodedOnDemand`.
- Test: render test `ImagesArePreloadedWithTheirDocument` (scene `ui-preload`). With preloading off it fails three ways:
  nothing pending, one image decoded on demand, and 381,904 B allocated by the show.
