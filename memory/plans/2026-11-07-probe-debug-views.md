# Plan: Probe debug views and an indirect-light view (G8e.1 rest)

**Status:** done, see [log](../logs/2026-11-07-probe-debug-views.md)
**Docs:** [Lighting → light probes](../../docs/design/lighting.md), [ADR 0170](../decisions/0170-light-probe-volume.md)
("Not built (deferred): the probe debug view …, an 'indirect only' view mode"), the G8e proposal's G8e.1 Editor paragraph
**Started:** 2026-11-07

## Goal

A person can see what the probe bake did: an "indirect light" view shows only the ambient term (sky, probes, bounce,
AO) with the direct lights off, in the game (`MAINFRAME_DEBUG_VIEW=indirect`) and in the editor's viewport, and the
editor draws a volume's probes coloured by their baked light so leaks and dark pockets show.

## Not in scope

- `rendering.probes` quality levels, rotated or blended volumes, SH L2 sky irradiance (ADR 0170 keeps them deferred).
- Invalid probes in red: the bake dilates them away and stores no validity.

## Steps

- [x] `RenderDebugView.IndirectLight`: the lights UBO written with no direct lights for that view (`LightEnvironment.WriteUbo`
      `directLights`), env value `indirect`; `SubViewport.DebugView` so offscreen views (the editor's) take every debug
      view. Verified by: a render test (`probes` scene, indirect view: golden on both drivers; sunlit ground darker than
      the normal frame, shade unchanged within tolerance) and a unit test of the UBO counts.
- [x] Editor: View menu Indirect Light / Ambient Occlusion / Motion Vectors (one at a time) and Show Light Probes; the
      probe overlay (`LightProbeOverlay`: probes within a radius of the camera as small crosses, colour = baked sky
      visibility × the sky's colour + bounce, capped count, 0 B per frame). Verified by: editor tests (menu toggles, the
      viewport's `DebugView`, overlay probe selection and colours on a baked test volume) and a screenshot.
- [x] Docs: lighting.md, post-processing.md (debug views), editor.md, ADR 0275, log with State changes, README +
      landing line with the screenshot. Verified by: check.sh, state-guard.

## Done when

- Gates pass; `MAINFRAME_DEBUG_VIEW=indirect` and the editor's View › Indirect Light show the ambient term only; View ›
  Show Light Probes draws the probes of a baked volume near the camera.

## Open questions

- None.
