# ADR 0184 — Engine-owned 2D skeletal animation (Skeleton2D) alongside Spine

- **Date:** 2026-10-11
- **Status:** proposed (design only; no implementation yet)
- **Milestone:** Gameplay toolkit, G17a–G17g
- **Docs:** docs/design/future/skeleton-2d.md, docs/design/future/skeleton-2d-editor.md,
  docs/design/future/animation-graph.md

## Context

Brogan wants a 2D skeletal animation system that replaces Spine for new work, is heavily inspired by it, and lets
artists animate inside the engine's editor. The only skeletal animation today is the vendored spine-csharp 4.3 runtime
(`SpineNode`, `SpineSprite`, ADR 0116). It has these problems:

- **Licence.** It is under the Spine Runtimes License, so every game and artist needs a Spine Editor licence.
- **Workflow.** Authoring happens outside the engine.
- **Gaps in the renderer.** No bounds, no prepass, picking or motion vectors, whole-quad shadows, and slot blend modes
  ignored.
- **Control.** The vendored code must not be modified.

The existing plans assumed Spine stays: [Keyframe animation](../../docs/design/future/keyframe-animation.md) lists 2D
skeletal animation and `AnimationTree`-style state machines as non-goals.

Requirements gathered on 2026-10-11:

- **Features:** full Spine-level rigging (weighted meshes, IK, transform, path and physics constraints, skins, slots,
  draw order, sequences, clipping, blend modes, normal maps).
- **Playback:** Spine-like tracks and mixing plus a state machine with blend spaces and masked layers, designed to be
  shared with 3D.
- **Editor:** a dedicated tab with Setup and Animate modes; PSD and PNG import; mesh and weight tools.
- **Performance:** about 200 skeletons at 60 fps with zero per-frame allocation.
- **Integrations:** bone attachments, hitboxes, networking, the `AnimationPlayer` bridge.
- **Interop:** a Spine JSON importer.
- **Agents:** `mfe` and Claude tooling.

## Decision

1. **A new system that lives alongside Spine, not inside it.** `SpineNode`/`SpineSprite` and `Plugins/Spine` stay for
   teams with Spine licences. The new code shares nothing with them at run time. Spine content moves over through a
   one-way **Spine 4.x JSON importer**; there is no export back to Spine.
2. **Clean room.** No code is ported from spine-csharp or written by reading it, because its licence covers derivative
   works. The importer is written from Spine's public JSON format reference, and behaviour follows Spine's public
   documentation. The runtime aims for parity in behaviour, not bit-exactness. A render test that compares the two
   runtimes on spineboy, within a tolerance, checks the result without sharing code.
3. **A Spine-style data model with bones as data.**
   - **Files.** `.mskel` (self-describing JSON, prefix `skl_`) holds the rig and skins. Every clip is its own `.manim`
     (prefix `anm_`) that references the skeleton by UID, so artists don't conflict and clips load on demand.
   - **Layout.** Timelines are laid out as parallel `t[]`/`v[]`/`curve[]` arrays for readable diffs and agent edits.
   - **Format code.** A dedicated `SkeletonFormat` reads and writes the files, not the generic resource saver.
   - **Godot's model is not used:** there are no `Bone2D` nodes.
4. **Y-down pixel bones** (ADR 0110). The Spine importer flips once. The 3D node mirrors once in its model matrix.
5. **A compact binary build, cached like tree bakes.**
   - Import packs atlases (MaxRects, trim, padding, bleed), triangulates meshes and pre-samples curves into a binary
     build.
   - The build is cached in `skeleton-bakes/` under the pipeline cache root, keyed by SHA-256, as ADR 0172 does for
     trees. The cache is outside the project and can be deleted.
   - Project sources stay JSON and PNG, so ADR 0011 holds. `mf-cook` ships the build in exports (ADR 0100).
6. **GPU skinning by a compute pre-pass**, `RenderServer.SkinMeshes`, keyed by `FrameSlot`. It is the step G1b
   planned, built here first. It is the engine's first compute and storage-buffer user, and it adds compute pipelines
   to `IVulkanContext.Pipelines`.
7. **Canvas extensions:**
   - a `GpuSkinned` draw command kind;
   - per-command blend modes (which also fix `SpineSprite`'s ignored slot blends);
   - premultiplied Add, Multiply and Screen modes;
   - `CanvasTexture` normal maps with `PointLight2D.Height`.
   
   Clipping attachments use a CPU path in v1; there is no stencil.
8. **The 3D node, `SkeletonSprite3D`, draws through `MeshRenderer`** as a skinned mesh. That gives it culling,
   alpha-tested shadows, the prepass, picking and motion vectors. `MeshRenderer` gains per-slot vertex overrides,
   dynamic order-keeping runs and premultiplied blend modes.
9. **A shared animation layer in `Src/Animation/`.** `AnimationTracks` (Spine-like tracks and mixing) and an
   `AnimationGraph` resource with its `AnimationGraphPlayer` (state machines, 1D/2D blend spaces, masked override and
   additive layers) produce weighted `ClipSample`s for an `IPoseBackend`. 2D implements the backend now; `Skeleton3D`
   implements it with G1b. This supersedes G1a's "no state machines" non-goal, which pointed at a later proposal.
10. **The editor gets a dedicated Skeleton tab** with Setup and Animate modes, its own undo history, and live updates
    of scene instances. It is preceded by:
    - an `IDocumentTab` refactor that replaces the session's `tab is SongTab` checks;
    - the extraction of the shared `Editor/Src/Timeline/` from the song tab, done here instead of in G1a's G1.5.
11. **Runtime-first phasing:**

    | Phase | Contents |
    |---|---|
    | G17a | runtime + Spine import |
    | G17b | Setup mode + timeline extraction |
    | G17c | Animate mode |
    | G17d | meshes, weights, PSD |
    | G17e | constraints + physics |
    | G17f | the graph |
    | G17g | 3D + integrations + `mfe skel` + skill |

## Consequences

- **Licence and workflow.** Games can ship 2D skeletal animation under the engine's MIT licence, and artists author it
  in the editor.
- **Two systems to maintain** while Spine stays. New features go only to the new one; Spine is kept working, not
  extended.
- **Clean-room cost.** Work is slower and some edge-case behaviour differs from Spine. Reviews must enforce it.
- **Compute risk.** Compute and storage buffers arrive in the renderer for the first time: validation on MoltenVK and
  lavapipe, and M11 (backend abstraction) must cover compute.
- **New in-house code that others can reuse:** a PSD reader, an atlas packer, constrained Delaunay triangulation and
  heat-diffusion weights, with no new NuGet packages. The atlas packer and normal-mapped canvas lighting benefit G2 and
  `Sprite2D`.
- **Build cost.** The first load of a skeleton costs a build. Later loads read the cache; shipped games read cooked
  builds.
- **Naming.** The `Skeleton2D` name collides with Godot's node-based `Skeleton2D` for Godot ports. That is accepted
  and documented.
