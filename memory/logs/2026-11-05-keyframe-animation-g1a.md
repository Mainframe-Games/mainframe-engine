---
summary: >-
  G1a complete (ADR 0244): value, bezier, method and audio tracks with cubic and eased keys on typed bindings, the
  editor's Animation panel on a timeline extracted from the song tab, and the Demo workshop animated by tracks alone.
---

# 2026-11-05: keyframe animation (G1a)

Branch `feature/paths-finish` (the gameplay toolkit's partly-done items, one PR). Plan:
[G1a keyframe animation](../plans/2026-11-05-keyframe-animation-g1a.md). As built:
[Animation and skinning](../../docs/design/animation.md) (the proposal moved into it).

## What changed

- **Engine:** `AnimationValueOps`/`PropertyBinding` (typed writes through the generated registration, sub-components),
  `IFloatVectorCodec<T>`; `ValueTrack`, `BezierTrack`, `MethodTrack`, `AudioTrack`; `Cubic`, `Transitions`, `LoopWrap`
  on transform and value tracks; `AnimationPlayer` value slots in crossfades, method and audio keys in play direction,
  `CallbackModeMethod`, `BlendTimes`, `GetSyncState`/`ApplySyncState`; `[AnimationMethod]` with generated invokers
  (MFG011), on the audio players' `Play`/`Stop` and the particles' `Restart`.
- **Editor:** `Src/Timeline/` (shared with the song tab); the Animation bottom-dock tab (`AnimationEditor`,
  `AnimationPanel`, `AnimationTimelineCanvas`, `animation.rml`), inspector key buttons and K, method picker and audio
  keys on double-click, preview capture and restore around saves, `AnimationEditAction`/`AnimationLibraryAction`; node
  warnings for animated bodies; QA script `animation-walkthrough.qa`; icons keyframe, player-skip-back, repeat-off, pin,
  help.
- **Demo:** the `animation_3d` workshop (`DoorLatch`, lift, door, lamp, chime), Spanish string, screenshot.
- **Docs:** animation (with "Changes from the proposal"), editor, scene-serialization, physics, demo, milestones,
  the proposals that linked the old one, AGENTS.md, README and the landing page (the panel's screenshot).

## Decisions

- [ADR 0244](../decisions/0244-keyframe-tracks-and-the-animation-panel.md).

## Dead ends

- Cubic with repeated end keys eased every two-key track to a stop; mirroring the missing neighbour fixed it.
- Preview Play first ended the preview (restoring values) every frame; restoring was split from ending.
- A `TimelineViewState` name clashed with the video timeline's; the shared type is `TimelineZoom`.

## Verified

- `ValueTrackTests` (incl. 0 B over 1 000 nodes), generator tests, `AnimationEditorTests`, `TimelineTests`, the song
  tests, `Animation3DSceneTests` and all 128 `Demo.Tests`; `just qa-editor Tests/QA/animation-walkthrough.qa`
  captures inspected (the panel keying, playing, undone).
