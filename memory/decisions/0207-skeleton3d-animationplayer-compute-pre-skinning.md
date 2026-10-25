# ADR 0207 — Skeleton3D, AnimationPlayer and compute pre-skinning

- **Date:** 2026-10-25
- **Status:** accepted
- **Milestone:** G1a (player core) + G1b (skeletons, skinning), issue #119 (Crash Site Defense crew)
- **Docs:** docs/design/animation.md, docs/design/future/keyframe-animation.md

## Context

Crash Site Defense moves to 3D with humanoid crew made in Blender. It needs skinned meshes that animate, crossfade,
fire an event when a swing lands, carry a tool and a torch on the hand bone, and run on the dedicated server
(`HeadlessHost`). Nine crew skins share one rig; up to 12 characters of about 6 000 triangles are on screen. The engine
had no `Skeleton3D`, `Skin`, skinned rendering or `AnimationPlayer`.

The renderer has many vertex shaders that read positions: lit, lit with streams, outline (`MaterialOverlay`/`NextPass`),
object ids, the depth prepass (with motion vectors), three shadow caster variants (CSM, atlas, point cubes), foliage
and impostors, and the spatial `ShaderMaterial` that issue #121 adds. Skinning in each would multiply variants.

## Decision

- **Godot 4's model and names.** `Skeleton3D` holds bones as data (names, parents before children, rests; poses set at
  run time); `Skin` maps a mesh's joints to bones by name with inverse bind matrices; `MeshInstance3D` gains
  `Skeleton` (default `..`) and `Skin`; `MeshSurface` gains `Joints` (4 per vertex) and `Weights` (one `Vector4`);
  `BoneAttachment3D` (a child of the skeleton) copies a bone's pose into its transform.
- **Compute pre-skinning.** A skinned instance gets a `MeshVertex` buffer per frame slot. After `BeginFrame` and before
  `OnShadowPass`, `RenderServer.SkinMeshes()` writes every prepared instance's palette (inverse bind × bone pose ×
  skeleton→mesh, three float4 rows per joint) into a host-visible ring keyed by `FrameSlot`, dispatches
  `Skinning/Skin.vk.comp` once per instance, and adds one barrier (compute write → vertex attribute read). The draw
  lists use a per-instance `MeshGpu` view (the instance's output as vertices, the shared mesh's indices and streams),
  so **every existing pipeline draws the pose unchanged**: shadows, outline overlays, picking, sub-viewports and #121's
  shader materials. The only new pipeline is the prepass's `MeshDepthSkinned`: it reads last frame's skinned vertices
  at binding 5, so animated limbs get their own motion vectors (TAA, motion blur).
- **Bounds** come from per-joint bind-space boxes transformed by the pose (O(joints), exact enough for shadow-caster
  culling); `SkinState` builds them with the palette once per frame.
- **`AnimationPlayer` is the G1a core only:** transform tracks (`Position3DTrack`, `Rotation3DTrack`, `Scale3DTrack`,
  for bones or nodes) and an `EventTrack`; Godot's playback API (`Play(name, customBlend, customSpeed, fromEnd)`,
  `PlayBackwards`, `Queue`, `Stop`, `Pause`, `Seek`, `Advance`, `SpeedScale`, `CurrentAnimationLength/Position`) and
  signals (`AnimationStarted`, `AnimationFinished`, `AnimationChanged`, plus `AnimationEvent(animation, name)`).
  Crossfades mix two animations per channel; a channel only the outgoing one animates blends to the bone's rest (or the
  node's value at the switch). Tracks bind once per animation; evaluation uses per-track key cursors and typed calls
  and allocates nothing.
- **Events are an `EventTrack`, not method tracks.** Method tracks need the generator's `[AnimationMethod]` invokers
  (G1.4); a named event raised as a signal is what the game needs now and works the same on the server.
- **No new limits in `limits.json`:** the shader reads joints through a push-constant joint count; `Skin.MaxJoints`
  (256) is a C# constant (`MeshSurface.Validate`, the importer).

## Consequences

- One compute shader serves every pipeline, and #121's shader materials skin for free. The cost is a vertex buffer per
  instance per frame in flight (32 B × vertices × 2) and one dispatch per instance; skinned instances are not
  instanced together.
- 12 characters (and up to 192 in the sweep) stay at the 120 Hz display cap on an M5 in Release; the steady state
  allocates nothing with crossfades, events, the prepass and TAA; validation is clean.
- Value, bezier, method and audio tracks, cubic interpolation, the editor timeline, blend trees and root motion remain
  in the proposal (`future/keyframe-animation.md`, `animation-graph.md`, `animation-authoring.md`).
- A third `Play` during a crossfade drops the oldest animation instead of blending from the current mix.
- `SpineNode.AllAnimations`/`CurrentAnimation` now name `Spine.Animation` explicitly (the engine's `Animation` shadows
  it inside the `MainframeEngine` namespace).
