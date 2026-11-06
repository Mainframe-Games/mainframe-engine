# World-space UI and 3D text

**Milestone:** [Gameplay toolkit](../milestones.md#gameplay-toolkit-) (G11) · **Status:** ✅ G11.1–G11.9 shipped; the
proposal moved here (its changes are in [Changes from the proposal](#changes-from-the-proposal)) · **Decisions:**
[ADR 0200](../../memory/decisions/0200-label3d-first-slice.md) (first `Label3D`),
[ADR 0216](../../memory/decisions/0216-viewport-textures-on-3d-materials.md) (sub-viewport textures on meshes),
[ADR 0251](../../memory/decisions/0251-label3d-on-the-mesh-batcher.md) (labels, layout, fallbacks),
[ADR 0254](../../memory/decisions/0254-markers-as-rows-of-one-document.md) (markers),
[ADR 0252](../../memory/decisions/0252-ui-panels-render-before-the-scene.md) (panels render before the scene),
[ADR 0253](../../memory/decisions/0253-ui-panel-input.md) (panel input),
[ADR 0257](../../memory/decisions/0257-world-ui-owner-answers.md) (the owner's answers: `IgnoreExposure`, occlusion, input
order, panels and post),
[ADR 0258](../../memory/decisions/0258-batched-label3d-renderer.md) (the batched text renderer)

Text and UI in the 3D world: `Label3D` for signs and tags drawn in the scene, `UiMarker3D` for nameplates, health bars,
prompts and waypoints drawn on the HUD at a 3D point, `UiPanel3D` for terminals and in-world menus (a whole RmlUi context
on a quad, clicked with the mouse, a crosshair or code), and the camera helpers they use.

| | `Label3D` | `UiMarker3D` | `UiPanel3D` |
|---|---|---|---|
| Drawn | in the scene pass, batched (HDR, tonemapped, fog) | after the tonemap, on a screen `UiLayer` (exact colours) | in the scene, from the panel's own texture |
| Hidden by walls | depth test (`NoDepthTest` turns it off) | physics occlusion (optional) | depth test |
| Size | world units (`PixelSize`), or fixed pixels (`FixedSize`) | dp, optional distance scale | world units (`Resolution` × `PixelSize`) |
| Content | plain text, multi-line | any RML row template (one host document) | any RML (its own context) |
| Input | none | none | mouse ray, crosshair, code (quad or mesh) |
| Exposure | applies (opt out: `IgnoreExposure`) | none (after the tonemap) | applies (unshaded: opt out with `IgnoreExposure`) |
| 100 instances | a few draws in all | one document, one list binding | not intended (≤ 8 rendered per frame) |

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
- `LocaleFallbackFonts` (internal) loads the faces the project's `FontFallbackTable` (`Assets/locale/fonts.mfres`) names for
  `Tr.LocaleChain`, the same table the game UI loads into RmlUi ([Localization](localization.md)). `Label3D` uses them when
  its font lacks a glyph, so CJK text shows once the project lists a CJK face.

## Label3D

Godot's `Label3D`: glyph quads laid out from the font's atlas, rebuilt only when the text, a property or the locale
changes, and drawn by the batched text renderer (below; lit labels by the mesh batcher). Options: `Text` (translated with `Atr`),
`Font` (null = the engine's Lato Latin), `FontSize`, `PixelSize`, `Modulate`, `OutlineSize`, `OutlineModulate`,
`OutlineRenderPriority`, `TextRenderPriority`, `HorizontalAlignment`, `VerticalAlignment`, `Offset`, `Width` (500 px),
`Autowrap`, `LineSpacing`, `Uppercase`, `Billboard`, `FixedSize`, `NoDepthTest`, `Shaded`, `DoubleSided`, `AlphaCut`
(`Disabled` blends, `Discard` is a depth-writing cutout), `IgnoreExposure` (below). `GetLines()` returns the laid-out
lines and `GetAabb()` the local bounds.

- **Lines.** The block is as wide as its widest line (or `Width` when wrapping) and is aligned around the node; each line
  aligns inside the block.
- **`FixedSize`.** The renderer scales the model matrix per view (`Billboard.FixedSize`, after the billboard) so one
  label pixel covers one framebuffer pixel at any distance. Like the billboard, it is CPU-side: the colour pass, picking
  and (for mesh labels) casters all see the same quad.
- **Picking and the editor.** Labels pick through the object-ID pass (the batched renderer draws their quads with the
  label's id; cutouts discard their holes) and draw in the scene view.

### Batched text renderer

ADR 0258. `Text3DRenderer` (`Src/Rendering/Text/`, one per render server beside the mesh batcher, like the particle
renderer) draws every **unshaded** label of a world. A lit (`Shaded`) label, and an `AlphaCut = Discard` cutout that casts
shadows, stay an `ArrayMesh` on the mesh batcher (ADR 0251), so lighting and cutout shadows keep working;
`Label3D.IsBatchedText` says which path a label takes.

- **Glyph runs.** A batched label keeps a run of glyph quads in label space (outline glyphs first), rebuilt only when it
  changes. `World3D` lists its labels. Before the frame records (`RenderServer.PrepareFrame`) the renderer syncs the runs
  and gives each atlas page a slot: pages sit in **groups of eight**, one descriptor set per group (a sampler and eight
  images), so glyphs of different pages and fonts share a draw. Pages whose pixels changed re-upload through
  `TextureGpu`.
- **Per view.** Each label is posed on the CPU (`Billboard.Face`, `Billboard.FixedSize`), culled by its bounds and
  `VisibilityRangeBegin/End`, and ordered: cutouts, then blended labels by `TextRenderPriority` and back to front, then
  `NoDepthTest` labels the same way. Inside a label the outline draws first, unless `OutlineRenderPriority` is above
  `TextRenderPriority`. A label record (model matrix, linear colours, object id) goes to a per-frame-slot storage buffer,
  and one 40-byte instance per glyph (rectangle, UVs, record, page) to a per-frame-slot vertex buffer.
- **Draws.** One instanced draw of six vertices per glyph for each run of glyphs with the same pipeline (depth test,
  blend or cutout, cull) and page group: a world of labels in one font is typically one to three draws. The shaders
  (`Text/Text3D.vk.{vert,frag}`, `include/text3d.slang`) build the quad from `SV_VulkanVertexID`, sample the glyph's page
  under a branch per slot (gradients taken first), and match the unshaded mesh path: colour × atlas texel, fogged.
  Blended glyphs use straight alpha without depth writes; cutouts discard below 0.5 and write depth.
- **Order in the frame.** Labels draw last in a view's scene pass, after the blended meshes and blended visuals. They are
  not in the depth prepass (no motion vectors), like other blended surfaces.
- **Counters.** `RenderServer.Text3DStats`: labels drawn and culled, glyphs, colour and object-ID draws, resident pages.
  Steady frames allocate 0 B.

### Ignoring exposure

ADR 0257. `Label3D.IgnoreExposure` and `UiPanel3D.IgnoreExposure` (off by default) divide the surface's colour by the
view's **effective exposure**: the frame's exposure (the project's, or a Godot tonemapper's), times auto exposure's
`scale / adapted luminance` while the post tonemap runs it. A sign or terminal then keeps its authored brightness while
the camera adapts; the tonemap (ACES) still applies, so the colour is not exact (markers stay the tool for exact colours).

- Auto exposure copies its 1 × 1 adapted luminance into a per-frame-slot readback buffer, so the CPU knows it a couple of
  frames late (`RenderServer.MainViewExposure`). During a fast adaptation an `IgnoreExposure` surface trails the scene by
  those frames.
- A batched label multiplies its record's colours by `1 / exposure`. An unshaded panel's emission energy becomes
  `Brightness / exposure`, rewritten only when it moves by more than 0.5 %. Lit labels and panels ignore the flag.

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
  emission multiplier: above 1 the glow catches it), `IgnoreExposure` (an unshaded panel keeps its brightness under any
  exposure, [above](#ignoring-exposure)), `UpdateMode` (`Always`, `Once`, `Disabled`), `MaxUpdateDistance`
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
- **Custom meshes** (G11.9): with a `Mesh`, `TryRayToPixel` tests the mesh's triangles in local space (Möller–Trumbore over
  the surfaces' CPU arrays, front faces only) and maps the nearest hit through its UVs (0–1 covers the panel). A held
  pointer that leaves a mesh keeps its last pixel (a quad's pointer follows its plane instead).

## Editor

- `Label3D` (`letter-t`) draws and picks in the scene view.
- `UiMarker3D` and `UiMarkerHost` use the `map-pin` icon. A marker shows a map-pin scene badge at its point
  (`WorldOffset` above the node) with a dashed line to the node; the badge picks and box-selects. Markers are not drawn
  in the editor (screen layers of an edited scene are inert).
- `UiPanel3D` (`device-desktop`) draws in the scene view with the **UI preview's stand-in data** (G11.7): game code creates
  a panel's data models and does not run in edit mode, so the viewport (`PanelStandIns`) reads the bindings of each
  panel's documents and binds stand-ins in the panel's context, as the [UI preview](editor.md#ui-preview) does (text shows
  each variable's name, conditions are true, lists have three rows), then reloads the documents; the panel renders once
  after each load. It binds again when a document's source changes. Its inspector has an icon button, "Open document in
  UI preview", which opens its first document's preview tab at the panel's `Resolution` (`UiPreview.FixedSize`, centred in
  the view area).

![A nameplate marker selected in the editor](../images/editor-world-ui.png)

![The Demo's terminal panel in the scene view, bound to stand-in data](../images/editor-world-ui-panel.png)

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
  `FixedSize` scale, alpha cut; which labels are batched, outline glyphs first and around the text, glyph runs rebuilt only
  on change, the lit label's mesh and materials), `UiMarkerTests` (projection, fade, scale, edge clamp and angle behind
  the camera, physics occlusion with the own body excluded, sort, signals, element mode, the fast path, 0 B over 100
  markers), `UiPanel3DTests` (documents and model names per panel, `TryRayToPixel` on rotated and scaled quads and through
  a bent mesh's UVs, mouse clicks and focus, layers first, text and Escape, the crosshair's click action, manual pointers
  with capture past the edge, line of sight, `UpdateMode`/distance/frustum gating, at most 8 renders per frame, emission
  with `IgnoreExposure`, 0 B with a panel updating), `PostExposureTests` (the effective exposure with and without auto
  exposure); editor `UiPreviewTests` (the panel preview button; a panel in the scene view bound to stand-ins, rebound when
  its document changes, dropped when it leaves); Demo `WorldUiSceneTests` (the terminal opens the gate through
  `PushPointer`, Tab switches to the crosshair).
- Render: `label3d` (ADR 0200's name tags, unchanged by the batched renderer), `label3d-layout` (multi-line, autowrap, a
  Cyrillic fallback through Roboto Mono, alpha cut, two `FixedSize` tags equal on screen), `label3d-batch` (300 labels
  over two fonts and three sizes: the scene checks ≤ 3 draws per page group, nothing on the mesh batcher, a label picked
  through the object-ID pass; 0 B per frame while labels move and the camera orbits), `label3d-exposure` (exposure 4 vs 1:
  the `IgnoreExposure` label and panel keep their exposure-1 colour, the others brighten), `ui-markers` (`#3366cc` bar
  fill exact after the tonemap, an edge waypoint) and `ui-panel3d` (an angled terminal with a gradient, a clip mask and a
  blur, a swatch panel and a sub-viewport monitor; the same-frame check: the swatch turns green in the frame its document
  changes; 0 B per frame with a panel updating).
- Benchmarks: `WorldUiBenchmarks.Markers100Frame` (a tree tick with 100 markers moving on screen, RmlUi update included)
  and `Label3DRebuild`.
- QA: `Tests/QA/world-ui.qa` (`just qa-editor Tests/QA/world-ui.qa`): a marker selected, a label picked, the terminal
  panel with stand-in data.

## Non-goals

- Shaping beyond kerning, right-to-left text, ligatures (the same limits as canvas text and RmlUi, M9).
- VR-specific UI (laser pointers, curved cylinders as a primitive): the manual pointer API lets a VR layer drive panels.
- Screen-space reflections or refraction of panels; panels in sub-viewport worlds beyond the first rendering view.
- Replicating markers or panel state over the network: panels are local UI; games replicate their own data.

## Changes from the proposal

- **`Label3D` options.** `TextLayout` breaks lines into strings rather than writing `GlyphQuad`s into a caller span
  (layout runs only when a label changes); `Font.Draw` keeps its single-line path, and `Label2D` and `Label3D` share the
  breaking (ADR 0251). `AlphaCut` reuses `SpriteAlphaCut`; `TextRenderPriority` is the text's material priority (Godot's
  `render_priority`, since `VisualInstance3D.RenderPriority` is node order). `VisibilityRangeEnd` is
  `GeometryInstance3D`'s (no 10 % fade). There is no `Fog` toggle: labels always fog.
- **The batched renderer** (ADR 0258) differs from the proposal's `Text3DRenderer`:
  - Labels are posed on the CPU (billboard and `FixedSize` in the model matrix, as ADR 0251 does for meshes), not in the
    vertex shader; the shader only builds the quad.
  - Pages are grouped eight to a descriptor set and picked per glyph, so a draw spans pages and fonts; the proposal's
    "one draw per atlas page" would have broken back-to-front order across fonts.
  - Lit labels and shadow-casting cutouts stay meshes on the mesh batcher (the proposal made labels unshaded and
    shadowless). `World3D` keeps an internal label list instead of a public `World3D.Labels`.
  - The pipelines are built lazily from depth test × blend/cutout × cull (three cull modes for mirrored single-sided
    labels), not a fixed set of eight. Atlas pages upload through `TextureGpu` (a changed page re-creates its GPU image,
    as before) instead of dirty-row uploads.
  - The outline and text order per label follows their two priorities; between labels, `TextRenderPriority` then
    distance.
- **Markers move rows from C#** between content refreshes (ADR 0254, open question 3 measured): the proposal's
  data-bound transform alone re-evaluated every row's views each frame (≈ 1.1 ms for 100). With the fast path 100 markers
  cost ≈ 0.55 ms on a busy machine, above the proposal's 0.3 ms target; re-measure on the quiet baseline machine.
  `ContentRefreshSeconds` is new.
- **Icons:** the host uses `map-pin` too (`map-pins` is not in the icon set); panels use `device-desktop` (the atlas has
  no `device-tv`).
- **Panels (G11.5/G11.6, ADRs 0252/0253):**
  - The stack is not sized once a frame from the panels rendered. It is the maximum of the swapchain and every
    registered panel, so a resize does not recreate it while panels come and go.
  - Panel images are created when the panel is recorded, in `UiServer.Process` before `PrepareFrame`. Materials then
    bind the image the pre-scene pass renders that frame.
  - Owed renders take priority over distance, so a panel never starves for its first image.
  - `Brightness` is the emission energy of an unshaded panel (albedo black, emission = the panel).
  - `MaxInteractDistance` and `RequireLineOfSight` apply to the mouse and crosshair. Manual pointers are the caller's.
  - `UiServer.MaxPanelRendersPerFrame`, `PanelsRenderedLastFrame`, `HoveredPanel`, `FocusedPanel`,
    `UiPanel3D.HasPointer`, `PointerPosition`, `QuadSize` and `Texture` are new.
  - Panel textures clamp.
  - The editor's preview opens at `UiPreview.FixedSize`. Stand-ins in the scene view are bound by the editor's viewport
    (`PanelStandIns`), not by the panel's own edit-mode render.
  - `SubViewport.GetTexture()` keeps `FromViewport`; it shares `TextureGpu`'s borrow code with `FromGpuSource`.
  - A held pointer that leaves a custom mesh keeps its last pixel (there is no plane to follow).
- **`IgnoreExposure`** (open question 2, ADR 0257) divides by an exposure the CPU reads back from auto exposure a couple
  of frames late, rather than sampling the adapted luminance in the shader.
- **Not built:** SDF/MSDF text (open question 1 stays coverage bitmaps), depth-buffer marker occlusion (open question 5:
  no), a bundled CJK face (the Demo shows Spanish and `qps`; the fallback golden uses Cyrillic through Roboto Mono), the
  `WorldUiAllocationTests` combined case (each part has its own 0 B test or render test), and the benchmarks' 1 000-label
  prepare and panel update cases. `baseline.json` has no `WorldUiBenchmarks` entries yet (quiet machine only).

## Decided questions

1. **Text technique:** coverage bitmaps at `FontSize`, Godot's default for non-MSDF fonts (ADR 0251).
2. **Exposure:** an opt-in `IgnoreExposure` on `Label3D` and unshaded `UiPanel3D`, off by default; ACES still applies
   (owner, ADR 0257).
3. **Marker positioning:** measured; data-bound `transform` on content refresh plus `SetProperty` from C# on other frames
   (ADR 0254).
4. **Shim element creation:** not needed; the row model, no native rebuild (ADR 0254).
5. **Depth-buffer marker occlusion:** no; physics rays only (owner, ADR 0257).
6. **Panel input precedence:** screen layers before panels; `InputBeforeUi` nodes still come first (ADR 0253, kept by the
   owner in ADR 0257).
7. **Panels and post:** panels are scene geometry, so TAA, glow, exposure and the tonemap apply (ADR 0252, kept by the
   owner in ADR 0257).

## History

### What shipped (ADR 0200)

A first slice for Buy My Snake Oil's NPC name tags (mainframe-engine#56): `Camera3D.UnprojectPosition` and
`IsPositionBehind`; `Label3D` as an `ArrayMesh` of glyph quads on the mesh batcher (outline surfaces per atlas page, then
text surfaces) with Godot's defaults; `StandardMaterial3D.NoDepthTest`. G11.1–G11.4 (ADR 0251) grew the options on that
mesh, and ADR 0258 moved unshaded labels to the batched renderer.

### What shipped (ADR 0216)

`SubViewport.GetTexture()` on 3D materials, for the Neon example's monitors and TV: any material texture slot takes a
viewport texture. `TextureGpu` borrows the view's colour image and follows it across resizes; colour slots sample through
an sRGB view of the `UNORM` target. A 3D view renders before the scene pass, so the scene shows its current frame; a 2D
view (`Disable3D`) shows the previous frame. G11.5 generalised the borrow path as `Texture2D.FromGpuSource` for panels.

## Related

[Game UI](game-ui.md) (layers, documents, data models), [2D canvas](canvas.md) (canvas text), [Localization](localization.md)
(`Atr`, font fallback), [Physics](physics.md) (queries), [Editor](editor.md), [Demo](demo.md).
