---
summary: >-
  The editor's view of the Neon flickered: two causes, both animated by the view's TAA jitter. Neon's facade (and crowd,
  screen, sign) shaders interpolated INSTANCE_CUSTOM, so hashed per-window values flipped per pixel row; the varyings are
  now flat. And debug lines/grid drawn after post (unjittered) were depth-tested against the jittered scene depth, so lines
  on a surface came and went; they now write a depth pulled towards the camera by 4 pixels' footprint.
---

# 2026-11-03: the editor's scene view flickers (Neon)

Branch `fix/editor-viewport-taa-flicker`. Small fix, no plan. The owner's recording (3.6 s, 99 frames) showed the Neon in
the editor from outside the home tower, the Apartment selected.

## What flickered

Frame-to-frame differences of the recording, with the camera still, were all in two places: the windows of the home
tower (whole windows switching between streaky patterns every frame) and the green selection wireframe. Reproduced with an
editor QA script (`--qa-script`, the Apartment selected, `camera 0 0 0 40 180 -12`, consecutive `capture`s): mean
difference ~1.0 per frame, 1.7 % of view pixels changing by more than 16 levels. With View › Post-Processing off: 0.001,
nothing changing, but the window streaks were there, still. So the content was wrong, and TAA's per-frame jitter (the
editor previews the project's TAA, ADR 0169) made it move.

Ruled out on the way: view-slot mapping of sub-viewports between frames (stable: the scene view is view 1, the Hologram
view 3), TAA history validity (valid every frame), geometry z-fighting (one solid colour per instance: no streaks),
`TIME` tearing (uniform within a frame), blinds slats and the glass reflection (each alone smooth).

## Cause 1: interpolated per-instance values (Neon)

`facade.gdshader` passed INSTANCE_CUSTOM to `fragment()` through `varying vec4 v_custom` (and the model scale through
`v_scale`). Interpolating three equal values gives values a few ulps apart from pixel to pixel; the shader hashes them
(`seed = v_custom.x * 173`) and floors/steps the hashes (room widths `2 + floor(hash * 3)`, lit rooms, televisions), so
a whole room flipped along rasterised rows: the streaks. Jitter moves the samples every frame. `varying flat` fixes it:
the QA run's difference fell to 0.06 mean, 0 pixels over 16, and the rooms render whole. `crowd`, `screen` and `sign`
had the same pattern (hashes, `floor`, `neon()`'s `floor(fract(h) * 6)` of interpolated INSTANCE_CUSTOM): flat too.
`Neon.Tests` `ShaderTests.InstanceCustomDataReachesTheFragmentFlat` fails on the old facade.

The game shows the same in principle; its camera rarely sees a facade close enough for the rooms (`fine`) to matter.

## Cause 2: after-post lines against a jittered depth (engine)

ADR 0169 draws a post-processed view's grid and debug lines after the effects with the camera unjittered, against the
scene depth, which the prepass rendered jittered (≤ half a pixel). A line lying on a surface (the Apartment's selection
wireframe, the grid on a floor) is at that surface's depth, so per pixel it passed or failed the LESS_OR_EQUAL test
depending on the jitter. `include/frame.slang` `afterPostLineDepth`: the after-post variants of `DebugLines` and
`SceneGrid` (specialisation constant 0) write `SV_DepthLessEqual` = the depth of the line pulled towards the camera by
4 pixels' footprint (perspective: view depth × (1 − 4 · 2 / (P₁₁ · height)); orthographic: linear), enough for surfaces
seen at down to ~7°. Inside the loft the QA difference over the grid and wireframe fell from 0.0008 to 0.0002 of pixels
over 16; what remains is wireframe edges along silhouettes, where the jitter moves the occluding edge itself.
`subviewport-post` (count 2, TAA) now draws a patch of blue debug lines on the floor and checks it is exactly blue and
identical in two frames: with the old shaders it is blue in one frame and the floor in the next.

## Tooling notes

- slangc 2026.1's Linux x86_64 release segfaults in `obfuscateModuleLocs` (`SourceMap::getSourceFileIndex`) when
  `-obfuscate` meets the `#line` directives the Godot-shader translator writes; deterministic for many inputs on this
  machine, some in a Debian container too. The Neon SPIR-V here was built through a `slangc` wrapper that drops `#line`
  from the temporary source (`-obfuscate` drops locations anyway); rebuilding the unchanged `crowd`/`facade` that way gave
  identical vertex SPIR-V and fragment SPIR-V differing only in equivalent comparisons (`!(x > 0)` vs unordered `<= 0`,
  the committed ones came from slangc 2026.1-52).
- Engine shaders: only `DebugLines.vk.frag.spv` and `SceneGrid.vk.frag.spv` were rebuilt (slangc 2026.1); the other
  `.spv` stay the 2026.1-52 builds (the new `frame.slang` function is unused there), and `shaders.lock` was rewritten,
  so its version line now reads 2026.1.

## Verified

- Editor QA, the recording's view (outside the home tower, the Apartment selected, TAA preview on), with the committed
  SPIR-V: frame-to-frame mean difference 1.0 → 0.06, pixels changing by > 16 levels 1.7 % → 0–0.01 %.
- `just build`, Release `-warnaserror -p:CompileShaders=false`: 0 warnings (Debug: the local "slangc not found" note).
  `just test`: 3193 + 1805 + 460 pass. `just format-check`, `just shaders-check` (with slangc 2026.1 on PATH), `check.sh`:
  ok. `Neon.Tests` `ShaderTests`: passes; fails on the old facade.
- `just test-render` on an RTX 3070 Ti (proprietary driver, no goldens of its own: every other check ran): 222 pass,
  validation-clean, allocation gates 0 B. The new floor-lines check fails with the old line shaders.
- **Goldens not re-recorded.** Comparing old and new line shaders on this GPU, four frames change, all where lines meet
  surfaces (the grid now shows over a floor at y = 0, selection edges at surfaces stay visible):
  `subviewport-post_frame0008`, `editor_frame0051`, `editor-viewport-tools_frame0040`, `editor-filesystem_frame0040`.
  `lavapipe`: three exceeded CI's tolerance (`editor_frame0051` stayed within it); their frames come from PR #157's CI
  run (`render-tests-*` artifacts), inspected: the grid's axes and the selection outlines are whole where they lie on the
  floor, nothing else moved. `moltenvk` still needs `just golden-update` on the Mac. `just render-tests-linux` does not
  run on a Linux host (`build/linux/run.sh` uses bsdtar's `--no-mac-metadata`/`-n` and `docker build -f … -`, which the
  legacy builder rejects).
