# ADR 0150 — PBR shading and sky image-based lighting

- **Date:** 2026-10-08
- **Status:** accepted
- **Milestone:** Gameplay toolkit G6.1/G6.2 (the forest slice's cut), for G8d
- **Spec:** docs/design/future/rendering-features.md (PBR, image-based lighting from the sky); docs/design/lighting.md,
  sky.md, materials-and-meshes.md (what was built)

## Context

The forest showcase (ADR 0149) needs physically based materials (bark, rocks, wet ground, water), ambient light that
follows the sky, and fog. The renderer had Blinn-Phong only (ADR 0014), a flat ambient colour, and no fog; the frame
data already carried the fog parameters (29a8ae2). G6 plans the full PBR/IBL feature set with a serialization
migration that flips the default to PBR; the slice needs the shading and the lighting now, without changing how
existing scenes look.

## Decision

- **PBR is a shading mode, opt-in.** `ShadingMode.Pbr` on `StandardMaterial3D`, with Godot's `Metallic` (0), `Roughness`
  (1) and an `AmbientOcclusion` (1) value, and one linear `OrmTexture` (R occlusion, G roughness, B metallic) that
  multiplies them. Blinn-Phong stays the default, so scenes and goldens are unchanged; the G6.2 default flip and its
  v1 → v2 migration come later.
- **Shading model.** Cook-Torrance: GGX distribution, Smith height-correlated visibility, Schlick Fresnel with
  F0 = lerp(0.04, albedo, metallic); Lambert diffuse × (1 − F)(1 − metallic); perceptual roughness clamped to ≥ 0.045.
  Lambert's 1/π stays folded into the light (the Blinn-Phong units), so the specular lobe carries a π. The light loops,
  shadow lookups, shadow opacity and cascade tint are the Blinn-Phong ones. `shadeLightsPbr(PbrSurface, Ngeo, worldPos)`
  in `lights.slang` is the entry point every later lit 3D shader calls.
- **Image-based lighting from the sky (G6.2-lite).** `SkyRadiance` renders the sky's own fragment shader into a 128²
  RGBA16F cube (a 90° camera per face through the frame data and the sky push constants), so every sky type — and the
  physical sky the forest adds — lights the scene the way it looks, with no per-type code. Fragment passes (no compute)
  prefilter it with GGX importance sampling into 6 radiance mips and convolve a 32² irradiance cube (a cube rather than
  9 SH coefficients: no read-back, no compute, and it keeps the sky's directional detail). The split-sum BRDF LUT is
  computed on the CPU once per process (unit-testable, nothing to port to other backends).
- **Re-capture on change.** A bake runs when the sky instance or its push constants change; parameter changes re-bake
  at most every 4 frames. The capture leaves the sun disk out (`sun.z = 1`): directional lights light with the sun, and
  a sub-texel disk flickers as it moves.
- **Binding: set 0.** The radiance cube, irradiance cube and BRDF LUT are bindings 2–4 of the per-frame set
  (`FrameContext`), per frame slot and view, so each world's views bind their own sky. As G6 planned, new per-view
  textures go into set 0 and set 3 stays free. The fragment stage uses 13 images and 10 samplers (≤ 16), 3 sets (≤ 4).
  Flags in the lights UBO's spare components (`ambientColor.w` = ambient energy, `cameraPosition.w` = environment
  flags) tell the shaders what to use; `FrameData` is unchanged.
- **`WorldEnvironment`** gains Godot's `AmbientSource` (`Color` default | `Sky`; applies to Blinn-Phong too),
  `AmbientEnergy` and `ReflectedLightSource` (`Background` default | `Disabled` | `Sky`). Without a captured sky the
  helpers fall back to the ambient colour (a uniform environment for reflections).
- **Fog** (`fog.slang`): exponential distance fog with a height falloff (uniform below `FogHeight`, thinning above), the
  optical depth integrated exactly along the view ray, and Godot's sun scatter towards the first directional light.
  The sky gets only the height part — the ray to infinite height — so the horizon fades into the fog and uniform fog
  leaves the sky clear. The capture is unfogged.

## Consequences

- Every pipeline using set 0 now has three more bindings (fragment only); Blinn-Phong's ambient goes through
  `iblDiffuse`, so Spine and meshes follow `AmbientSource` too. Defaults render exactly as before.
- Worlds with a sky bake once at startup (48 small passes) even when no material is PBR, because reflections default to
  the sky; a moving sun costs a bake every 4 frames.
- Material UBO 80 → 96 bytes; material set binding 5 (ORM).
- Not yet: glTF metallic/roughness/occlusion import, `MetallicSpecular`, per-channel texture selection, `AoLightAffect`,
  energy compensation, HDR panoramas, the radiance size/process-mode settings, one bake per shared `Sky`.
