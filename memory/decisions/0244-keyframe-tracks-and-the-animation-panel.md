# ADR 0244 — Keyframe tracks and the Animation panel: G1a complete

- **Date:** 2026-11-05
- **Status:** accepted
- **Milestone:** G1a (G1.1–G1.7)
- **Docs:** docs/design/animation.md (the Keyframe animation proposal moved into it), docs/design/editor.md,
  docs/design/scene-serialization.md

## Context

ADR 0207 shipped `AnimationPlayer` with transform and event tracks, enough for imported characters. G1a also asks for
keying any property, calling game code from keys, sound on keys, and an editor timeline to author all of it, without
reflection or per-frame allocation.

## Decision

- **Bindings through the generated registration.** `ExportPropertyInfo<TOwner, TValue>.Bind` returns a typed
  `PropertyBinding` over the generated getter and setter; `AnimationValueOps.Get<T>()` packs a value as floats (scalars,
  enums and bools directly, every float-array codec through `IFloatVectorCodec<T>`). A `:x`-style suffix keys one
  component. Values live in tracks as flat `float[]` with `Components` per key, so one `ValueTrack` type serves every
  value type and the `.mscene` JSON stays plain arrays.
- **Cubic in time, mirrored at the ends.** Godot's `cubic_interpolate_in_time` (spherical for rotations); a missing
  neighbour is mirrored through the end key instead of repeated, so a two-key cubic track does not ease to a stop at
  its ends. `Transitions` use `Mathf.Ease` on every interpolated track.
- **`[AnimationMethod]`, not any public method.** The generator emits invokers and parameter codecs only for marked
  methods (MFG011 for invalid ones), so keys call without reflection and arguments are parsed once at bind time. The
  engine marks the audio players' `Play`/`Stop` and the particles' `Restart`. `NodeTypeInfo` keeps its old constructor
  so assemblies built against 1.1.x still load.
- **Editor preview captures instead of a RESET animation.** The panel records the values an animation touches before
  posing and restores them on Stop, a player change and around saves; method and audio keys never fire in the editor.
- **Two snapshot undo actions** (`AnimationEditAction` over an animation's tracks and settings,
  `AnimationLibraryAction` over a library's list) instead of the proposal's seven: key arrays are small and one shape
  covers every edit.
- **Shared timeline:** `Src/Timeline/` (`TimelineZoom`, `TimelineRuler`, `TimelineSnap`, `TimelineHost`,
  `TimelineCanvasBase`) extracted from the song tab, which now draws on it; gestures stay per view.

## Consequences

- G1a is complete; `docs/design/future/keyframe-animation.md` is gone. Its unbuilt panel features (curve view, key
  copy/paste and duplicate, play backwards, track filter, persisted tab and pin) are known issues in animation.md.
- Game nodes get key-callable methods by marking them; Godot's "call any method" is not supported.
- G17b's 2D skeleton editor builds on the shared timeline instead of extracting it.
