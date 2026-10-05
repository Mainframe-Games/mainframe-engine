# Developer overlay & screen gizmos

## Purpose

The in-engine debug tools. The **dev overlay** (F12) is an RmlUi layer of collapsible panels that shows frame, renderer,
shadow, GPU, audio, physics and network numbers and lets a game add its own panels. **Screen gizmos** are Vulkan-drawn
screen-space shapes (light icons, the corner axes) that the overlay toggles. RmlUi is the engine's only UI stack: the
former ImGui overlay is gone ([ADR 0115](../../memory/decisions/0115-remove-imgui.md)).

## The overlay

Files: [Debugging/DevOverlay/](../../MainframeEngine/Src/Debugging/DevOverlay/) ·
[Content/UI/dev/overlay.rcss](../../MainframeEngine/Content/UI/dev/overlay.rcss).

| Aspect | Detail |
|---|---|
| Toggle | F12 (`Engine.DevOverlayKey`). `EngineOptions.DevOverlayVisible` sets the start state (default hidden); `Engine.DevOverlayVisible` reads and sets it at runtime. The editor keeps it off. |
| Access | `Engine.DevOverlay`, or `Tree.Servers.Get<DevOverlay>()` from a node (null when `EngineOptions.EnableUi` is off). Works for `GameHost` games and `Engine` subclasses alike: no hook to override. |
| Layer | One `UiLayer` named `DevOverlay` at `DevOverlay.LayerOrder` (`int.MaxValue`, above every game layer) holding one `UiDocument` composed from the panels. The layer is created the first time the overlay is shown; hidden, it does no work. |
| Layout | A 360 dp column at the top right, at most 45 % of the viewport wide, scrolling when taller than the window. Each panel is a collapsible section: its title toggles the body. Only the column takes the mouse; the rest of the overlay passes input through to the game. |
| Server | `DevOverlay` is an `IServer` and `IFrameServer`, registered by `Engine.OnLoad` next to `UiServer`. |

### Built-in panels

| Id | Title | Contents |
|---|---|---|
| `frame` | Frame | FPS, frame ms (average / min / max over the refresh window), frame counter, VSync toggle, UI draw calls |
| `renderer` | Renderer | Exposure slider and reset, colour pipeline description, mesh counters (draw calls, surfaces, instances, culled, pipeline and material binds, shadow and ID draws), resident meshes / materials / textures, pipeline cache |
| `shadows` | Shadows | Cascade colours, stable cascades, filter and radius, pass and draw counters, map sizes and memory, CPU / GPU ms, and the shadow-map images (see below) |
| `gpu` | GPU memory | Allocator totals and per-memory-type usage, staging ring, pending uploads, deletion queue, pipeline cache and shader modules |
| `audio` | Audio | Device name, voices / steals / underruns, and one row per bus: dB fader, mute, solo, peak meter (writes go through `AudioServer` commands) |
| `physics` | Physics | Toggles for collision shapes, light gizmos and the axis gizmo; 3D and 2D body counts (awake) |
| `network` | Network | Mode, tick, networked nodes, clients, bytes per second in and out; "Offline" while no `MultiplayerApi` session is active |

A panel whose server is not registered (no `RenderServer`, no `AudioServer`, ...) shows "—" instead of its rows.

### Adding a panel

`DevOverlay.AddPanel(id, title, rml, bind)` appends a section and returns its `DevOverlayPanel`. `rml` is an RML
fragment (the section body); `bind` runs once the overlay document can create data models, with `panel.Model` set (the
model is named `dev_{id}`). Duplicate ids throw. `RemovePanel(id)` removes one. Add panels from a node's `OnReady`:

```csharp
public sealed class EnemyCounter : Node
{
    private int _enemies;

    protected override void OnReady()
    {
        if (Tree?.Servers.Get<DevOverlay>() is not { } overlay)
            return;
        var panel = overlay.AddPanel("enemies", "Enemies", """
            <div class="dev-row"><span>Alive</span><span class="v">{{alive}}</span></div>
            """, p => p.Model!.Bind("alive", this, static n => n._enemies));
        overlay.Refreshed += () => panel.Dirty("alive"); // 4 Hz, only while visible
    }
}
```

Use the overlay's classes (`dev-row`, `dev-heading`, `dev-button`, `dev-slider`, `v` for values; see `overlay.rcss`) and
the shared widget library for controls. `DevOverlayPanel.Expanded` is the open state; `Dirty(name)` marks a bound value
changed and does nothing until the panel is bound.

### Refresh model

