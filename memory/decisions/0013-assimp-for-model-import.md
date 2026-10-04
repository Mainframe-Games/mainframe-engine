# ADR 0013 — Assimp for model import

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M3 (W3 lane m3b)

## Context

The materials-and-meshes proposal left open whether to import glTF only, through a dedicated loader, or every
format through Assimp. `Silk.NET.Assimp` was already a referenced (approved) package. Its native library
(`Ultz.Native.Assimp` 5.4.1, the transitive runtime package) ships for win/linux/osx x64 and arm64. Users bring
FBX and OBJ as well as glTF.

## Decision

All model formats go through Assimp (`ModelImporter`: `.gltf .glb .fbx .obj .dae`).

- **Post-processing:** triangulate, join identical vertices, sort by primitive type, improve cache locality,
  validate, and `FlipUVs` (top-left UV origin, like Vulkan and glTF). Optional smooth normals and mesh
  optimisation come from `.meta` settings.
- **Mapping:**
  - Each Assimp node becomes a `Node3D` or a `MeshInstance3D`.
  - The meshes of a node become one `ArrayMesh`, with one surface per Assimp mesh. Nodes with the same meshes
    share it.
  - Materials become `StandardMaterial3D` (see ADR 0014). glTF colours are linear and are converted to sRGB.
  - Textures load through `ResourceLoader` (their own `.meta` applies) or from embedded data.
- **Instancing:** the import becomes an *imported* `PackedScene`, whose `Instantiate` clones a template. Imports
  are cached in memory by path, size, modification time and settings.

No dedicated glTF loader, and no new dependency.

## Consequences

- One code path for every format. Assimp's glTF support covers what the engine renders today.
- A native dependency: where Silk's loader cannot find the library, imports throw a clear
  `InvalidOperationException`. The Linux CI loader fix for Silk natives (lane `ci/fix`, for SDL) applies here too.
- Assimp normalises away some glTF detail (it converts PBR, and sampler settings are dropped; the image's `.meta`
  decides). A dedicated glTF path can be added behind `AssetImporters` if PBR (ADR 0014) needs it.
