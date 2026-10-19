# ADR 0200 — Label3D and Camera3D.UnprojectPosition: the first G11 slice

- **Date:** 2026-10-20
- **Status:** accepted
- **Milestone:** G11 (part)
- **Docs:** docs/design/future/world-space-ui.md#what-shipped-adr-0200, docs/design/materials-and-meshes.md

## Context

Buy My Snake Oil puts a name tag over each of its 9 villagers (mainframe-engine#56). Each tag is a Godot `Label3D`:
- default font, 64 px, a 14 px translucent cream outline and dark brown text;
- Y-billboarded and `no_depth_test`;
- seen through a narrow, fov-20 camera from about 70 units away.

The port's HUD also needs `Camera3D.UnprojectPosition` to place its hold ring. G11 (`world-space-ui.md`) designs a
full text system: its own batched renderer and shader, multi-line text, fixed size, fog and fallback fonts. That is
more than the port needs now.

## Decision

- **`Camera3D.UnprojectPosition` / `IsPositionBehind`** take Godot's semantics. They return framebuffer pixels from a
  top-left origin, use `ProjectRay`'s synced camera and `GizmoProjection`'s maths, and treat anything nearer than `Near`
  as behind. A static overload takes an `ICamera` and a viewport size.
- **`Label3D` is a `GeometryInstance3D` on the mesh batcher, not a new renderer.**
  - It builds an `ArrayMesh` of glyph quads. The outline comes first, at `OutlineRenderPriority` (default −1), with
    one surface per atlas page. The text follows, one surface per page, at material priority 0.
  - Each surface uses a private unshaded, blended, double-sided `StandardMaterial3D` on the font's atlas page.
  - Picking, culling and ADR 0199's billboards come for free. The mesh is rebuilt only when a property or the locale
    changes.
- **`Font.Layout`** is extracted from `Font.Draw`, so 3D and canvas text place glyphs identically: same rounding,
  kerning and atlas pages.
- **Godot's defaults:**
  - `FontSize` 32, `PixelSize` 0.005, `OutlineSize` 12, centred both ways.
  - Without a `Font`, the label uses the engine's Lato Latin, as G11 planned. This is unlike `Label2D`, which draws
    nothing without one.
  - `Text` is shown through `Atr`.
- **`StandardMaterial3D.NoDepthTest`** (Godot's `no_depth_test`) joins `MaterialRenderState` and `PipelineKey`.
  - The surface draws in the transparent pass, by priority and then back to front, with depth test and writes off.
  - The object-ID pipeline keeps its depth test, so picking still finds what is in front.
- `VerticalAlignment` (Top, Center, Bottom) is a new public enum next to `HorizontalAlignment`.

## Consequences

- No new shaders or pipelines beyond the `NoDepthTest` variants. Each label is one draw per atlas page per pass, which
  is fine for dozens of tags.
- Not in this slice: G11's batched `Text3DRenderer`, multiple lines and autowrap, `FixedSize`, fog, alpha cut, fading,
  fallback fonts, `IsPositionInFrustum`/`GetFrustumPlanes`, `UiMarker3D` and `UiPanel3D`. A new glyph still re-uploads
  the whole atlas page (`Texture2D.SetPixels`).
- Godot's `render_priority` for the text has no equivalent yet, because `VisualInstance3D.RenderPriority` already means
  node order.
