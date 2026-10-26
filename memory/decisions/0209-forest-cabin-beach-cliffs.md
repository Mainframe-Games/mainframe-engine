# ADR 0209 — Forest: a log cabin, a beach and lake, edge cliffs and distant mountains

- **Date:** 2026-10-26
- **Status:** accepted
- **Milestone:** G8e (the Forest)
- **Docs:** docs/design/forest.md#the-edges-the-beach-and-the-mountains, docs/design/forest.md#the-log-cabin,
  docs/design/terrain.md (twelve splat layers)

## Context

Brogan asked for a log cabin in the Forest (a fireplace, a bed, table and chairs, light set dressing), a beach at one end
of the map, and the rest of the map enclosed by cliffs with mountains in the distance, so the player cannot walk off the
edge. The valley was a 256 m terrain closed by invisible walls with nothing beyond. Choices made with him: the beach on a
lake south of the pond (the map grows), the cabin at the glade's edge on a spur off the walking loop (the loop, the
benchmark spline and R1–R8 unchanged), its shell modelled in Blender, Poly Haven furniture, a fire crackle, a door that
opens, glass windows, a new reference shot (R9), forested rocky mountains, and sand as a real terrain layer (the splat
shader was capped at eight, all in use).

## Decision

- **Twelve splat layers** in the engine: a third RGBA8 weight map (`splat-2.png`, layers 8–11) written only once one of
  them holds weight, so eight-layer terrains load and save byte-identically; the GPU binds the maps as one texture array
  (the fragment stage stays within 16 images), and the shader reads the third map only for materials past eight layers.
  The Forest's sand is layer 8.
- **The map is 320 m.** The valley keeps its coordinates; the beach and lake fill the new south, the east ridge the new
  east. The lake runs off the south edge into a far basin (a flat water plane with the pond's material; no seam).
- **Cliffs bounded by the sun.** The morning sun is low (25°) in the north-north-east and the look-dev was tuned with the
  sun reaching the fall, the pools and the glade, so `SunCap` keeps every cliff and mountain under a 21° line from the
  showpieces in the sun's direction, fading over its cone's edge. Where that holds a cliff down, a ravine at its foot
  makes the face (a long walkable slope back up). No ravine digs into a reference shot's frame (R6 looks up at the old
  rim's pines, R2 and R7 north at the fall), so ≈ 55 m of north edge is closed by the wall alone. The invisible walls
  stay as a backstop everywhere.
- **Distant tree shadows** (follow-up): the impostors' shadow density 0.5 → 0.8 (at half the crowns past 55 m read
  evenly sunlit and pale); it and the impostor distance are pause-menu options.
- **Distant mountains** are a second, coarse terrain (2048 m at 8 m, no collision, no shadows) starting at the valley's
  edge heights, with impostor-only conifers on the foothills.
- **The cabin is a script**, not a hand-edited asset: `Tools/cabin/build_cabin.py` builds it in Blender headless
  (`just forest-cabin`) and exports glTF with named empties (`Place_<id>` furniture sockets, `Fire`, `ChimneyTop`,
  `CandleFlame`, `Interior`, `R9`); the game converts materials to PBR, makes the shell two-sided (one-sheet roof, back
  faces culled in the shadow pass), collides the shell's triangles, adds a ramp over the entry steps, and puts the fire,
  the door and the furniture at the empties.
- **An interior zone instead of local probes.** One probe volume renders and it keeps 85 % of the sky light under any
  roof, so while the active camera is in the room the sky ambient and volumetric haze ease down; the fire is the light.
- **The probe fingerprint** (the load-time staleness check) now skips meshes with nothing opaque (glass), as the bake
  does: a scene with windows read as stale after every bake.

## Consequences

- Generation stays ≈ 0.45 s (distance fields capped at 64 m); trees ≈ 2 400 (+550 on the new pine ridge), ≈ 3 200 far
  impostors. On a loaded machine the benchmark measured +1.6 ms at p50 against main under the same load (+1.5 ms of sun
  shadows, ≈ 300 more draws); the baseline is to be re-recorded on a quiet machine.
- 43 MB of new LFS art (furniture, cabin textures) plus the rock scans and the sand: ≈ 210 MB of the 400 MB budget.
- The probe bake is 207 368 probes (6.7 MB), ≈ 2 minutes.
- R6's and the fall's stretches of the north edge have no cliff; recomposing R2, R6 and R7 would allow them.
