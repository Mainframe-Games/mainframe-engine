# ADR 0095 — Shadow quality levels (project setting → Shadows v2)

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 (integration of lane m10b with M4)

## Context

`project.mfproj` has `rendering.shadows`: `Off`, `Low`, `Medium`, `High` (ADR 0090). Lane m10b only mapped it to an
atlas size (2048/4096/8192) because M4 had not landed. With Shadows v2 the cost of shadows is spread over several
knobs: the spot/secondary-directional atlas (`MaxAtlasSize`), the PCF kernel (`Filter`, `FilterRadius`), the number
of sun cascades and each map's resolution (per light, `Light.ShadowResolution`, authored in scenes). A quality level
must scale GPU time and memory on weaker hardware without editing scenes, and `High` must not change how existing
scenes (Sandbox, render-test goldens) look.

## Decision

- `ShadowQualitySettings(MaxAtlasSize, Filter, FilterRadius, CascadeLimit, ResolutionLimit)` and
  `ShadowQualitySettings.For(level)`:

  | Level | Atlas ≤ | Filter | Cascades ≤ | Any map ≤ |
  |---|---|---|---|---|
  | `High` | 4096 | Poisson 16, r 1.5 | 4 | 8192 (no cap: the lights' own resolutions) |
  | `Medium` | 2048 | 3×3 PCF, r 1.5 | 3 | 2048 |
  | `Low` | 1024 | hard (one bilinear comparison tap), r 1 | 2 | 1024 |
  | `Off` | no shadow system (`RenderServer.ShadowsEnabled = false`) | | | |

- Limits **cap** per-light settings rather than replace them: `ShadowPlanner.CascadeLimit` caps
  `DirectionalLight.CascadeCount`, `ShadowPlanner.ResolutionLimit` caps every cascade layer, atlas tile and cube face.
  A scene authored with small maps stays small at `High`.
- `High` equals a new `ShadowSystem`'s defaults (a unit test pins it), so the Sandbox and every golden are unchanged.
- `RenderServer.ShadowQuality` owns the level: it applies the settings to the shadow system now or when it is created,
  and toggles `ShadowsEnabled` for `Off` (which, like `ShadowsEnabled`, must be set before visuals exist).
  `GameHost.OnLoad` sets it from the project. Settings changed on `ShadowSystem` afterwards stay until the next change.

## Consequences

- Low/Medium/High switch at run time (e.g. a graphics menu); `Off` ↔ on needs a restart (or no visuals yet).
- The cascade array image is still allocated with 4 layers (memory scales with resolution, not cascade count).
- `RenderingProjectSettings.ShadowAtlasSize` (lane m10b) is removed; `ShadowQuality` moved to
  `Rendering/Shadows/ShadowQuality.cs`.
