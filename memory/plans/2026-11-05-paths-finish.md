# Plan: finish G13 (platforms, 3D curve tool, closest-point grid, rails)

**Status:** done (written while building: the steps follow the accepted proposal's G13.1, G13.5, G13.6 and G13.8)
**Docs:** [Paths](../../docs/design/paths.md) (the proposal's content moved there), [ADR 0242](../decisions/0242-path2d-and-the-curve-tool.md),
[ADR 0243](../decisions/0243-moving-platforms-and-the-3d-curve-tool.md), [Physics](../../docs/design/physics.md)
**Started:** 2026-11-05

## Goal

The rest of G13: `AnimatableBody3D`/`2D` and character platform riding (`PlatformOnLeave`, `PlatformFloorLayers`,
`GetPlatformVelocity`); kinematic bodies reporting their real velocity; the closest-point grid and hinted query; the
curve tool in 3D views (tilt ring, ground snap, Fit to Ground, `CurveTerrainFit` shared with `River3D`); the Forest's
fly-over and benchmark on `Curve3D`; a `paths_3d` Demo scene; the proposal closed.

## Not in scope

- `IEditorViewportTool` (G8a.2) and `River3DTool` (G8c.4).
- Re-recording `baseline.json` or the Forest's benchmark baseline (quiet machine only).

## Steps

- [x] Platforms in both spaces. Verified by: `MovingPlatformTests` (lift and sideways within 1 cm, turntable radius
  within 1 cm over 10 s, leave modes, layers, a pushed box, kinematic velocity), 474 physics tests unchanged.
- [x] Closest-point grid and hinted query. Verified by: `ClosestPointTests` (8 000 random queries equal a scan, hinted,
  0 B); `PathBenchmarks` (~100× faster than the scan).
- [x] 3D curve tool. Verified by: `CurveEditor3DTests` (11), the walkthrough's `26h` capture inspected; river tests.
- [x] Forest rails. Verified by: Forest `PauseMenuTests`/`ValleyTests` (steady speed, loop).
- [x] Demo `paths_3d`. Verified by: `Paths3DSceneTests`, the screenshot inspected.
- [x] Docs and proposal closed. Verified by: links grep (no `future/paths.md` left), `check.sh`.

## Done when

- All of the above tests pass, and G13's row in `docs/milestones.md` is ✅.
