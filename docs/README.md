# Mainframe Engine — Documentation

Design documentation for Mainframe Engine. The **current-state** docs describe the engine exactly as
it exists in the code today (including its known bugs). The **future** docs are design proposals for
work that has not started yet. [`milestones.md`](milestones.md) ties the two together.

> Status: M0–M10 complete (v1.0.0 line, 2026-10-05); M11–M13 are proposals. When code changes, update the matching
> doc and its *Known issues* section in the same commit.

## Start here

| Doc | What it covers |
|---|---|
| [Milestones](milestones.md) | Roadmap: what is done ✅, what is next, links to every design doc |
| [Architecture overview](design/architecture-overview.md) | Layers, assemblies, dependencies, who owns what |
| [Engine lifecycle](design/engine-lifecycle.md) | Startup, the per-frame loop, shutdown |

## Current-state design docs

| Area | Doc |
|---|---|
| Build & release | [Build & platforms](design/build-and-platforms.md) · [Release & versioning](design/release.md) · [Future: distribution via NuGet](design/future/distribution-nuget.md) · [Future: mobile (M12)](design/future/mobile.md) |
| Core | [Engine lifecycle](design/engine-lifecycle.md) · [Scene graph & nodes](design/scene-graph-and-nodes.md) · [Scene serialization](design/scene-serialization.md) · [Physics](design/physics.md) · [Localization](design/localization.md) · [Demo](design/demo.md) |
| Games & editor | [Projects & GameHost](design/project-and-gamehost.md) (`project.mfproj`, `GameHost`, editor link, code reload, `mfgame` template) · [Editor](design/editor.md) · [Future: editor](design/future/editor.md) |
| Rendering | [Vulkan renderer](design/vulkan-renderer.md) · [2D canvas](design/canvas.md) · [GPU resources](design/gpu-resources.md) · [Materials & meshes](design/materials-and-meshes.md) · [Asset pipeline](design/asset-pipeline.md) · [Color pipeline](design/color-pipeline.md) · [Shadow system](design/shadow-system.md) · [Lighting](design/lighting.md) · [Sky](design/sky.md) · [Spine](design/spine.md) · [Scene grid](design/scene-grid.md) · [Cameras & input](design/cameras-and-input.md) · [Shaders](design/shaders.md) · [Coordinate conventions](design/coordinate-conventions.md) |
| UI | [Game UI (RmlUi)](design/game-ui.md) · [Native libraries](design/natives.md) |
| Tooling | [ImGui & debug tools](design/imgui-and-debug-tools.md) · [Testing](design/testing.md) |
| Audio | [Audio](design/audio.md) |
| Online | [Networking](design/networking.md) · [Steamworks](design/steamworks.md) |

## Future design proposals

Located in [`design/future/`](design/future/). Each one has: Problem · Goals / Non-goals ·
Proposed design · Diagram · Task list · Open questions. See [milestones.md](milestones.md) for order.

| Proposal | Milestone |
|---|---|
| [Editor, after M10](design/future/editor.md): remote scene tree, simulate mode, box selection, docking | after M10 |
| [Rendering backend abstraction (WebGPU)](design/future/rendering-backend-abstraction.md) | M11 |
| [Mobile core (Android + iOS)](design/future/mobile.md): platform layer, lifecycle and safe areas, TBDR rendering and quality tiers, touch/gestures/virtual controls, ASTC/KTX2 asset cooking, AOT and size budgets, editor deploy and live preview, store pipeline | M12 |
| [Mobile platform services](design/future/mobile-services.md): IAP, achievements/leaderboards/cloud saves, consent + ads, notifications, analytics/crash reporting via `mfplatform` shims | M13 |
| [Distribution via NuGet](design/future/distribution-nuget.md) | — |

## Conventions

- **One topic per file.** Cross-link instead of duplicating.
- **Diagrams:** Mermaid for graphs embedded in markdown (sequence, class, flow, gantt).
  SVG in [`images/`](images/) for detailed graphics (memory layouts, descriptor tables, timelines,
  coordinate spaces). SVGs carry an opaque light background so they read on dark themes too.
- **Source links** are relative (`../../MainframeEngine/Src/...`) so they work on GitHub and in IDEs.
- **Known issues** cite `file:line` at the snapshot commit. Issues found by reading code but not
  reproduced at runtime are marked *(inferred)*.
- **Status legend** (milestones): ✅ done · 🚧 in progress · ⬜ planned.
