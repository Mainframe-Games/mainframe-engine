# ADR 0223 — SpriteFrames as texture table + regions; AnimatedSprite2D on Godot's timing

- **Date:** 2026-10-28
- **Status:** accepted
- **Docs:** docs/design/animated-sprites.md (from the proposal docs/design/future/2d-content.md, G2a)

## Context

The canvas had `Sprite2D` with `Hframes`/`Vframes` but nothing that times frames. Godot's `SpriteFrames` stores one
`Texture2D` per frame, usually an `AtlasTexture` (a region of a sheet). The engine's `Texture2D` is concrete (file,
pixels, GPU upload, viewport variants): an `AtlasTexture` subclass would need every texture consumer — canvas
primitives, 3D materials, UI registration — to resolve a region. The proposal's open questions asked whether to add
`AtlasTexture`, where slicing should live, and whether to auto-slice packed sheets.

## Decision

- **No `AtlasTexture`.** A `SpriteAnimation` keeps a texture table (`Textures`) and value-typed frames
  (`SpriteFrame(int Texture, Rect2 Region, float Duration)`; a zero region is the whole texture). Frames are written
  by a built-in codec as six numbers, one line per frame. A later Godot importer can map `AtlasTexture` frames onto
  this without new texture types.
- **Slicing is data in the resource that uses it**, not an import setting: `SpriteSheetGrid` (cell size, margins at
  the top-left, separation; `FromCounts`, `GridSize`, `CellRect`, `CellAt`) is the shared maths for the sheet slicer
  now and the tile set atlas (G2b) later. A sheet keeps one `Texture2D` and one `.meta`.
- **`SpriteAnimation` is a `Resource` with its own `Name`** (inline sub-resource) so the inspector, undo and the
  generated serialization work unchanged; `SpriteFrames` caches name → index and validates hits against the list, so a
  rename or an in-place edit never returns a stale animation and lookups allocate nothing.
- **`AnimatedSprite2D` ports Godot 4's playback exactly**, including its quirks: the speed of a step is taken before a
  frame change (the remainder of a step that starts a frame runs at the previous frame's rate), `FrameChanged` fires
  once per frame step, `AnimationLooped` before the `FrameChanged` of the wrap, `AnimationFinished` pauses on the
  last frame. Playback allocates nothing; the node redraws only when its frame changes. It shares `Sprite2D`'s rect
  maths (`SpriteRects.Destination`).
- **Deviations:** setting `Animation` while `SpriteFrames` is null keeps the name (scene loading order) instead of
  clearing it; assigning an empty `SpriteFrames` keeps `Animation`/`Autoplay` (Godot's resources always start with a
  "default" animation, ours get it from the editor); a non-positive duration counts as 1; signal handlers that stop
  the sprite end the step loop.
- **Auto-slice by opaque bounds** (packed sheets) and a project-wide default canvas texture filter are left for later
  (pixel-art scenes set `TextureFilter = Nearest` on their root, as the Demo does).

## Consequences

- Frames are cheap values; duplicating or copying frames copies indices, so editor operations remap textures
  (`SpriteFramesEditing.PruneTextures`, `InsertFrames`).
- G2b's `TileSetAtlasSource` reuses `SpriteSheetGrid` for its layout (`Margins`, `Separation`, `TextureRegionSize`).
