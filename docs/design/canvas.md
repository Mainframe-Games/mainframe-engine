# 2D canvas

## Purpose

Godot-compatible 2D drawing: canvas items (`Node2D`, `Sprite2D`, custom `OnDraw`), canvases and layers, draw order,
modulation and blending exactly as Godot 4.7's `RendererCanvasCull` + `RendererCanvasRenderRD` with `hdr_2d` off, so a
game ported from Godot draws the same pixels. Built for the Crash Site Defense port (its `docs/porting.md`, rows E2/E3);
ADRs [0110 Y-down](../../memory/decisions/0110-y-down-2d.md), [0111 canvas renderer](../../memory/decisions/0111-canvas-renderer.md).

| Piece | Where | What |
|---|---|---|
| Nodes | [Scene/Canvas/](../../MainframeEngine/Src/Scene/Canvas/), [Scene/Nodes2D/](../../MainframeEngine/Src/Scene/Nodes2D/) | `CanvasItem` (base of `Node2D`), `Sprite2D`, `Polygon2D`, `Skeleton2D`/`Bone2D`, `RemoteTransform2D`, `CanvasLayer`, `CanvasModulate`; `Canvas` (a viewport's root canvas or a layer's) |
| Commands | `CanvasDrawList`, `CanvasPrimitives` | per-item vertices/indices recorded by `OnDraw`; Godot's tessellation (64-segment circles, polyline strips with clamped joints, feathered antialiasing, ear-clipping triangulation) |
| Ordering | [Rendering/Canvas/CanvasCuller.cs](../../MainframeEngine/Src/Rendering/Canvas/CanvasCuller.cs) | port of `_cull_canvas_item`: z lists −4096…4096, tree order, show-behind-parent, y-sort, top level, viewport culling |
| Server | `CanvasServer` (`IFrameServer`) | runs queued draws, culls every canvas of the root viewport (root canvas + visible layers, by layer) into one `CanvasFrame` |
| Renderer | `VulkanCanvasRenderer` (`IOverlayRenderer`) | draws the frame into an RGBA8 UNORM layer (gamma space), composites it after the tonemap, below the game UI |
| Materials | `CanvasItemMaterial`, `ShaderMaterial` + `Shader` | blend mode (mix, add, sub, mul, premultiplied, disabled) and light mode; canvas shaders (E5, in progress) |
| Shaders | [Assets/Shaders/Canvas/](../../MainframeEngine/Assets/Shaders/Canvas/), `include/canvas.slang` | default vertex/fragment stages; push constants mirror Godot's world/canvas/screen transforms and canvas modulation |

## Model

- **2D is Y-down** (ADR 0110). `Transform2D` maths is Godot's.
- **Canvas items** keep a draw list. `QueueRedraw()` queues `RunDraw` for the end of the frame (`SceneTree.FlushCanvasRedraws`,
  called by the canvas server): the list is cleared, then the type's built-in draw (`Sprite2D`), the `Draw` signal and
  `OnDraw()` record into it. `Draw*` outside a draw throws. Items redraw on entering the tree and on becoming visible.
- **Canvases.** A canvas item whose parent is not a canvas item (or that is `TopLevel`) is a *root* of a canvas: the
  nearest `CanvasLayer` ancestor's, else its viewport's `RootCanvas` (layer 0). Roots draw in the order of their index
  among their parent's children (Godot's draw index), ties by registration. The root canvas transform is
  `SceneViewport.CanvasTransform` (the camera), a layer's is its own transform. Layers draw by `Layer`, the root canvas
  first among layer 0; hidden layers are skipped.
- **Order** within a canvas (CanvasCuller): an item is attached to the list of its absolute z (relative to its parent
  unless `ZAsRelative` is false); children follow their parent unless `ShowBehindParent`; a `YSortEnabled` item flattens
  its subtree (the item itself first, nested y-sort subtrees included) and orders it by the Y of each item's origin
  relative to the sort root, ties (is_equal_approx) by collection order. Lists are concatenated from the lowest z.
- **Modulate** multiplies down the tree; `SelfModulate` applies to the item only; an item with final alpha < 0.007 is
  skipped with its subtree. `CanvasModulate` (the last visible one on a canvas) multiplies every fragment of the canvas
  after the item's colour and before lights, except for unshaded materials.
