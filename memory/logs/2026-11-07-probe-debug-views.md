---
summary: >-
  G8e.1 rest: RenderDebugView.IndirectLight (the view's lights UBO written without lights: the ambient term alone,
  MAINFRAME_DEBUG_VIEW=indirect), SubViewport.DebugView so editor views take every debug view, the editor's View menu
  debug views and Show Light Probes (baked probes near the camera as crosses by sky visibility and bounce); ADR 0275.
---

# 2026-11-07: Probe debug views (G8e.1 rest)

Branch `feature/probe-debug-views`, cut from `origin/main` (1271884f). Plan: [Probe debug views](../plans/2026-11-07-probe-debug-views.md).
As built: [Lighting → Debugging the probes](../../docs/design/lighting.md#debugging-the-probes).

## What changed

- **Engine:** `RenderDebugView.IndirectLight`; `LightEnvironment.WriteUbo(…, directLights)`, `FrameContext.Begin`/
  `WriteLights` overloads with it; `RenderServer` writes the main view's lights without them under that view, and each
  sub-viewport's by its own `SubViewport.DebugView` (new, runtime only; also passed to its post settings, so velocity
  and AO views work in sub-viewports); `MAINFRAME_DEBUG_VIEW=indirect`.
- **Editor:** View menu Indirect Light Only (`view.debug.indirect`), Ambient Occlusion (`view.debug.ao`), Motion Vectors
  (`view.debug.velocity`), one at a time, and Show Light Probes (`view.probes`); `ViewportController.DebugView`,
  `ProbesVisible`, `ProbesDrawn`; `Viewport/LightProbeOverlay.cs`.
- **Tests:** `LightEnvironmentTests.WithoutDirectLightsOnlyTheAmbientHeaderIsWritten`; `ProbeDebugViewTests` (3,
  editor); the `probes` render scene's `--count 5` (sun + probes through the indirect view: the shed matches the no-sun
  prediction, the red bounce stays) as `TheIndirectLightViewDropsTheSunAndKeepsTheBounce` with goldens
  `probes-indirect_frame0010.png` on moltenvk and lavapipe.
- **Docs:** lighting.md (Debugging the probes, Known issues), post-processing.md (Debug view), editor.md (View menu),
  README (a screenshot row, the lighting line), the landing page's Showcase; `docs/images/editor-probes.png`.

## Decisions

- [ADR 0275](../decisions/0275-indirect-light-view-and-probe-overlay.md): no shader change (the light counts do it), a
  debug view per sub-viewport, crosses near the camera instead of SH spheres, no invalid-probe colour (not stored).

## Dead ends

- Recording the new golden with `UPDATE_GOLDENS=1` overwrote `probes_frame0010.png`: a capture is named by its scene,
  so a second golden of one scene needs `goldenFile:` (recorded from `new-goldens/` instead; the overwrite was reverted).
- Writing this phase in a second git worktree was refused by the session's worktree hook; the work moved back here.

## Verified

- The indirect golden inspected beside the normal one: no sun or sun shadows, sky-blue ambient, the shed dark, the red
  wall's bounce on the floor.
- Full gates: see the PR.

## Not done

- `rendering.probes` quality levels, rotated or blended volumes, SH L2 sky (ADR 0170's deferred list).

## State changes

- milestones G8e: in the status cell, after "G8e.1 probe GI ([ADR 0170](../decisions/0170-light-probe-volume.md))",
  add "with the indirect-light view and the editor's probe overlay ([ADR 0275](../decisions/0275-indirect-light-view-and-probe-overlay.md))".
