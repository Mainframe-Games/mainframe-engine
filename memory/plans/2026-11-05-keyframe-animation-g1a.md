# Plan: G1a keyframe animation (value, bezier, method and audio tracks; the Animation panel)

**Status:** done
**Docs:** [Keyframe animation proposal](../../docs/design/future/keyframe-animation.md) (§ Animation resource, Evaluation,
Binding properties, Method tracks, Audio tracks, AnimationPlayer, Editor: the Animation panel, Shared timeline, Task list
G1.1–G1.7), [Animation and skinning](../../docs/design/animation.md) (what shipped: ADR 0207, 0208, 0210)
**Started:** 2026-11-05

## Goal

The rest of G1a, as the proposal designs it:

- **Bindings (G1.1):** `IAnimationValueOps<T>`, `AnimationValueOps.Get<T>()` (floats, integers, bools, enums, vectors,
  quaternions, both colour types, `Vector2I`, `Rect2`, transforms, game types with float-array codecs),
  `ExportPropertyInfo.Bind` with sub-components (`Position:y`, `Modulate:a`). No reflection, no allocation per frame.
- **Tracks (G1.2):** `ValueTrack`, `BezierTrack`; `Cubic` interpolation, per-key `Transitions` and `LoopWrap` on value
  and transform tracks; discrete values for bools, integers and enums.
- **Method and audio tracks (G1.4):** `[AnimationMethod]` with generated invokers and diagnostics; `MethodTrack` with
  JSON arguments parsed at bind time; `AudioTrack` playing `AudioPlayer*` nodes.
- **Player (G1.3 rest):** value slots in crossfades, method/audio keys fired in play direction across loops,
  `CallbackModeMethod`, `BlendTimes`, `GetSyncState`/`ApplySyncState`; node warnings in the editor; a 1 000-node
  allocation gate and `AnimationEvaluate1k`.
- **Shared timeline (G1.5):** `MainframeEngine.Editor/Src/Timeline/` extracted from the song tab, the song tab on it.
- **Animation panel (G1.6):** bottom dock tab, transport, animation menu, track list, timeline canvas with keys, key
  editor, inspector key buttons and K, preview capture/restore, undo actions; QA script; screenshot.
- **Demo and docs (G1.7):** the `animation_3d` scene gains a lift, a door with a method key, a flickering lamp and a
  chime; `animation.md` absorbs the proposal.

## Not in scope

- G1c, G1d, G1f (authoring, posing, rigs), morph targets, the animation graph (G17f).
- `Tween` on the bindings (optional in G1.7).
- Editing external `.mres` libraries' history separately (open question 5): they are edited with the scene.

## Steps

- [x] Bindings and value ops. Verified by: `AnimationBindingTests` (each value kind, sub-components, discrete, game
  struct, unsupported type warns once), 0 B writes.
- [x] Value and bezier tracks, cubic, transitions, loop wrap. Verified by: `AnimationTrackTests` per mode and value
  type, `.mres` round trip.
- [x] `[AnimationMethod]` generator and method/audio tracks. Verified by: generator tests (invokers, diagnostics),
  `AnimationPlayerTests` (keys across loops, backwards, ping-pong, deferred/immediate).
- [x] Player: value blending, blend times, sync state. Verified by: crossfade and capture tests; allocation gate over
  1 000 nodes during a crossfade with method keys; benchmark runs.
- [x] Shared timeline, song tab on it. Verified by: the song tab's tests unchanged and green; timeline math tests.
- [x] Animation panel. Verified by: editor tests for every undo action, key buttons, preview restore, save never
  writes the preview; `Tests/QA/animation-walkthrough.qa` captures inspected.
- [x] Demo, docs, proposal moved. Verified by: `Demo.Tests`, the screenshot, `check.sh`.

## Outcome

Done (ADR 0244). Changes from the plan: undo is two snapshot actions; gestures were not extracted into the timeline;
the panel adds method and audio tracks from header buttons instead of a `+` menu; the curve view, key copy/paste and
play backwards are follow-ups (animation.md → Known issues). Tests: `ValueTrackTests` (17), generator MFG011,
`AnimationEditorTests` (10), `TimelineTests` (2), `Animation3DSceneTests` (5); QA `animation-walkthrough.qa`.

## Done when

- The tests above pass, the QA captures show the panel keying and playing, and G1a's row in `docs/milestones.md` is ✅.

## Open questions

- None blocking: the proposal's open questions keep their proposed answers (compute skinning, Assimp, no auto-key).
