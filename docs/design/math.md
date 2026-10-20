# Math and 2D helpers

The engine's value types for 2D games ([ADR 0142](../../memory/decisions/0142-godot-math-and-2d-helpers-in-core.md)).
GodotSharp 4.7.2's C# math (MIT, attributed in each file under `MainframeEngine/Src/Math/`), so formulas and edge cases
match Godot's.

| Type | Notes |
|---|---|
| `System.Numerics.Vector2` | the one 2D vector. `Vector2Extensions` adds Godot's members as C# 14 extensions: `Normalized()` (zero stays zero), `Length`-based helpers (`DistanceTo`, `DirectionTo`, `MoveToward`, `LimitLength`), `Angle`, `AngleTo`, `Rotated`, `Lerp`, `Slerp`, `Snapped`, `Floor`/`Ceil`/`Round`/`Sign`/`Abs`, `Clamp`, `Min`/`Max`, `Reflect`/`Bounce`/`Slide`/`Project`, `Orthogonal`, `IsEqualApprox`; constants `Vector2.Up/Down/Left/Right/Inf` (Y down) and `Vector2.FromAngle` |
| `Vector2I`, `Rect2I` | integer vector and rectangle; `Vector2I` → `Vector2` implicit, back explicit (truncates); `Rect2I` → `Rect2` implicit |
| `Rect2`, `Transform2D` | the engine's own; `Transform2D(rotation, origin)`, `transform * point`, `transform * rect` (bounding box) |
| `Color`, `Colors` | float RGBA with Godot's API (`FromHtml`/`ToHtml`, `Lerp`, `Darkened`/`Lightened`, HSV, named colours); converts implicitly to and from `Vector4` (the renderers' form). The 3D material side still uses `System.Drawing.Color` (files that use both alias it `DrawingColor`) |
| `Mathf` | `float` maths: `Lerp`, `MoveToward`, `Wrap`, `PosMod`, `Snapped`, `Clamp`, `DegToRad`, `LinearToDb`, easing, approximate comparisons |
| `Side` | rectangle sides (`Rect2I.GrowSide`) |

`Vector2I`, `Rect2I` and `Color` are built-in value types: `[Export]` members of these types store as number arrays in
`.mscene`/`.mres` (`[x, y]`, `[x, y, w, h]`, `[r, g, b, a]`).

## 2D helpers

- `Label2D`: world-space text (font, size, colour, outline, shadow): newlines break lines, `Autowrap` (Godot's
  `autowrap_mode`: `Off`, `Arbitrary`, `Word`, `WordSmart`) wraps at `Width`, lines sit `LineSpacing` apart (3, Godot's
  default) and align horizontally in `Width` and vertically in `Height` (`GetLines`, `GetMinimumSize`, `Size`); for
  prompts, hints and tags that move with the world. Menus and HUDs are UI documents.
- `BitMask` (ADR 0203; Godot's `BitMap`, renamed because `Bitmap` below differs only by case): a bit grid ported from
  Godot 4.7.2. `CreateFromImageAlpha` (a `Texture2D`, a `Bitmap` or RGBA8; alpha / 255 > threshold, 0.1),
  `Get/SetBit`, `SetBitRect`, `GetTrueBitCount`, `Resize`, `GrowMask`/`ShrinkMask` (a disc of radius n inside a rect),
  `OpaqueToPolygons(rect, epsilon = 2)` (one outline per 8-connected region, marching squares split where the outline
  touches itself, then Ramer–Douglas–Peucker; holes are not traced), `ConvertToImage`, `Blit`. Built at run time, not
  saved in scenes. Typical use: a sprite's alpha → `GrowMask` → `OpaqueToPolygons` → a `CollisionPolygon2D` or a
  `Polygon2D`.
- `Geometry2D` (ADR 0203): Godot's polygon helpers. `MergePolygons`, `ClipPolygons`, `IntersectPolygons`,
  `ExcludePolygons`, `ClipPolylineWithPolygon`, `IntersectPolylineWithPolygon`, `OffsetPolygon(delta, join)`,
  `OffsetPolyline(delta, join, end)` wrap Clipper2 1.5.4 with Godot's settings (5 decimals, even-odd, collinear points
  removed, miter limit 2, arc tolerance 0.25) and return `Vector2[][]` (holes wind opposite to outlines);
  `TriangulatePolygon` is the canvas ear clipper (empty on failure) and `IsPolygonClockwise` Godot's (+Y up).
  `GodotGeometryParityTests` checks both types against Godot's output for the same inputs.
- `Bitmap`: RGBA8 pixels on the CPU (`SetPixel`, `Fill`, `FromLuminance`/`FromRed` expand one-channel data like an
  L8/R8 texture samples); `Texture2D.FromBitmap` uploads it as a data texture, `SetPixels(bitmap)` refreshes it.
- `Time`: `TicksMsec`/`TicksUsec` (monotonic, since start) and `UnixTime` (wall clock).
