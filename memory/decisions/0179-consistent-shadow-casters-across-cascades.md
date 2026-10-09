# ADR 0179 — Consistent shadow casters across cascades: the coarse level's hand-off, no coarse cascades in the Forest

- **Date:** 2026-10-10
- **Status:** accepted
- **Milestone:** Gameplay toolkit G8 (G8e, a fix after Brogan's playtest of ADR 0178's look)
- **Spec:** docs/design/shadow-system.md → Coarse casters; docs/design/forest.md → Sun

## Context

Brogan, playing the Forest: "I can see a shadow line cut off. Maybe need longer shadow drawing distance?" Three
observations, reproduced at 1920 × 1080 with the cascades tinted and each cascade (and the far shadow) forced over the
whole view in turn (a temporary debug mode):

1. **The line.** The canopy's shadow on the forest floor ended a few steps ahead of the player, at the split between
   cascade 1 and cascade 2 (16 m: 4 cascades to 140 m, λ 0.8). The Forest's last two cascades were coarse
   (`ShadowCoarseCascades` 2, ADR 0167): they draw no tree level 0 or 1, only each tree's coarse level, which since
   ADR 0172 is the impostor at half density (`TreeImpostorShadowDensity` 0.5). So past 16 m every tree, even the one next
   to the path, cast a thin half shadow, and the floor brightened along a line that moved with the camera. Forcing cascade
   1, then cascade 2, over the same view showed the near shore of R4 shadowed by the aspens in one and lit in the other.
2. **R4's near shore flipped with `ShadowMaxDistance`.** The same cause: at 140 m the shore (13–17 m away) lay in
   cascade 2, at 250 m in cascade 1. Its luminance read 52.6 at 140 m and 35.8 at 250 m.
3. **Missing tree shadows in the mid-ground.** With `ShadowMaxLod` 1 the mesh levels cast only to 55 m, and the impostor
   cast into the fine cascades only where it is drawn, from 85 m: the trees 55–85 m away cast nothing there. The coarse
   cascades had hidden that gap.

Also checked: the long straight diagonal edge across R4's far meadow is not a shadow-map edge. It is the stream channel's
bank turning away from the 25° sun: it stays with the sun's shadows off (`--set Sun.CastsShadows=false`), with the far
shadow off, and at 250 m. In the start view the trees in the sunlit meadow on the right cast towards the camera (the sun
is ahead and to the right), onto the meadow hidden behind the path's embankment: correct for the sun direction.

## Decision

- **The hand-off (engine).** A tree's coarse shadow level (`TreeScatter.ShadowCoarseLod`) casts into the fine passes from
  where its finer casting levels end (`TreeScatter.ShadowHandOff`: the end of level `ShadowMaxLod`'s range), not from where
  it is drawn, so each tree casts exactly one level at every distance. The Forest's impostors cast from 55 m.
  - Per instance: `FoliageMaterial3D` / `ImpostorMaterial3D.InstanceShadowBegin`, `InstanceShadowEnd` (−1 by default:
    the visibility range; 0 end: unbounded), packed + 1 into the material block's free `variation.zw` (0 = follow the
    visibility range) and read by `casterInstanceVisible` (`include/foliage_caster.slang`). No new binding, no layout
    change.
  - Per batch: `GeometryInstance3D.HasShadowRange` / `IsInShadowRange` (internal virtuals; `TreeScatterBatch3D` overrides
    them with its `ShadowBegin`, per tree or per chunk), which `MeshRenderer.Prepare` asks instead of the visibility range
    to put a caster into the fine passes (`MeshRenderer.CasterPasses`).
  - Coarse passes are unchanged: the coarse level casts for every tree there.
- **No coarse cascades in the Forest.** `ShadowCoarseCascades` 2 → 0: every cascade draws the same casters (mesh levels
  to 55 m, then impostors), and only the far shadow, sampled past 140 m, draws every tree as its impostor. Coarse cascades
  stay an engine option, documented as trading a visible split for cost when the coarse level differs from the fine ones.
- **Shadow distance stays 140 m.** 200 and 250 m cost 0.1–0.3 ms more and move every split out (cascade 0 to 12.8 m,
  cascade 1 to 28 m at 250 m: coarser near texels) without fixing anything; the far shadow covers the valley past 140 m
  with no visible seam in R1–R7, the start view or the walk.

## Consequences

- R4's near shore reads the same at 140 and 250 m (luminance 34.0 / 34.3 of the region; 52.6 / 35.8 before), and the
  canopy's shadow continues past the player's first 16 m instead of stopping at a line (the autowalk, R1). R7's straight
  dark streak through the shafts above the fall (a cascade edge in the volumetric fog) is gone.
- The mid-ground is darker than before: the aspens' real canopies shade the floor and the air where half-density
  impostors did. R1 gains shafts and floor shadows; R2's and R7's glade behind the fall lose most of their haze glow (the
  volumetric fog there is now in the canopy's shadow). `--set Valley.TreeClusterShadowDensity=0.7` brings flecks, shafts
  and some of the glow back (≈ 2 ms of shadows, ADR 0172): left for look-dev to decide. R1–R7 regenerated.
- Costs (`just forest-bench`, 1920 × 1080, TAAU 0.75, busy machine, two interleaved rounds; sun shadows GPU p50):
  ADR 0178's casters 5.15 / 4.96 ms; after, 140 m 5.54 / 5.77 ms; 200 m 6.12 / 5.72 ms; 250 m 5.94 / 5.62 ms. About
  +0.6 ms of shadows for the fix; the frame is within this machine's noise. 0 B per frame (`--autowalk`: 600 frames, 0 B).
- The impostor batches out of their draw range are now collected for the fine passes too (they already were for the
  coarse ones): the vertex shader collapses the trees nearer than the hand-off.
- `ShadowCoarseCascades` > 0 with a `TreeScatter` still shows its split; the hand-off does not help there (coarse
  cascades have no fine levels by design). Documented in shadow-system.md.
- The probe bake stays current (it hashes the sun's direction and radiance, not its shadow settings).

## Alternatives considered

- **A longer `ShadowMaxDistance`** (Brogan's suggestion): moves the splits further out (cascade 1 to 28 m at 250 m), so
  the line moved but stayed, and it costs more texels per metre in every cascade.
- **Impostor shadow density 1 in the coarse cascades**: a whole tree in one view is far denser than its canopy (ADR 0172
  chose 0.5 to keep R5's sunflecks), and the line stays: the impostor's shadow is not the meshes' shape.
- **Level 1 as the coarse level again**: the coarse cascades would draw every tree's level 1 to 140 m (ADR 0167's
  configuration before impostors), and the near trees' level 0 still differs from it.
- **`ShadowMaxLod` 2** (level 2 casts 55–85 m): its leaf cards cost more than the impostor quads and Ez Tree's level 2
  casts a nearly opaque canopy.
