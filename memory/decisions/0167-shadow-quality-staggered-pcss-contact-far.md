# ADR 0167 — Shadow quality: staggered cascades, coarse casters, PCSS, contact shadows and a far shadow

- **Date:** 2026-10-09
- **Status:** accepted (implemented on `forest/sh`, forest slice wave 5)
- **Milestone:** Gameplay toolkit G8e.2 (ADR 0164's plan)
- **Spec:** docs/design/future/forest-visual-quality.md#g8e2-shadow-quality; current state in
  docs/design/shadow-system.md#shadow-quality-g8e2

## Context

The Forest affords 2 × 1024² sun cascades to 60 m: alpha-tested leaf cards make every cascade texel under the canopy
run the cut-out shader, so the cost is cascades × resolution² × overdraw, every frame (the engine default, 4 × 2048² to
100 m, costs about 12 ms there). The filter is a fixed-radius Poisson disc, so leaf shadows 20 m down are as sharp as a
trunk's; past 60 m nothing casts, and trees past level 1 never cast. Small things (pebbles, ferns, the gap under a log)
never ground themselves. ADR 0164 approved G8e.2: cached cascades, coarse far casters, PCSS, contact shadows and a static
far shadow, within the binding budget and opt-in so existing scenes and goldens stay put.

## Decision

- **Staggered cascades** (`DirectionalLight3D.ShadowCacheMode = Staggered`; Godot has no equivalent). Cascade 0 every
  frame, cascade 1 on even frames, cascades 2 and 3 on alternate odd frames: at most two cascade passes a frame. A
  cascade between renders keeps its layer, matrix, depth range and "had casters" (`ShadowPlanner`'s cache). Its sphere
  is grown by a **constant** margin (`ShadowCacheSchedule.Margin`: camera travel at `CacheMaxSpeed` 10 m/s and turn at
  `CacheMaxTurnRate` 60°/s over its interval at 60 Hz; turning swings a far slice's sphere by its distance ahead), so its
  texel grid never moves between renders; it re-renders early when the current slice leaves the rendered sphere, and
  all re-render when the light turns, the settings change or `ShadowSystem.InvalidateShadowCache()` is called.
  Cascade layers get per-layer barriers so cached layers are never discarded. The turn term was added after the first
  Forest benchmark: translation alone re-rendered the far cascades on every turn (2.7 cascade passes a frame instead of 2).
- **Coarse casters** (`GeometryInstance3D.ShadowCasterLod` `All`/`Fine`/`Coarse`, `DirectionalLight3D.ShadowCoarseCascades`,
  `TreeScatter.ShadowCoarseLod`). The light's last N cascades and the far shadow are coarse passes: `Fine` instances skip
  them, `Coarse` ones cast into them from any distance (their visibility range ignored) and into the fine passes while in
  range, so a tree is never drawn twice and near cascades still get far trees' long shadows. This is the hook G8e.5's
  impostor casters use.
- **PCSS** (`ShadowFilter.Pcss`, `High`'s filter and the shadow system's default; `DirectionalLight3D.LightAngularDistance`,
  Godot's name, default 0 = today's Poisson 16). On the primary light's cascades: a blocker search of five 2 × 2 gathers
  through a **point sampler**, the penumbra `(d_r − d_b) · depthRange · tan(angle / 2)` in texels, the Poisson disc at
  that radius clamped to [`FilterRadius`, 8 texels]; no blocker → lit, all blockers → umbra (both skip the filter). The
  cascade array became a **separate image** with an immutable comparison sampler (b4) and point sampler (b5): +1
  sampler, no image. With TAA (a jittered main view) the pattern rotates per pixel and frame and drops to 12 filter taps;
  without, it stays fixed (ADR 0072). The first version (16 point taps, no early-outs) cost about 3 ms in the Forest.
- **Contact shadows** (`DirectionalLight3D.ContactShadows`, `ContactShadowLength`; `ShadowQualitySettings.ContactShadows`,
  `High` only). An `AfterPrepass` effect (`PostEffectOrder.ContactShadows`, after SSAO) marches 12 steps towards the sun
  in the prepass depth at full resolution and writes `R16G16_SFLOAT` (shadow, view depth) to **set 0, binding 6**; the
  lit shaders multiply the primary light's cascaded shadow by it on the surface the prepass drew (the depth check keeps
  water and transparent surfaces out). Noise only with TAA. **A separate target, not SSAO's:** lane S owns binding 5 and
  runs in parallel; G8e.1 folds this into the AO target's G channel (as the proposal plans) and retires binding 6.
- **Far shadow** (`FarShadowEnabled`, `FarShadowDistance`, 0 = every caster's bounds). An extra **layer of the cascade
  array** (not an atlas tile: the atlas clears and redraws all its tiles in one pass whenever any tile renders, and the
  array layer gets the cascades' sampler and binding for free), orthographic along the sun over the box, drawing the
  coarse pass's casters, rendered once and again only when the sun turns by more than 0.1°, the box outgrows the rendered
  one or the cache is invalidated. `sampleCascades` blends into it past the last cascade instead of fading to lit.
- **Binding budget:** the lit fragment stage declares 15 images and 13 samplers (terrain splat 16 and 14) of 16, so the
  sky irradiance cube's move to SH L2 is **not needed yet**; G8e.1 needs it (or the binding-6 merge) for its probes.
- **The Forest** opts in to everything: 4 cascades to 140 m, staggered, the last two coarse with the trees' level 1,
  0.5° sun, contact shadows at 0.4 m, the far shadow. **At 1024², not the proposal's 2048²:** 2048² cascades cost 4–6 ms
  more on this M5 (two 4 M-texel leaf-card passes a frame), which waits for impostors (G8e.5). Level 1, not level 2,
  is the coarse level: Ez Tree's level 2 casts nearly opaque canopy shadows (the floor went black under the low sun).

## Consequences

- `just forest-bench` at 1920 × 1080 (Apple M5, busy machine, interleaved runs): p50 15.4 → 14.1–15.4 ms, p99
  24.2–25.7 → 23.9–28.8 ms, shadow pass 2.1 → 3.7–4.0 ms GPU. The depth prepass the contact shadows turn on pays for
  PCSS; the frame stays within noise of before (docs/design/forest.md#performance). Every cascade every frame would cost
  15 ms of shadows.
- Defaults unchanged: every existing golden matches; `ShadowFilter.Pcss` without an angular size is the Poisson path.
- `ShadowUniforms` grows to 1840 B (appended: far map, far params, cascade depth ranges, PCSS, contact); `MaxShadowPasses`
  40; the shadow set layout has six bindings (the fallback set too).
- Known limits: PCSS reads depths with the raster slope bias (narrower penumbra at a caster's steep faces) and shows
  soft steps without TAA; secondary suns have no PCSS; the far shadow has the cascade resolution (no
  `FarShadowResolution` yet) and sees static casters as they were (`InvalidateShadowCache()` after edits); moving casters
  lag up to 4 frames in cached cascades.
