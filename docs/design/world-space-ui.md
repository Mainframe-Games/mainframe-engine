# World-space UI and 3D text

**Milestone:** [Gameplay toolkit](../milestones.md#gameplay-toolkit-) (G11) · **Status:** 🚧 G11.1–G11.6 and G11.7's
panel preview shipped (this page); the batched text renderer, panel stand-in data in the scene view and G11.9 remain in
the [proposal](future/world-space-ui.md) · **Decisions:**
[ADR 0200](../../memory/decisions/0200-label3d-first-slice.md) (first `Label3D`),
[ADR 0216](../../memory/decisions/0216-viewport-textures-on-3d-materials.md) (sub-viewport textures on meshes),
[ADR 0251](../../memory/decisions/0251-label3d-on-the-mesh-batcher.md) (labels, layout, fallbacks),
[ADR 0254](../../memory/decisions/0254-markers-as-rows-of-one-document.md) (markers),
[ADR 0252](../../memory/decisions/0252-ui-panels-render-before-the-scene.md) (panels render before the scene),
[ADR 0253](../../memory/decisions/0253-ui-panel-input.md) (panel input)

Text and UI in the 3D world: `Label3D` for signs and tags drawn in the scene, `UiMarker3D` for nameplates, health bars,
prompts and waypoints drawn on the HUD at a 3D point, `UiPanel3D` for terminals and in-world menus (a whole RmlUi context
on a quad, clicked with the mouse, a crosshair or code), and the camera helpers they use.

| | `Label3D` | `UiMarker3D` | `UiPanel3D` |
|---|---|---|---|
| Drawn | in the scene pass (HDR, tonemapped, fog) | after the tonemap, on a screen `UiLayer` (exact colours) | in the scene, from the panel's own texture |
| Hidden by walls | depth test (`NoDepthTest` turns it off) | physics occlusion (optional) | depth test |
| Size | world units (`PixelSize`), or fixed pixels (`FixedSize`) | dp, optional distance scale | world units (`Resolution` × `PixelSize`) |
| Content | plain text, multi-line | any RML row template (one host document) | any RML (its own context) |
| Input | none | none | mouse ray, crosshair, code |

![The Demo's World UI tab](../images/demo/world_ui.png)

## Camera helpers

`Camera3D` (all with the synced render camera, no TAA jitter):

- `UnprojectPosition(worldPoint)` → framebuffer pixel, top-left origin (Godot's `unproject_position`); a static overload
  takes an `ICamera` and a viewport size, and `Camera3D.Project(viewProjection, point, size)` skips the matrix product for
  code projecting many points.
- `IsPositionBehind(point)` (behind the near plane), `IsPositionInFrustum(point)`.
- `GetFrustum()` (the engine's `Frustum`: `Intersects(Aabb)`, `Contains(point)`, `CopyPlanes`) and
  `GetFrustumPlanes(Span<Plane>)`: six normalised planes, left, right, bottom, top, near, far. **Normals point inwards**
  (Godot's point outwards): a point is inside when every `Plane.DotCoordinate` is ≥ 0.

## Text layout and fallback fonts

- `Font.Fallbacks` (Godot's `fallbacks`): fonts searched in order for a character the face lacks. Their glyphs are
  rasterised into the primary font's atlas pages; kerning applies only between glyphs of one face; lines keep the
  primary font's ascent and descent. `Font.Layout`/`MeasureLine` also take an extra list searched after `Fallbacks`.
- `TextLayout.BreakLines` (internal) breaks text at `\n`/`\r\n` and wraps inside a width with Godot's `AutowrapMode`
  (`Arbitrary`, `Word`, `WordSmart`; greedy, spaces at a break dropped). `Label2D` and `Label3D` share it; `Label2D`'s
  output is unchanged.
- `LocaleFallbackFonts` (internal) loads the faces the project's `FontFallbackTable` (`Assets/locale/fonts.mres`) names for
  `Tr.LocaleChain`, the same table the game UI loads into RmlUi ([Localization](localization.md)). `Label3D` uses them when
  its font lacks a glyph, so CJK text shows once the project lists a CJK face.

## Label3D

Godot's `Label3D`: one `ArrayMesh` of glyph quads (outline surfaces per atlas page, then text surfaces) drawn by the
mesh batcher, rebuilt only when the text, a property or the locale changes. Options: `Text` (translated with `Atr`),
`Font` (null = the engine's Lato Latin), `FontSize`, `PixelSize`, `Modulate`, `OutlineSize`, `OutlineModulate`,
`OutlineRenderPriority`, `TextRenderPriority`, `HorizontalAlignment`, `VerticalAlignment`, `Offset`, `Width` (500 px),
`Autowrap`, `LineSpacing`, `Uppercase`, `Billboard`, `FixedSize`, `NoDepthTest`, `Shaded`, `DoubleSided`, `AlphaCut`
(`Disabled` blends, `Discard` is a depth-writing cutout that casts shadows). `GetLines()` returns the laid-out lines and
`GetAabb()` the local bounds.

- **Lines.** The block is as wide as its widest line (or `Width` when wrapping) and is aligned around the node; each line
  aligns inside the block.
- **`FixedSize`.** The mesh renderer scales the model matrix per view (`Billboard.FixedSize`, after the billboard) so one
  label pixel covers one framebuffer pixel at any distance. Like the billboard, it is CPU-side: the colour pass, prepass,
  picking and casters all see the same quad.
- **Picking and the editor.** Labels pick like any mesh (object IDs) and draw in the scene view.

## Markers

A `UiMarker3D` is a 3D point (the node plus `WorldOffset`); a `UiMarkerHost` (a `UiDocument` on a screen `UiLayer`,
`Source` = `Assets/UI/markers/markers.rml` by default) draws every marker of its camera's viewport as one row of its
`markers` list.

```text
World (Node3D)
├── Npc (CharacterBody3D)
│   └── Nameplate (UiMarker3D)   Kind = "nameplate", Text = "Nell", WorldOffset = (0, 2, 0)
└── Hud (UiLayer)
    └── Markers (UiMarkerHost)    one document for every marker
```

- **Marker options:** `Kind` (the row template: `nameplate`, `health`, `prompt`, `waypoint` in the default document),
  `Text`/`Subtext` (translated), `Value` (0–1 bars), `Icon`, `WorldOffset`, `ScreenOffset` (dp), `MaxDistance` (0 =
  never hidden) and `FadeDistance`, `ScaleWithDistance` with `ReferenceDistance` and `MinScale`, `Offscreen` (`Hide`,
  `ClampToEdge` with `EdgeMargin` dp), `Occlusion` (`None`, `Physics` with `OcclusionMask`), `Host` (empty = the first
  host showing the marker's viewport). Read-back: `ScreenPosition` (layer pixels), `IsOnScreen`, `IsOccluded`,
  `Distance`, `EdgeAngle` (degrees, 0 = right, 90 = down), `IsShown`; signals `ScreenEntered`, `ScreenExited`.
- **Element mode:** with `TargetDocument` and `TargetElementId` a marker takes no row and moves that element instead
  (`transform`, `opacity`), for a single rich widget such as a boss bar.
- **Host options:** `Camera` (empty = the viewport's active camera), `SortByDistance` (nearer rows on top),
  `OcclusionRaysPerFrame` (16, round robin), `OcclusionFadeSeconds` (0.15), `ContentRefreshSeconds` (0.1).
- **The `markers` model.** Each row has `x`, `y` (layer pixels), `scale`, `opacity`, `visible`, `edge`, `angle`,
  `distance`, `kind`, `text`, `subtext`, `value` and `icon`. A game copies the default document or points `Source` at
  its own; each row must stay a direct child of one parent with RmlUi's `data-for` template after them (see below).
- **Per frame** (`UiDocument.PrepareFrame`, just before the layer's context updates): project each marker with the
  camera's view-projection, map the viewport onto the layer, apply distance hide, fade and scale, clamp points that are
  off-screen or behind the camera to the margin rectangle along the line from the centre, and ease the occlusion fade.
  Rows are rebuilt, sorted farthest first and the model dirtied only when content changes (markers added or removed,
  kind, text, value, visibility, edge state, an edge arrow turning more than 2°) or every `ContentRefreshSeconds`; other
  frames set `transform` and `opacity` on the generated row elements from C#, which skips RmlUi re-evaluating every
  row's data views. Steady frames allocate 0 B.
- **Occlusion** (`Physics`): up to `OcclusionRaysPerFrame` rays from the camera to markers, round robin, through
  `DirectSpaceState.RayCast`, excluding the marker's nearest `CollisionObject3D` ancestor (a nameplate is not hidden by
  its own body). A hit fades the marker out over `OcclusionFadeSeconds`.
- **Split screen:** give each player's HUD layer a `Region` and its own host whose `Camera` is that player's; the
  camera's viewport maps onto the layer.

## Panels

A `UiPanel3D` (a `GeometryInstance3D`) holds `UiDocument`s like a `UiLayer` does: its own RmlUi context at `Resolution`
pixels, rendered into its own texture before the scene pass, so the scene shows the frame's UI in the same frame (ADR
0252). It is shown on a quad of `Resolution` × `PixelSize` metres facing +Z, or on `Mesh` (UV 0–1 maps the panel). It
casts shadows, picks and sorts like any mesh.

```text
Terminal (StaticBody3D)          its collider does not block its own panel's line of sight
└── Screen (UiPanel3D)           640×400 px × 0.0022 m
    └── Ui (UiDocument)          Source = "Assets/WorldUi/terminal.rml"
```

- **Options:** `Resolution` (1024×640), `DpScale` (panel pixels per dp; the player's UI scale does not apply),
  `PixelSize` (0.001 m), `Mesh`, `Background` (sRGB; what the documents are drawn over), `Transparent` (the documents'
  own alpha, alpha-blended), `Shaded` (lit like a sign; default unshaded like a screen), `Brightness` (an unshaded panel's
  emission multiplier: above 1 the glow catches it), `UpdateMode` (`Always`, `Once`, `Disabled`), `MaxUpdateDistance`
  (30 m), `InputMode`, `MaxInteractDistance` (4 m), `RequireLineOfSight`, `ClickAction` (`ui_accept`). Read-back:
  `Context`, `Documents`, `HasFocus`, `HasPointer`, `PointerPosition` (panel pixels), `RenderCount`, `Texture`, `QuadSize`.
  Signals `PointerEntered`, `PointerExited`, `FocusChanged(bool)`.
- **Documents** attach to the nearest `UiLayer` or `UiPanel3D` above them (`IUiContextHost`). `UiDocument.Panel` is the
  panel, and `Layer` is null under one. Data model names are unique per panel, as they are per layer.
- **When it renders:** an `Always` panel renders every frame while it is within `MaxUpdateDistance` of its viewport's
  camera and its bounds are in that camera's frustum. A `Once` panel renders on the next frame, then switches to
  `Disabled`. A panel the pointer is on, or that has focus, renders every frame. At most
  `UiServer.MaxPanelRendersPerFrame` (8) render per frame: owed renders (after a document loads, after a skipped frame)
  first, then the nearest. The others keep their last image. `UiServer.PanelsRenderedLastFrame` counts them.
- **Rendering** (ADR 0252): `UiServer` records each due panel as a segment of the UI command list
  (`VulkanUiRenderer.BeginTarget`), then the screen layers. `IPreSceneRenderer` (`IVulkanContext.AddPreSceneRenderer`,
  run by `RenderServer.RenderOffscreen` before the sub-viewports) replays each segment into the top-left of the shared
  UI layer stack. The stack grows to the largest panel. A resolve pass then composites it over `Background` (or
  un-premultiplies it) into the panel's `R8G8B8A8` image, and blits build its mips. Filters, clip masks, gradients and
  box shadows work as on the HUD. A 1024×640 panel costs about 3.5 MB of texture with mips.
- **Material:** a private `StandardMaterial3D` over `Texture2D.FromGpuSource` (internal: the panel image, borrowed the way
  sub-viewport textures are, sRGB view, clamped). Unshaded panels draw the texture as emission × `Brightness`. Panels
  are scene geometry, so exposure, the tonemap, TAA and glow apply.
- **Edit mode:** a panel of a scene being edited renders once after each document load and takes no input.

### Panel input

ADR 0253. Input goes to `InputBeforeUi` nodes first, then the screen layers (a HUD menu over a terminal wins), then
panels, then game nodes.

- **`Mouse`** (default): the cursor's ray from the viewport's camera, not while the mouse is raw or captured, nor while
  a screen layer has it. **`Crosshair`**: the screen-centre ray, every frame, also while the mouse is raw; `ClickAction`
  (looked up in the tree's input map) presses and releases the left button. **`Manual`**: `PushPointer(origin,
  direction)`, `PushButton(button, pressed)`, `PushWheel(delta)` (VR hands, NPCs, tests). `None`: a display.
- The nearest front-facing hit within `MaxInteractDistance` wins (`TryRayToPixel`). With `RequireLineOfSight`, a physics
  ray must reach it first; it excludes the panel's nearest `CollisionObject3D` ancestor, so a terminal's own body does
  not block it.
- A press on a panel is consumed (the game does not get it) and focuses the panel. The panel keeps the pointer until
  release, and moves follow its plane past the edge. A press elsewhere releases the focus, and so does Escape when no
  text field has it. Keys, text and gamepad navigation go to the focused panel after the screen layers. A text field
  there takes every key. `UiServer.HoveredPanel` and `FocusedPanel` read the state.
- Rays hit the quad only: a panel with a custom `Mesh` renders on it, but takes no pointer input yet (G11.9).

## Editor

- `Label3D` (`letter-t`) draws and picks in the scene view.
- `UiMarker3D` and `UiMarkerHost` use the `map-pin` icon. A marker shows a map-pin scene badge at its point
  (`WorldOffset` above the node) with a dashed line to the node; the badge picks and box-selects. Markers are not drawn
  in the editor (screen layers of an edited scene are inert).
- `UiPanel3D` (`device-desktop`) draws in the scene view (it renders once per document load). Its inspector has an icon
  button, "Open document in UI preview", which opens its first document's preview tab at the panel's `Resolution`
  (`UiPreview.FixedSize`, centred in the view area).

![A nameplate marker selected in the editor](../images/editor-world-ui.png)

## Demo

The `world_ui` tab (UI category): a street with `Label3D` signs (a three-line menu, a billboarded market sign, an
autowrapped notice), six walkers with nameplate markers whose bars drain and refill, a `FixedSize` + `NoDepthTest`
debug tag per walker, a "press E" prompt on a crate and a waypoint that clamps to the screen edge. A terminal
(`WorldUiTerminal` on a `UiPanel3D`) near the camera opens the gate across the far street. Tab switches it between the
mouse and a crosshair: the camera then aims at it, and the `use` action (E, gamepad A) clicks. The side panel toggles
occlusion, distance scale, the debug tags and a camera orbit. Translated into Spanish and `qps`.

## Testing

- Unit: `Camera3DRayTests` (frustum helpers), `TextLayoutTests` (newlines, wrap modes equal to `Label2D`'s, fallback glyphs
  sharing the atlas, Latin text unchanged by fallbacks), `Label3DTests` (lines, alignment, autowrap, uppercase,
  `FixedSize` scale, alpha cut), `UiMarkerTests` (projection, fade, scale, edge clamp and angle behind the camera, physics
  occlusion with the own body excluded, sort, signals, element mode, the fast path, 0 B over 100 markers),
  `UiPanel3DTests` (documents and model names per panel, `TryRayToPixel` on rotated and scaled quads, mouse clicks and
  focus, layers first, text and Escape, the crosshair's click action, manual pointers with capture past the edge, line
  of sight, `UpdateMode`/distance/frustum gating, at most 8 renders per frame, 0 B with a panel updating); editor
  `UiPreviewTests` (the panel preview button); Demo `WorldUiSceneTests` (the terminal opens the gate through
  `PushPointer`, Tab switches to the crosshair).
- Render: `label3d-layout` (multi-line, autowrap, a Cyrillic fallback through Roboto Mono, alpha cut, two `FixedSize`
  tags equal on screen), `ui-markers` (`#3366cc` bar fill exact after the tonemap, an edge waypoint) and `ui-panel3d` (an
  angled terminal with a gradient, a clip mask and a blur, a swatch panel and a sub-viewport monitor; the same-frame
  check: the swatch turns green in the frame its document changes; 0 B per frame with a panel updating). The ADR 0200
  `label3d` golden is unchanged.
- Benchmarks: `WorldUiBenchmarks.Markers100Frame` (a tree tick with 100 markers moving on screen, RmlUi update included)
  and `Label3DRebuild`.

## Changes from the proposal

- **No `Text3DRenderer`, `text3d.slang` or `World3D.Labels` yet** (ADR 0251): `Label3D` stays a `GeometryInstance3D` on
  the mesh batcher, one draw per label and page. `FixedSize` is a CPU model-matrix scale instead of a vertex-shader
  term. `VisibilityRangeEnd` is `GeometryInstance3D`'s (no 10 % fade). There is no `Fog` toggle: labels always fog.
- **`TextLayout` breaks lines** into strings rather than writing `GlyphQuad`s into a caller span; layout only runs when a
  label changes. `Font.Draw` keeps its single-line path; `Label2D` and `Label3D` share the breaking.
- **`AlphaCut`** reuses `SpriteAlphaCut`; `TextRenderPriority` is the text's material priority (Godot's
  `render_priority`, since `VisualInstance3D.RenderPriority` is node order).
- **Markers move rows from C#** between content refreshes (ADR 0254, open question 3 measured): the proposal's
  data-bound transform alone re-evaluated every row's views each frame (≈ 1.1 ms for 100). With the fast path 100 markers
  cost ≈ 0.55 ms on a busy machine, above the proposal's 0.3 ms target; re-measure on the quiet baseline machine.
  `ContentRefreshSeconds` is new.
- **Icons:** the host uses `map-pin` too (`map-pins` is not in the icon set).
- **Not built:** G11.9 and the panels' stand-in data in the scene view. A CJK face is not bundled: the Demo shows Spanish
  and `qps`, and the fallback golden uses Cyrillic through Roboto Mono.
- **Panels (G11.5/G11.6, ADRs 0252/0253):**
  - The stack is not sized once a frame from the panels rendered. It is the maximum of the swapchain and every
    registered panel, so a resize does not recreate it while panels come and go.
  - Panel images are created when the panel is recorded, in `UiServer.Process` before `PrepareFrame`. Materials then
    bind the image the pre-scene pass renders that frame.
  - Owed renders take priority over distance, so a panel never starves for its first image.
  - `Brightness` is the emission energy of an unshaded panel (albedo black, emission = the panel).
  - `MaxInteractDistance` and `RequireLineOfSight` apply to the mouse and crosshair. Manual pointers are the caller's.
  - The icon is `device-desktop` (the atlas has no `device-tv`).
  - `UiServer.MaxPanelRendersPerFrame`, `PanelsRenderedLastFrame`, `HoveredPanel`, `FocusedPanel`,
    `UiPanel3D.HasPointer`, `PointerPosition`, `QuadSize` and `Texture` are new.
  - Panel textures clamp.
  - The editor's preview opens at `UiPreview.FixedSize`; panels in the scene view render without stand-in data.
  - `SubViewport.GetTexture()` keeps `FromViewport`; it shares `TextureGpu`'s borrow code with `FromGpuSource`.

## Open questions decided here

- Open question 1 (text technique): coverage bitmaps at `FontSize`, the proposal's default.
- Open question 3 (marker positioning): measured; data-bound `transform` on content refresh plus `SetProperty` from C#
  on other frames.
- Open question 4 (shim element creation): not needed; the row model, no native rebuild.
- Open question 6 (panel input precedence): the default, screen layers before panels; `InputBeforeUi` nodes still come
  first (ADR 0253). The owner may revisit it.
- Open question 7 (panels and post): the default, panels are scene geometry, so TAA and glow apply. The owner may
  revisit it.
- Questions 2 and 5 stay open in the [proposal](future/world-space-ui.md#open-questions).

## Related

[Game UI](game-ui.md) (layers, documents, data models), [2D canvas](canvas.md) (canvas text), [Localization](localization.md)
(`Atr`, font fallback), [Physics](physics.md) (queries), [Editor](editor.md), [Demo](demo.md).
