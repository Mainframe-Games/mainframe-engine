# ADR 0275 — The indirect-light view drops the lights from the UBO; probes show as editor line crosses

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G8e (G8e.1 rest)
- **Docs:** docs/design/lighting.md#debugging-the-probes, docs/design/post-processing.md, docs/design/editor.md,
  memory/plans/2026-11-07-probe-debug-views.md

## Context

ADR 0170 shipped the baked `LightProbeVolume` and deferred its two inspection tools: a probe debug view ("spheres showing
the SH, invalid probes red") and an "indirect only" view mode. Without them a person cannot tell a probe leak from a
lighting choice. `RenderDebugView` (ADR 0163/0165) already replaces the main view's image with the motion vectors or the
AO, but only in the game's main window: every `SubViewport`, the editor's scene views included, was handed
`RenderDebugView.None`.

## Decision

- **`RenderDebugView.IndirectLight` writes the view's lights UBO without lights** (`LightEnvironment.WriteUbo`
  `directLights: false`: counts 0, every slot zeroed, the ambient header kept). Every lit shader (meshes, terrain,
  foliage and its translucency, impostors, water, decals) loops over the counts, so all of them show the ambient term
  alone (sky light, the probes' sky visibility and bounce, AO) with no shader or `FrameData` change and no new
  permutation. Emission, fog and post-processing stay; the shadow maps still render (unused). `MAINFRAME_DEBUG_VIEW=indirect`.
- **`SubViewport.DebugView`** (runtime, not saved): each view takes its own debug view, so the editor's View menu sets
  Indirect Light Only, Ambient Occlusion and Motion Vectors on the scene's view (one at a time; the active one again turns
  it off). The latter two need the view's post-processing (Preview Post-Processing), as in the game.
- **The probe overlay is editor line crosses** (`LightProbeOverlay`, View › Show Light Probes): the probes of every baked
  volume within 24 m of the camera (8 192 at most, nearest columns first) as three short lines each in the view's
  depth-tested debug lines; brightness = the baked sky visibility toward up (cosine-weighted), tint = the bounce toward
  up × the volume's energy. Read from the CPU coefficients, 0 B per frame.

## Ruled out

- **A `FrameData` flag read by `shadeLights*`:** a shader change in every lit include and a `.spv` refresh for what the
  light counts already do; foliage translucency and water read the lights directly and would need their own checks.
- **SH spheres rendered as meshes:** a sphere per probe (133 000 in the Forest) needs a new instanced pipeline and a GPU
  buffer of coefficients; crosses near the camera show leaks just as well in the places one looks.
- **Invalid probes in red:** the bake dilates invalid probes from their neighbours and stores no validity, so there is
  nothing to colour; a leak shows as a bright cross where the sky is hidden.

## Consequences

- G8e.1's acceptance check "no leaks in the probe debug view" can be made by eye in the editor.
- `rendering.probes` quality levels, rotated or blended volumes and the SH L2 sky stay deferred (ADR 0170).

## Revisit when

- A game needs to see probes from far away or below ground: draw them as instanced spheres from the probe texture.