- **Culling:** an item whose drawn bounds (transformed) miss the viewport rect is not attached; its children still are.
- **Sub-items** (G2b, ADR 0225): an item may own draw lists besides its own (`CanvasSubItem`, a `TileMapLayer`'s chunks
  and y-sorted rows). They attach after the item's list and before its children, each culled on its own bounds with a z
  offset and the owner's modulate, material and texture state; under y-sort each is an entry sorted with the children.
  `CulledCanvasItem.DrawList` is the list the frame reads. `EditorModulate` (never saved) multiplies like `Modulate`;
  the editor dims layers with it.
- **Textures** are sampled raw (UNORM, gamma space), straight alpha, with the item's resolved `TextureFilter`/
  `TextureRepeat` (default linear, no repeat); `draw_texture_rect(tile: true)` forces repeat for that rect.

## Canvas shaders

`.gdshader` files in Godot's shading language (`shader_type canvas_item`) load as `Shader` resources (`.gdshader` importer,
`Shader.Load`); `CanvasShaderCompiler` wraps them in a Slang template compiled with `-allow-glsl`, so the Godot code keeps
its GLSL syntax and meaning (one-argument `matN(x)` calls are rewritten to `glsl_matN(x)` helpers, because Slang fills every
element from a scalar where GLSL builds a diagonal; [ADR 0113](../../memory/decisions/0113-canvas-shaders-godot-language.md),
[ADR 0144](../../memory/decisions/0144-slang-shader-language.md)).
Supported: uniforms of scalar/vector/matrix types and arrays (std140 block, set 1 binding 0, declaration order) with
defaults and hints (`source_color` kept raw as in Godot's non-HDR canvas, `hint_range` ignored), `sampler2D` uniforms
(set 1 bindings 1…, `filter_*`/`repeat_*`), `render_mode blend_mix|add|sub|mul|premul_alpha|disabled, unshaded,
light_only`, varyings (flat for integers), helper functions and constants, `vertex()` and `fragment()`, and the built-ins
VERTEX, UV, COLOR, TEXTURE, TEXTURE_PIXEL_SIZE, TIME, FRAGCOORD, SCREEN_UV, SCREEN_PIXEL_SIZE, MODEL_MATRIX,
CANVAS_MATRIX, SCREEN_MATRIX, PI, TAU, E. Items whose shader has `vertex()` keep local vertices (MODEL_MATRIX = the
item's canvas-space transform, CANVAS_MATRIX = camera × stretch) and draw alone; others are batched pre-transformed.
SPIR-V is built ahead of time with `slangc` and committed next to the source (`x.gdshader.vert.spv`, `.frag.spv`,
`.spvlock`), with `-obfuscate` so no name from the user's code reaches MoltenVK's Metal source (a local named `vertex` or
`device` would not compile there). Projects whose canvas SPIR-V was built before the move to Slang get a stale-shader
warning at load until it is rebuilt once with `just canvas-shaders <folder>`. The fragment stage compiles first; the vertex stage then writes only the inputs that stage kept (Slang drops
unread fragment inputs, and an unread vertex output is a validation warning; `SpirvInputs`):
`just canvas-shaders <folders>` / `just canvas-shaders-check`. `ShaderMaterial.SetShaderParameter(name, value)` sets
uniforms (vectors as `System.Numerics`, colours as `Vector4`, arrays, `Texture2D` for samplers).
Runtime-updated textures (`Texture2D.FromPixels` + `SetPixels`) re-upload when their version changes.
The same `Shader`/`ShaderMaterial` carry Godot `shader_type spatial` shaders to 3D meshes ([Shaders → Spatial
shaders](shaders.md#spatial-shaders), ADR 0205); `mf-shaders` builds both kinds.

## Camera and stretch

- **`Camera2D`** is Godot 4.7's, ported: while current it writes `SceneViewport.CanvasTransform` (anchor centre or top
  left, Godot zoom where 2 = twice as close, offset, limits, drag margins, position/rotation smoothing). The scroll updates
  when Godot updates it: in `OnProcess` (Godot's internal process; subclasses call `base.OnProcess` first), on transform
  changes without smoothing, and on every `Zoom`/`Offset` set, which keeps the smoothed position (so a script that sets
  the zoom every frame sees Godot's double smoothing step). `GetScreenCenterPosition()`; a camera entering a viewport
  without a current camera makes itself current (when `Enabled`).
- **Content scale** (Godot's window stretch) is computed by `ContentScale.Compute` (a port of
  `Window::_update_viewport_size`) on the root viewport each frame (`SceneViewport.SetSize`): `canvas_items` mode draws
  the canvas at the window's resolution from a base size, `Expand`/`Keep`/`KeepWidth`/`KeepHeight`/`Ignore`, fractional
  or integer scale, letterbox margins. `SceneViewport.GetVisibleRect()` is the visible area in canvas units;
  `StretchTransform` multiplies every canvas (layers included). Project file: `window.stretchMode` (`canvas_items`),
  `window.stretchAspect`, `window.stretchScale`, `window.stretchScaleMode`; base size = `window.width/height`.
  `rendering.canvasClearColor` sets `CanvasServer.ClearColor`. `window.contentScale: 1` makes `width/height` pixels on
  every display as in Godot (`EngineOptions.ContentScale`; a 1920×1080 window is 960×540 pt on a 2× screen), so a
  Godot-sized canvas draws at scale 1 and frame captures match Godot's.

## Frame

1. `Tree.Tick` (process), then frame servers: `CanvasServer.Process` flushes redraws, builds `CanvasFrame` (vertices in
   target pixels, pre-transformed and pre-modulated; one batch per run of equal texture, sampler, material, blend,
   primitive and canvas modulate) and uploads new textures (`VulkanCanvasRenderer.PrepareTextures`, so the uploads are
   recorded at the next frame's start).
2. `BeginOverlayPass`: `RecordOffscreen` writes the frame's geometry into per-frame-slot dynamic buffers and draws the
   batches into the canvas layer, cleared to `CanvasServer.ClearColor` (opaque: a 2D game; null: transparent over the
   3D scene). After the tonemap `RecordOverlay` composites the layer premultiplied (Godot's mix blend into a
   transparent target is premultiplied by construction), then the screen gizmos, the game UI and the dev overlay draw (`OverlayOrder`).

## Debug lines

A 2D sub-viewport (`Disable3D`) and, since G3a, a root viewport without a 3D camera (a 2D game) hand their `DebugLines`
and `OverlayLines` to the canvas: `CanvasServer` appends them (z ignored, through the stretch and canvas transforms) after
the viewport's canvas items and clears them, so collision shapes and navigation draw over the sprites. Views with a 3D
camera keep drawing them in the 3D scene pass ([Navigation](navigation.md)).

## Known issues

- Not yet: shader `light()`, `SCREEN_TEXTURE`/back buffer, 2D light shadows/normal maps/`DirectionalLight2D`, font oversampling and shaping beyond kerning,
  Camera2D physics interpolation, the `viewport` stretch mode (treated as `canvas_items`), `CanvasGroup.UseMipmaps`,
  `ClipChildrenMode.Only` hiding the owner, nine-patch, meshes/multimeshes, physics interpolation of canvas items, pixel snapping.
- 1 px lines (width −1) on exact integer coordinates rasterise on whichever side the GPU picks, as in Godot.

## 2D sub-viewports (ADR 0116)

A `SubViewport` with `Disable3D` is a 2D view: canvas items under it register with its own canvas, and the canvas server
draws it into an RGBA8 target before the main canvas (one `CanvasPass` of the frame each; nested views first),
cleared to transparent black with `TransparentBg` (else the canvas clear colour). `GetTexture()` returns a `Texture2D`
backed by that target for `Sprite2D`/draw calls. `UpdateMode.Once` draws on the next frame then keeps the image;
`Disabled` keeps it. A target unused for 600 frames, or whose view left the tree, is released.

The texture also works on 3D materials (ADR 0216, [materials-and-meshes.md](materials-and-meshes.md#offscreen-views-subviewport)):
the target is created `MUTABLE_FORMAT` so colour slots sample it through an sRGB view. The scene pass runs before the
canvas passes, so a material shows the image drawn the frame before: the one target is sampled, then overwritten, in
submission order (the canvas pass's incoming dependency waits for the fragment reads; its outgoing one and the barrier
after it make the new image visible to the next frame), so no second target or copy is needed. Sampling from a material
counts as use, so a disabled view's image stays while a screen shows it. The target holds premultiplied colour: give a
view shown on a mesh an opaque clear colour.

## Animated sprites

`AnimatedSprite2D` plays the named flipbook animations of a `SpriteFrames` resource (frames cut from sprite sheets by
`SpriteSheetGrid`) with Godot 4's timing and signals, and draws the current frame like `Sprite2D` (shared rect maths);
it redraws only when its frame changes. See [Animated sprites](animated-sprites.md) (ADR 0223).

## Tile maps

`TileMapLayer` paints cells of a `TileSet` (atlas sources cut by `SpriteSheetGrid`, collision polygons, custom data),
stored, drawn and collided in chunks: a chunk is a sub-item per z (or per row when y-sorted), rebuilt only when one of
its cells changes, so a layer using one atlas draws in about one batch. See [Tile maps](tile-maps.md) (ADR 0225).

## Particles

`GpuParticles2D` + `ParticleProcessMaterial` (Godot's names): a CPU simulation of the subset 2D games use — box/sphere/
point emission, direction ± spread, initial speed range, gravity, per-particle linear acceleration, start angle and scale
ranges, one colour; `Amount`, `Lifetime` (even restarts), `AmountRatio`, `Preprocess`, `LocalCoords`. ADR 0187 adds
`OneShot` (+ `Finished`), `Explosiveness`, a buffer-reusing `Restart`, and per material damping, spin, lifetime
randomness, a `ScaleCurve` (`Curve`) and a `ColorRamp` (`Gradient`) over each particle's life.
Each particle draws its texture centred on it (one canvas batch per emitter). Random streams differ from Godot's GPU ones.
`ParticleProcessMaterial` lives in `Src/Scene/Particles/` and is shared with `GpuParticles3D` ([3D particles](particles-3d.md),
ADR 0206); the 2D emitter ignores its 3D-only members (ring emission, flatness, initial ramp, hue variation, turbulence).

## Lights (ADR 0117)

`PointLight2D` (Godot's names and formula): its texture, centred on the node plus `Offset` and scaled by `TextureScale`,
adds `texture × Color × Energy × the item's colour` after the canvas modulation, so it shows through a `CanvasModulate`
night. It lights the items of its own canvas whose `LightMask` shares a bit with `RangeItemCullMask` and whose z is in
`RangeZMin..RangeZMax`; unshaded materials are not lit. Lights whose texture rect misses the drawn area are culled.
Up to 32 lights per frame (`CanvasFrame.MaxLights`, `limits.json` `MAX_CANVAS_LIGHTS`) in one per-frame-slot block,
sharing up to 8 distinct textures (`MAX_CANVAS_LIGHT_TEXTURES`, one sampler slot per texture; ADR 0186); each batch's
push constants carry the bits of the lights that reach it. No shadows or normal maps.
`Gradient` and `GradientTexture2D` (linear, radial, square, conic fills; Godot's sampling) generate light and sweep
textures in code or in a scene.

## Post-processing (ADR 0185)

A `CanvasEnvironment` node in the root viewport post-processes its canvas (Godot does this with a `WorldEnvironment` in
the Canvas background mode; the engine's `WorldEnvironment` is 3D-only). The main pass draws the canvases up to
`MaxLayer` into the canvas layer as before; then glow (a soft-knee bright-pass and a 13-tap down / tent up chain from
half size, up to five levels, wider levels weighted by `GlowSpread`), the adjustments (brightness, contrast,
saturation), lift/gamma/gain, tint, vignette, chromatic aberration and grain (`CanvasPost.vk.frag.slang`) write a second
target, the canvases above `MaxLayer` draw on top of it with a load pass, and the composite shows it. Everything runs
in gamma space on the RGBA8 layer (Godot's `hdr_2d` off): glow picks the pixels above a gamma-space luma threshold,
so emissive art is art that is bright after the canvas modulation. The game UI and the dev overlay are never touched.
`CanvasPostMath` mirrors the colour maths on the CPU. With no environment (or `Enabled` false) nothing changes.

## Text (ADR 0118)

`Font` (a `.ttf` resource; `Font.FromFile`/`FromData`) and `CanvasItem.DrawString` / `DrawStringOutline` (Godot's
parameters: baseline position, `HorizontalAlignment` in a width, size, modulate; outline size as Godot's). Glyphs are
read and rasterised in managed code (`TrueTypeFont`, `GlyphRasterizer`: TrueType outlines, GPOS/kern pair kerning,
exact-area coverage, outlines grown by `size / 4` px at 4× supersampling) into shared atlas pages per size and outline.
Metrics follow FreeType's rounding. Unhinted, whole-pixel pen positions, no oversampling under a scaled canvas.
Glyph placement is `Font.Layout` (one line into `GlyphQuad`s: pixel rectangle, atlas page and region), shared by
`DrawString` and `Label3D` ([ADR 0200](../../memory/decisions/0200-label3d-first-slice.md)).

## Polygons and bones (ADR 0202)

Godot's `Polygon2D`, `Skeleton2D`, `Bone2D` and `RemoteTransform2D`, with the same properties and rules:

- **`Polygon2D`** draws its points (`Polygon`, plus `Offset`) as indexed triangles (`DrawTriangles`).
  - Without `Polygons`, the outline is ear-clipped (`CanvasPrimitives.Triangulate`, Godot's) and the last
    `InternalVertexCount` points are left out. With `Polygons`, each index list is triangulated on its own, internal
    vertices included.
  - The triangulation is cached until the points or polygons change.
  - `UV` is in texture pixels: `S·(R·uv + TextureOffset)` divided by the texture size, where R is `TextureRotation` and S
    is `TextureScale`. Without UVs, the points are used.
  - `VertexColors` gives a colour per point, else `Color` is used.
- **Skinning** runs on the CPU, with Godot's GPU maths. `Skeleton` names a `Skeleton2D`, and `BonePaths` (relative to
  it) with `BoneWeights` give each bone's weight per point. Every vertex keeps its four heaviest bones, weights
  normalised; unweighted vertices stay put. In skeleton space a vertex moves by Σ wᵢ (boneᵢ · restᵢ⁻¹), where
  `Bone2D.GetSkeletonRest()` composes the parent bones' `Rest`s.
- **Bones:**
  - `Skeleton2D` numbers its `Bone2D` children, and theirs, in tree order.
  - A bone's transform change asks the skeleton's polygons to redraw (and to re-resolve their bones when bones are
    added or removed).
  - `Bone2D.Length`/`BoneAngle` autocalculate from the first child bone, as in Godot.
  - Godot's modification stack (look-at, IK) is not supported.
- **`RemoteTransform2D`** pushes its global or local transform to `RemotePath`: all of it, or only position, rotation
  or scale. It pushes in the frame's transform notifications. Physics poses are written before that point, and canvas
  redraws run after it, so a bone driven by a `RigidBody2D` deforms its polygon in the same frame.
- **Cost:** four 600-vertex, 25-bone polygons re-skin and redraw in about 0.15 ms per frame (Release) and allocate
  nothing. `DrawTriangles` writes vertices straight into the draw list (`CanvasDrawList.AddVertices` from spans).
- **Naming:** `Skeleton2D` is Godot's bone container. G17's planned Spine-like skeletal node is `SkeletonSprite2D`
  ([proposal](future/skeleton-2d.md)).

## Clip children (ADR 0119)

`CanvasItem.ClipChildren` (`AndDraw`; `Only` draws like `AndDraw` for now) makes the item a group with its subtree:
the culler tags the members (`CulledCanvasItem.ClipGroup`; an inner owner joins the outer group) and adds a composite
entry after the subtree. `CanvasServer` culls every canvas first, then emits one pass per group before the viewport's
pass: the owner draws as usual on a transparent target the size of the pass (`EnsureGroupTarget`, keyed by owner like
sub-viewport targets), the other members with the `Atop` blend (premultiplied output, `DST_ALPHA` × source, alpha
kept), so they show only where the owner has drawn. The viewport's pass draws the composite in the owner's place: a
quad over the owner's drawn bounds sampling the group target (`Texture2D.ForClipGroup`, premultiplied, unshaded,
since the group is already modulated and lit). Members drawn behind the owner (`ShowBehindParent`) land on an empty
target and do not show.

## Canvas groups

`CanvasGroup` (Godot's, `CANVAS_GROUP_MODE_TRANSPARENT`) takes the same path: the culler opens a group for it (unless it
is already inside one: a nested group or clip owner then draws ungrouped, where Godot warns), culls every child into it
(behind the parent or not) and adds its composite entry; the group draws nothing of its own. Members blend normally
(no `Atop`). `CanvasServer` records the members' drawn bounds (`CanvasGroup.GroupRect`, target pixels) while building
the group pass, and the composite is a quad over that rect grown by `FitMargin` (10), sampling the group target with the
group's colour premultiplied (Godot's default canvas group shader un-premultiplies the back buffer and multiplies by
`COLOR`, mix-blended: the same result). As in Godot, `Modulate` reaches the children before grouping and the composite
again, so fades use `SelfModulate`: two overlapping opaque squares at `SelfModulate` 0.5 are one even grey, at `Modulate`
0.5 the overlap shows (64/96 against Godot's 0.251/0.3765; the `canvas-group` render test checks Godot's values). A group
whose children draw nothing draws no composite. `ClearMargin` is kept for scenes (the whole target is cleared) and
`UseMipmaps` is not supported yet.

## Edit mode (ADR 0126)

A `Disable3D` sub-viewport draws its `DebugLines` and `OverlayLines` over its canvas (line batches, z ignored) and
clears them. In `SceneTree.EditMode` canvas items run only their built-in drawing, plus `OnDraw`/`Draw` on `[Tool]`
types, and a current `Camera2D` leaves its viewport's canvas transform alone. The editor's 2D view does not use these
yet (ADR 0126, Consequences).