| Event | When | Use |
|---|---|---|
| `DevOverlay.Refreshed` | Every `refreshInterval` seconds (default 0.25, 4 Hz) while visible, and on the first frame after showing | Copy numbers into bound fields, then `Dirty` them |
| `DevOverlay.Frame` | Every frame while visible, with the delta time | Accumulate per-frame samples (the Frame panel's min / max) |

Neither fires while the overlay is hidden. Per-frame code must not allocate (the allocation gate runs with the overlay
visible):

- Bind numbers as `int` / `float` and format them in RML (`{{ms | format(2)}}`) instead of building strings.
- Cache text and rebuild it only when its source changes; keep list rows in reused objects (`BindList`).
- Look servers up through the registry in `static` lambdas; no closures per refresh.
- Built-in panels keep one snapshot object each (`DevOverlayStats.cs`) that `Refresh()` fills; `Dirty` only what changed
  (the views touch the DOM only where the text differs).

### Shadow-map images

The Shadows panel shows each cascade layer and the atlas as `<img src="engine://dev-shadow-cascade-N"/>` and
`engine://dev-shadow-atlas`. They are registered through `UiServer.RegisterTexture(name, Func<UiTextureView> source,
flags)`, which asks the source every frame the image is drawn, so the view survives shadow-map re-creation:

- `UiTextureView(View, Sampler, Layout, Width, Height, Generation)`; a zero `View` means "nothing to show" and nothing is
  drawn. `Generation` identifies the image's incarnation: change it whenever the image is recreated, so the UI writes a
  new descriptor set instead of binding a destroyed view.
- The depth maps are sampled in `DepthStencilReadOnlyOptimal` with `UiTextureConversion.DepthToGray`: `.r` becomes grey
  with alpha 1. The image must be in the layout the source reports when the UI renders.
- An `engine://` source image with no view at document load reports 0×0, so size such `<img>` elements explicitly in
  RCSS (the overlay does: `img.dev-map` 78 dp square, `img.dev-atlas` 256 dp).

Other engine textures (a `GpuTexture`, a `RenderTarget` colour attachment, e.g. a `SubViewport`) are published with the
public `RegisterTexture` overloads; see [Game UI](game-ui.md).

## Screen gizmos

Files: [Rendering/Gizmos/](../../MainframeEngine/Src/Rendering/Gizmos/) · shaders `Content/Shaders/Gizmos/ScreenGizmo.vk.{vert,frag}`.

`RenderServer.ScreenGizmos` is a `ScreenGizmoBatch`: immediate-mode shapes tessellated into triangles on the CPU and drawn
by `ScreenGizmosRenderer` in the overlay pass, then cleared. It grows on demand and then never allocates.

| Aspect | Detail |
|---|---|
| Coordinates | Framebuffer **pixels**, origin top left, +Y down (not points: multiply layout sizes by `RenderServer.GizmoScale`, which `Engine` sets each frame from the UI content scale) |
| Colour | `Vector4`, straight-alpha **sRGB**; blended in sRGB space like the UI. The fragment shader linearises only when the overlay target encodes sRGB on store (`IVulkanContext.OverlayEncodesSrgb`) |
| Shapes | `Line`, `Polyline`, `Circle`, `FilledCircle`, `FilledRect`, `Triangle`, `Arrow`, and `Glyph` (stroke X / Y / Z letters, no font). Lines get a 1 px feathered edge for anti-aliasing |
| Lifetime | Fill it any time before the frame's overlay pass. One draw per frame into a per-frame-slot dynamic vertex buffer. `Engine` clears it on frames that are not drawn (minimised, swapchain rebuild) so shapes never pile up |
| Order | `OverlayOrder`: `Canvas` 0 (2D canvas), `Gizmos` 100, `Ui` 200. Gizmos sit above the canvas and below every UI layer, the dev overlay included |

Helpers (all take the batch, the camera, the viewport size in pixels and a scale):

| Helper | Draws | Toggle |
|---|---|---|
| `LightGizmos.Draw(batch, camera, viewport, lights, scale)` | Point lights as a dot with three range rings, spot lights with apex, cone rim and inner / outer circles, directional lights as a sun with a direction arrow | `RenderServer.ShowLightGizmos` |
| `AxisGizmo.Draw(batch, camera, viewport, scale)` | The top-right XYZ axes (X red, Y green, Z blue), depth-sorted back to front with stroke labels | `RenderServer.ShowAxisGizmo` |
| `GizmoProjection.TryProject(world, viewProjection, viewport, out pixel)` | World to framebuffer pixel; false at or behind the camera plane | none |

`RenderServer.RenderMain` queues the light and axis gizmos for the tree's root viewport when their flags are set; the
Physics panel's checkboxes drive the flags. Game code can draw its own shapes through `ScreenGizmos` the same way, using
`GizmoProjection` to place them.

## Logging

`Log` and its sinks (console, rotating file, memory ring) are described in
[Project & GameHost](project-and-gamehost.md#log-routing).

## Related docs

[Game UI](game-ui.md) · [Engine lifecycle](engine-lifecycle.md) · [Color pipeline](color-pipeline.md) ·
[Shadow system](shadow-system.md) · [Lighting](lighting.md) · [Testing](testing.md)
