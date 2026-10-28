# Animated sprites (`SpriteFrames`, `AnimatedSprite2D`)

**Status:** shipped (Gameplay toolkit G2a; [ADR 0223](../../memory/decisions/0223-sprite-frames-and-animated-sprite2d.md),
editor [ADR 0224](../../memory/decisions/0224-sprite-frames-editor.md)). Tile maps (G2b), which reuse
`SpriteSheetGrid` for atlas sources, are in [Tile maps](tile-maps.md).

Flipbook animation for the [2D canvas](canvas.md), Godot 4's API: a `SpriteFrames` resource holds named animations
cut from sprite sheets, and an `AnimatedSprite2D` plays one with Godot's timing and signals.

![The SpriteFrames editor in the inspector of the Demo's showcase hero](../images/editor-sprite-frames.png)

## Types

| Type | File | Role |
|---|---|---|
| `SpriteSheetGrid` | `MainframeEngine/Src/Scene/Nodes2D/Sprites/SpriteSheetGrid.cs` | A uniform grid over an image: cell size, margins (top-left), separation |
| `SpriteFrame` | `Sprites/SpriteFrames.cs` | `[SerializableValue]` record struct: texture index, region (texture pixels), relative duration |
| `SpriteAnimation` | same | Inline resource: `Name`, `Speed` (fps, default 5), `Loop` (default on), `Textures`, `Frames` |
| `SpriteFrames` | same | Resource (`.mres`): `Animations` with cached, allocation-free name lookups |
| `AnimatedSprite2D` | `Sprites/AnimatedSprite2D.cs` | The node: plays and draws the current frame |

```csharp
var sheet = ResourceLoader.Load<Texture2D>("Assets/Sprites/hero.png");
var frames = new SpriteFrames();
var run = frames.AddAnimation("run");
run.Speed = 10;
run.AddFrames(sheet, new SpriteSheetGrid(new Vector2I(32, 32)), [new(0, 1), new(1, 1), new(2, 1), new(3, 1)]);
ResourceSaver.Save(frames, "Assets/Sprites/hero.mres");

var hero = new AnimatedSprite2D { SpriteFrames = frames, Autoplay = "run" };
hero.AnimationFinished += OnLanded;
```

## Sprite sheets: `SpriteSheetGrid`

Godot's "Select Frames" layout, also the layout of a tile set atlas (G2b): cells of `CellSize` start `Margins` pixels
from the top-left corner and are `Separation` apart.

- `FromCounts(imageSize, columns, rows, margins, separation)`: cell = (image − margins − separations) / count, integer
  division as in Godot's dialog.
- `GridSize(imageSize)`: whole cells that fit; `CellRect(cell)` / `CellRegion(cell)`: texture pixels; `CellAt(pixel)`:
  the cell under a point, (−1, −1) in a margin or a separation gap.

Slicing is data in the resource that uses it, not an import setting: a sheet stays one `Texture2D` with one `.meta`,
and several animations (or a tile set) can cut it differently. Pixel-art sheets are drawn with the canvas item's
`TextureFilter`, so set `Nearest` on the scene's root `Node2D` (children inherit it).

## `SpriteFrames`

Each `SpriteAnimation` keeps a texture table and value-typed frames: `SpriteFrame.Texture` indexes `Textures`, a zero
`Region` draws the whole texture, `Duration` is relative (2 lasts twice as long as 1). There is no `AtlasTexture` and
no inline resource per frame. In a `.mres` each frame is one line of six numbers `[texture, x, y, w, h, duration]`:

```json
"SpriteAnimation_hdzjq": {
  "type": "SpriteAnimation",
  "props": {
    "Name": "run",
    "Speed": 10,
    "Textures": [ { "res": "Texture2D_srb15" } ],
    "Frames": [
      [0, 0, 32, 32, 32, 1],
      [0, 32, 32, 32, 32, 1]
    ]
  }
}
```

API: `GetAnimation(name)`, `HasAnimation`, `GetFrameCount`, `AddAnimation`, `RemoveAnimation`, `RenameAnimation`,
`NotifyChanged()` (after editing the list in place); on an animation `AddFrame(texture, region, duration, at)`,
`AddFrames(texture, grid, cells)`, `TryGetFrame(index, out texture, out region)`, `GetFrameDuration`,
`GetOrAddTexture`. Name lookups go through a cached dictionary that is checked against the list on every hit, so
renames and in-place edits are seen at once and no lookup allocates.

## `AnimatedSprite2D`

Exports (Godot's names and defaults): `SpriteFrames`, `Animation` ("default"), `Autoplay`, `Frame`, `FrameProgress`,
`SpeedScale`, `Centered`, `Offset`, `FlipH`, `FlipV`. Methods: `Play(name, customSpeed, fromEnd)`,
`PlayBackwards(name)`, `Pause()`, `Stop()`, `IsPlaying()`, `SetFrameAndProgress(frame, progress)`,
`GetPlayingSpeed()`, `GetRect()`, `CurrentAnimation`. Signals: `AnimationChanged`, `AnimationFinished`,
`AnimationLooped`, `FrameChanged`, `SpriteFramesChanged`.

- **Timing** is Godot's `_notification(INTERNAL_PROCESS)`: a frame lasts `Duration / (Speed × |playing speed|)`
  seconds; the remainder carries over, so a long delta steps several frames and raises `FrameChanged` once per step.
  At the end a looping animation raises `AnimationLooped` then `FrameChanged`; a non-looping one pauses on its last
  frame (the first when backwards) and raises `AnimationFinished`. As in Godot, a step's speed is taken before it
  moves to the next frame (uneven durations with long deltas show it).
- **Playing the current animation again** resumes it, restarting only after it finished. A negative `SpeedScale` or
  custom speed plays backwards; setting `Frame` then starts at the end of that frame.
- **Drawing** is `Sprite2D`'s (shared `SpriteRects.Destination`): one `TextureRectRegion` quad of the frame's region,
  centred or at `Offset`, flips as negative sizes. The node redraws only when the frame changes or the resource (or
  the current animation) raises `Changed`.
- **No allocation per frame**: name lookups, float maths, parameterless `Action` signals and one quad per redraw. The
  unit gate plays 1 000 sprites for 300 ticks, the render-host gate 1 000 sprites for 240 frames, both at 0 bytes.
- **Edit mode**: not a tool node, so it does not process in the editor and `Autoplay` runs only in a game. The
  SpriteFrames editor previews by setting `Frame` (below).

## With keyframe animation

`AnimatedSprite2D` is a self-timed flipbook (walk cycles, idle loops, effects); an `AnimationPlayer`
([Animation](animation.md)) choreographs properties over time. They combine as in Godot: a player can key
`Animation`, `Frame` and `FlipH` or call `Play`/`Stop` from a method track once G1a has property and method tracks;
`Sprite2D` + `Hframes`/`Vframes` + a keyed `Frame` is the other Godot pattern. A frame strip is not a timeline track.

## Editor

- **SpriteFrames editor** (a custom inspector header on `SpriteFrames` and `AnimatedSprite2D`): the animation list
  (name, frame count, fps, loop and autoplay marks) with Add, Duplicate, Rename, Delete, Loop, Autoplay on Load and a
  Speed field; the frame strip (thumbnails of each region, index, duration) with Play Backwards / Play / Stop (on a
  sprite), Add Frame from Image, Add Frames from Sprite Sheet, Copy, Paste, Move Left/Right, Delete and a Duration
  field. Icon buttons with tooltips. On a sprite, picking an animation sets its `Animation`; a sprite without
  `SpriteFrames` gets a New button. Every edit is one undo entry; a `.mres` edited from a scene is saved with it.
- **Preview**: Play steps the sprite's `Frame` from the editor's tick with no undo entry and without dirtying the
  scene; Stop (or the end of a non-looping animation) restores the frame.
- **Select Frames** (`sheet_slicer.rml`): the sheet with its grid; split by count (Horizontal × Vertical) or by cell
  size, with Separation and Offset; click cells to pick them (numbered in click order, the frame order); Select All,
  Clear, "Add N Frames". The last grid per sheet is remembered in `<project>/.mainframe/cache/sheet_grids.json`.
- **New SpriteFrames from Sheet…** (FileSystem context menu on an image): Select Frames, then `<image>.mres` with one
  animation per picked row ("default" for a single row), opened in the inspector.
- **Picking**: in a 2D tab a click inside a `Sprite2D`'s or `AnimatedSprite2D`'s drawn rect selects it (after
  collision shapes and node origins).

![Select Frames slicing the Demo's hero sheet](../images/editor-sheet-slicer.png)

## Tests

- Unit (`Tests/MainframeEngine.Tests/Canvas/`): `SpriteSheetGridTests`, `AnimatedSprite2DTests` (timing, durations,
  speed scale, loop and finish signal order, backwards, from the end, resume, `SetFrameAndProgress`, resource
  switching, autoplay in edit mode, drawing, redraws only on frame change, the 1 000-sprite allocation gate),
  `SpriteFramesSerializationTests` (`.mres` round trip with one line per frame and identical bytes on re-save, the
  scene round trip, the codec, cached lookups).
- Render (`CanvasRenderTests`): `animated-sprite` at frames 1 and 15 (frames, both flips, offset, speed ×3,
  backwards, uneven durations, a stopped frame) with `moltenvk` goldens, and the 1 000-sprite allocation run.
- Editor (`SpriteFramesEditorTests`): the slicer model and grid memory, snapshot undo of every edit, the inspector
  actions on a sprite, the preview, Select Frames, "New SpriteFrames from Sheet", sprite picking.
- Demo: `sprites_2d` (`Sprites2DSceneTests`, the scene file contract, the headless run).

## Changes from the proposal

- The SpriteFrames editor is an inspector header, not a tab of a new bottom panel (ADR 0224); the bottom-panel tabs
  come with G2b's TileMap palette.
- `SpriteAnimation` keeps its own `Name` export (not `ResourceName`), as proposed; setting `Animation` without
  `SpriteFrames`, or assigning an empty `SpriteFrames`, keeps the names instead of clearing them (scene load order).
- Not built: drag-reordering frames and frame zoom in the strip, auto-slice by opaque bounds, a project-wide default
  canvas texture filter.
- The Demo's animated sprite has its own scene, `sprites_2d` (Sprites 2D), with the generated `Assets/Sprites2D/hero.png`
  and `hero.mres`, which the planned `tiles_2d` scene can reuse; number key 0 opens the tenth Demo tab.

## Related

[2D canvas](canvas.md) · [Editor](editor.md) · [Scene serialization](scene-serialization.md) · [Demo](demo.md) ·
[Tile maps](tile-maps.md)
