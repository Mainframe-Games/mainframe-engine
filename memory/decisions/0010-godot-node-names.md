# ADR 0010 — Godot node names

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M2 (W2 lane A)

## Context

The node-system proposal left open whether node types mirror Godot's names exactly (`OmniLight3D`,
`QueueFree`, `NodePath`) or keep engine names (`PointLight3D`, the old `Camera3D` math class). The plan's
default was Godot names, for familiarity: most people who will use a scene tree + editor already know Godot.
Several engine names collided with the Godot ones, and the renderer lane was editing renderer files
concurrently.

## Decision

Use Godot's names and concepts for the scene API — `Node`, `Node2D`, `Node3D`, `SceneTree`, `NodePath`,
`PackedScene`, `Resource`, `Camera3D`/`Camera2D`, `DirectionalLight3D`/`OmniLight3D`/`SpotLight3D`,
`WorldEnvironment`, `VisualInstance3D`, `Timer`, `ProcessMode`, `QueueFree`, `CallDeferred`, `AddToGroup`,
`GetNode<T>`, `Reparent`, `Owner`, `UniqueNameInOwner`, `[Export]`, `[ExportGroup]`, `[Signal]`, `[Tool]` —
with these deliberate exceptions:

| Godot | Mainframe | Why |
|---|---|---|
| `Viewport` | `SceneViewport` | The renderer uses `Silk.NET.Vulkan.Viewport` unqualified in `namespace MainframeEngine` (ShapeBase, ShadowSystem, SkyEnvironment, and future M3/M4 files); a `MainframeEngine.Viewport` would shadow it in every one of them. |
| `_Ready`, `_Process`, … | `OnReady`, `OnProcess`, `OnPhysicsProcess(float)`, `OnInput`, … | The engine's existing `On*` callback style (design doc decision). |
| `rotation` (Euler radians) | `Rotation` (quaternion) + `RotationDegrees` (Euler, serialized) | The design stores a quaternion; Euler order is the engine's legacy X→Y→Z so existing scenes keep their look (Godot uses Y→X→Z). |
| `SceneGrid3D` (design sketch) | `Grid3D` | Avoids two types differing only by case (`SceneGrid3d` is the renderer class). |
| `SpineSprite3D` (design sketch) | `SpineNode` (kept) | Renaming would have conflicted with M1's concurrent `SpineNode` changes; rename later if desired. |
| `MeshInstance3D` | `Box3d`, `Quad` (kept until M3) | Meshes/materials are M3. |

The pre-M2 math cameras were renamed `PerspectiveCamera` / `OrthographicCamera` (`ICamera`) so the Godot
names belong to the nodes; the nodes wrap them.

## Consequences

- Godot users can read and write scenes and node code with little translation.
- `MainframeEngine.Timer` shadows `System.Threading.Timer` inside `MainframeEngine.*` namespaces (the
  Sandbox qualifies the latter); `Camera3D`/`Camera2D` call sites from before M2 must use the new math names.
- Docs and the editor (M10) use the Godot vocabulary.
