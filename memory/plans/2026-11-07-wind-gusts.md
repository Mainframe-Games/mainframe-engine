# Plan: Gusts on the simple wind (G8e.5 rest)

**Status:** done, see [log](../logs/2026-11-07-wind-gusts.md)
**Docs:** [Materials & meshes → Wind](../../docs/design/materials-and-meshes.md), [ADR 0172](../decisions/0172-ez-tree-foliage-quality.md),
the G8e proposal's "Gusts" bullet
**Started:** 2026-11-07

## Goal

Grass (and any foliage on the simple wind) can gust with the same rolling field as the hierarchical trees, opt-in per
world; the Forest turns it on.

## Not in scope

- `--bake-tree-clusters`, Godot's visibility-range fade margins on `GeometryInstance3D`, impostors on `Tree3D`
  (listed in ADR 0278 and the G8e changes).

## Steps

- [x] `windGust` in `wind.slang`, used by `windHierarchy` (unchanged result) and by `windOffset` under
      `windParams.w`; `WorldEnvironment.WindGusts`; the Forest at 0.4. Verified by: the existing wind and tree goldens
      unchanged (20 render tests).
- [x] Render test `GustsVaryTheSimpleWind` (`foliage-wind --count 2`) with goldens on both drivers.
- [x] Docs, ADR 0278, log. Verified by: check.sh, state-guard.

## Done when

- Gates pass; `foliage-wind` and the tree goldens unchanged; `foliage-gusts` differs from the steady wind.

## Open questions

- None.
