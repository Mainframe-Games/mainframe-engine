# ADR 0132 — Material overlays, next passes and the outline material

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Driving Range Simulator (Godot 4.7) — PM3 / E2 (the game's `docs/porting.md`)

## Context

Driving Range highlights what the player looks at by setting `GeometryInstance3D.material_overlay` on its meshes.
The overlay is a blended cyan `StandardMaterial3D` (alpha 0.13, faint emission). Its `next_pass` is a `ShaderMaterial`
on the usual inverted-hull outline shader:

- `cull_front, unshaded`;
- the vertex is pushed along `normalize(clip_normal.xy)` by `outline_width` (7) pixels;
- the colour is white.

The engine had neither overlays nor next passes, and no custom 3D shaders: every mesh pipeline used `Mesh.vk.vert`
and `Mesh.vk.frag`, with depth compare `Less`.

## Decisions

1. **`GeometryInstance3D.MaterialOverlay`** and **`Material.NextPass`**: both are `[Export]` resource slots, so the
   generator serialises them with no extra work.
   - The renderer resolves each node's **extra passes** whenever the materials are resolved:
     - each surface material's next-pass chain;
     - then the overlay chain, over every surface.
     - Chains stop at `Material.MaxPassChain` (8), which also bounds a cycle.
   - Any `NextPass` change bumps a global `Material.ChainGeneration`, and nodes compare against it. Chains change
     rarely, so one counter is cheaper than tracking each material.
2. **Extra passes go into the existing lists.** Each one is a `MeshDrawItem` (flagged `Extra`) in the opaque or
   transparent list, chosen by its own material's state:
   - The pipeline key gains `ExtraPass`, which sets depth compare to **less-or-equal**: an overlay must land exactly
     on the surface drawn before it. Godot's reverse-Z greater-or-equal does the same.
   - Extra passes cast no shadow, and the object-ID pass skips them (outlines are not pickable).
   - The resolve allocates only when something changes, never per frame.
3. **`OutlineMaterial3D`** (`Color`, `Width` in pixels) is a built-in material, because the engine has no custom 3D
   shaders:
   - **Shader set `MeshOutline`:** a new `Mesh/MeshOutline.vk.vert` (Godot's formula, with the model-view 3×3) plus the
     existing `Mesh.vk.frag` on its unshaded path.
   - **Width:** travels in the material UBO's spare `flags.w`. Set 2 binding 0 is now visible to the vertex stage.
   - **Render state:** front-culled and blended, like the Godot shader, which writes `ALPHA` and is therefore
     transparent; no depth write.
   - **Pipeline cache:** `MaterialGpu` now caches 12 pipelines, one for each combination of shader set (3), extra pass
     (2) and mirrored (2).

## Consequences

- godot2mf maps the outline `ShaderMaterial` to `OutlineMaterial3D`, and the hover highlight converts one to one.
- **Tests:**
  - Render test `OverlaysAndOutlinesDrawOverTheirSurfacesOnlyAndMatchGolden`, scene `outline`, golden
    `outline_frame0020`.
  - `MaterialAndPipelineTests.OutlinesAreFrontCulledBlendedAndNextPassesBumpTheChainGeneration`.
  - `MeshSerializationTests.OverlaysNextPassesAndOutlinesRoundTrip`.
- Existing draws keep their pipelines and goldens, because extra passes have their own keys.
- **Known issues:**
  - Hard-edged meshes show gaps at corners (inherent to the technique; Godot's shader has them too).
  - Outlines aren't drawn in sub-viewport ID passes or in shadows.
  - `ShaderMaterial` in 3D still draws the default material.
