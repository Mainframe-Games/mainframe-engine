# ADR 0142 — Godot's math and the 2D helpers a game needs are engine core

- **Date:** 2026-10-06
- **Status:** accepted (Brogan: "everything in the Godot namespace in the game should be core to the engine"; one
  Vector2 type)

## Context

Crash Site Defense carried a `namespace Godot` layer: Godot 4.7.2's C# math structs (its own `Vector2`, `Vector2I`,
`Rect2`, `Rect2I`, `Transform2D`, `Color`, `Mathf`) beside the engine's `System.Numerics` types, plus Godot-shaped
statics (`GD`, `OS`, `Time`, `DisplayServer`, `FileAccess`, …) and a world-space `Label`. Games on the engine should not
need such a layer.

## Decision

1. **One 2D vector type: `System.Numerics.Vector2`.** Godot's `Vector2` methods and constants become C# 14 extension
   members on it (`Math/Vector2Extensions.cs`: `Normalized` with Godot's zero rule, `DistanceTo`, `DirectionTo`,
   `MoveToward`, `Rotated`, `Angle`, `Lerp`, `Snapped`, `Up`/`Down`/`Left`/`Right`, `FromAngle`, …), same formulas.
2. **New core types** copied from Godot 4.7.2 (MIT, attributed in each file): `Mathf` (with `MathfEx`), `Vector2I`,
   `Rect2I`, `Color` + `Colors`, `Side`. `Color` converts implicitly to and from `Vector4` (the renderers' form). They
   are built-in value types for serialization (number arrays, the shapes games already stored) and the generator.
   Godot's OkHsl members are left out (they called into Godot's native code).
3. The engine's own `Rect2`/`Transform2D` gain the Godot members games use (no second `Rect2`/`Transform2D`).
4. **Runtime helpers:** `Label2D` (one line of world-space text: font, size, colour, outline, shadow, alignment),
   `Image` (RGBA8 pixels on the CPU, with luminance/red expansion) + `Texture2D.FromImage`/`SetPixels(Image)`, `Time`
   (`TicksMsec`, `TicksUsec`, `UnixTime`), `GameHost.IsDebugBuild`, `GameHost.UserDataDirectory`/`UserDataPath`.

## Consequences

- Engine files that use `System.Drawing.Color` by its simple name now see `MainframeEngine.Color` first (types in the
  enclosing namespace win over `using` imports); they alias it (`DrawingColor`). Follow-up: one colour type for the 3D
  side too.
- Games keep Godot's numeric behaviour (the same formulas), with `System.Numerics` operators.
