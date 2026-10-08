# ADR 0149 — Terrain, procedural trees and water are engine features; a forest showcase proves the realistic look

- **Date:** 2026-10-08
- **Status:** accepted (design only; no code yet)
- **Milestone:** Gameplay toolkit G8 (G8a–G8d)
- **Spec:** docs/design/future/terrain.md, procedural-trees.md, water.md, forest-showcase.md

## Context

A low-poly game built on the engine designed an engine terrain system modelled on TerraBrush (MIT):
- `Terrain3D` and `TerrainData`, with PNG image layers;
- a faceted 2 m chunk grid, with collision built from the render triangles;
- sculpt and paint tools;
- `MultiMesh` foliage, object scatter and painted ponds.

That game currently builds its terrain as game code, so the engine has no 3D terrain, trees or water.

The user also wants the engine to show that it can produce high-quality graphics like Unreal or Unity: a small first-person walk through a forest with streams. Today the renderer has none of what such a scene needs:
- Blinn-Phong shading only, with no IBL and no AA;
- no fog, no wind and no vertex colours;
- no texture arrays, no `MultiMesh` and no impostors.

G6 plans PBR, IBL, LOD, particles and SSAO, but lists impostors, TAA and volumetric fog as non-goals.

[Ez Tree](https://github.com/dgreenheck/ez-tree) is an MIT-licensed procedural tree generator. It is about 1.2k lines of JavaScript on Three.js, with a seeded RNG, 15 presets, LODs and leaf wind.

## Decision

- **Terrain is an engine feature (G8a).**
  - The engine owns the design, upstreamed in generic form from the game's pages.
  - Each `TerrainData` picks one of two profiles when it is created:
    - **Faceted:** flat palette colours, 2 m spacing, `TerrainMaterial3D`;
    - **Realistic:** smooth normals, 0.5–1 m spacing, chunk LOD, `TerrainSplatMaterial3D` over `Texture2DArray` PBR layers.
  - The Faceted profile ships first, on today's renderer.
- **Ez Tree is ported to C# (G8b).**
  - A pure, deterministic `TreeGenerator` with a bit-exact RNG, checked against the JavaScript output.
  - `TreeOptions` with the presets, and a `[Tool]` `Tree3D` node that bakes meshes.
  - Two styles: **LowPoly** (flat-shaded palette bark, low-poly leaf blobs; for low-poly games) and **Realistic** (PBR bark, alpha-cut leaf cards).
  - Wind runs through a built-in `FoliageMaterial3D` that reads a shared `WorldEnvironment` wind.
  - Far trees use octahedral impostors.
- **Water (G8c).**
  - Ponds and lakes come from the terrain's water layer.
  - Streams are `River3D` splines.
  - `WaterMaterial3D` handles flow, refraction and foam, using `SceneTextures` (a depth prepass shared with G6.6, plus an opaque colour copy).
- **The showcase is a separate example project (G8d).**
  - `Examples/Forest` is a standalone mfgame project, not a scene in `Examples/Demo`, with its own release zip.
  - The rendering it needs beyond G6 is phased in G8d: physical sky, fog, light shafts, TAA, auto exposure, LUT grading and contact shadows.
- **Assets are CC0 or procedural.**
  - ambientCG and Poly Haven, plus Ez Tree's bark (CC0) and leaves (MIT).
  - Trees, grass and terrain shape are generated in the engine.
- **New shaders are Slang** (ADR 0144). New 3D looks are built-in material types (the ADR 0132 pattern), not user shaders.

### Open questions settled (the user accepted every default, 2026-10-08)

Each proposal's "Decisions" section has the full list. The ones that shape the work:

- **Terrain (G8a):**
  - Realistic LOD is geomipmapping with skirts on G6.4 index-only LODs (not CDLOD).
  - Collision is per-chunk trimesh near moving bodies, and physics ray queries test terrains analytically.
  - `MeshVertex` in v1 (no compact terrain vertex).
  - 8 splat layers in two RGBA8 weight maps, with hex-tiling anti-tiling.
  - RGBA8 layer textures until the M12 cooker.
  - One terrain per scene; brush masks are regenerated rather than copied from TerraBrush.
- **Trees (G8b):**
  - A literal port in `Generation/` with idiomatic C# around it.
  - `Continuous` bark UVs (Ez Tree's mirrored UVs only for parity tests).
  - No trellis yet.
  - Runtime generation only when not baked (editor and debug builds).
  - 1024² impostors.
  - Synchronous generation in the editor.
- **Water (G8c):**
  - `SceneTextures` follows G6's rule and waits for M11 step 1. Ponds, streams and the fallback look ship before it.
  - Uses a depth prepass shared with G6.6 (not a depth copy).
  - Explicit Carve and Re-carve (no auto-carve).
  - No lake flow brush, engine buoyancy node or underwater fog yet.
  - Faceted water stays static.
- **Forest showcase (G8d):**
  - `FirstPersonController` stays in the Forest project.
  - No froxel volumetric fog until M11 brings compute; no upscaling for Medium; no clouds with the physical sky.
  - Desktop texture compression waits for the M12 cooker.
  - The release attaches osx-arm64 and win-x64 Forest player zips.
  - Dithered cross-fade between mesh LODs is added.
  - No grass that bends around the player.
  - Post effects run in Play only, not in the editor viewport.

## Consequences

- The engine gains these prerequisites:
  - `Texture2DArray`;
  - an optional second vertex stream (`MeshSurface.Colors`/`Custom0`);
  - `MultiMesh`;
  - an editor viewport-tools API;
  - `SceneTextures`.
- G6.1/G6.2 (PBR and IBL) gate the Realistic profile and the realistic trees, but not the Faceted terrain or the low-poly trees.
- Ez Tree's MIT licence needs a header in the ported file and a `THIRD_PARTY_NOTICES.md` entry when the code lands.
- The low-poly game drops its game-side terrain once `Terrain3D` (Faceted) lands, and its trees become Ez Tree LowPoly presets.
