# ADR 0241 — Video mode becomes a multi-track timeline editor

**Date:** 2026-11-04
**Status:** proposed (accepted when the owner accepts [the proposal](../../docs/design/future/video-nle.md))
**Docs:** [Video NLE proposal](../../docs/design/future/video-nle.md), [Video editing mode](../../docs/design/video-editor.md),
[Gameplay recording](../../docs/design/recording.md). Partly supersedes [ADR 0191](0191-video-editing-mode.md).

## Context

ADR 0191 put Cutback inside the editor. A video tab trims one recording: its segments partition the source, cuts are
kept or removed, and export concatenates the kept ranges. The design doc ruled out a multi-track timeline, transitions,
titles and music beds ("not an NLE").

The owner makes the game's social clips and trailers in DaVinci Resolve. A typical edit is a 9:16 timeline built from
ten gameplay recordings, reframed from 16:9, with music under it and a title over the payoff. Resolve is heavy for
this, and each clip still has to leave the editor. The owner wants video mode to do it instead.

## Decision

- **Video mode becomes a small non-linear editor**, laid out like Resolve's Edit page:
  - top row: media pool, Source viewer, Timeline viewer, Inspector (with a Transcript tab);
  - below: a multi-track timeline (video tracks over audio tracks);
  - under the tracks: a cut-editor pane for the selected clip.
- **Timelines are `.mftimeline` files** (versioned JSON). A project can have many, and each opens as a video-mode tab.
- **Each clip keeps Cutback's segment partition over its whole source.** Transcription, filler, silence and idle
  detection run per clip and cut non-destructively inside it. The clip's timeline length is its kept time, and the
  clips after it ripple. `SegmentList` and every detector stay as they are.
- **Video mode needs the open game project.** Media lives under `Assets/Video/`:
  - `Recordings` (F10 writes here when a project is open);
  - `Media` (outside files are copied in, including by dragging from the OS);
  - `Timelines`.

  `Assets/Video` never ships: builds and exports exclude it.
- **The engine raises OS file drops** as `SceneTree.FilesDropped`, from the SDL window's existing `FileDrop` event.
- **Preview and export stay on ffmpeg as a child process** (ADR 0190).
  - Preview: one decoder per visible track, composited as sprites in a `SubViewport`, with audio mixed into the existing
    clock voice.
  - Export: one generated filter graph with one input per clip window.
  - Titles are rasterised by the engine and overlaid, so preview and export match.
- **Undo is the editor's `UndoRedo`**, holding whole-timeline snapshots. `SegmentEditor`, `EditHistory` and
  `VideoDocument` leave the editor.
- **Dropped:** `.mfcut` and `.cutback` projects (no migration), Recent Videos, video mode without a project, Fast
  (stream-copy) export, and the recording dialog's Open in Cutback.
- **Delivered in phases:**
  - 1a: engine prerequisites.
  - 1b: the headless timeline library.
  - 1c: the NLE replaces the old mode.
  - 1d: the per-clip Cutback tools.
  - 2: transitions, titles, images, speed, captions.
  - 3: nested timelines, proxies.

  Between 1c and 1d, `main` briefly has no detection in video mode. The owner accepted that.

## Ruled out

- **Keep Resolve.** It stays heavy, and every edit leaves the editor; the owner asked for the opposite.
- **A separate pre-trim step** (trim in today's mode, then send the result to a timeline). It needs two screens and two
  file types for one job, and re-trimming means going back through the first screen.
- **Splitting clips at every detected cut.** Clips would be plain, but restoring a cut would need undo. The in-clip
  partition keeps click-to-restore and reuses every detector unchanged.
- **Commands per edit for undo.** Ripple and razor-all-tracks touch many clips. Immutable snapshots share structure and
  cannot get an inverse wrong.
- **ffmpeg `drawtext` for titles.** It needs an ffmpeg built with libfreetype, has fragile escaping and font paths, and
  would not match the preview.
- **A decoding library in-process** (G16's codecs, libav bindings). It would be a new native dependency;
  ffmpeg as a child process already serves the preview and export.
- **Media outside the project** (references to recordings in user data). A timeline would break when the project moves
  to another machine, and the owner wants everything in `Assets/`.

## Consequences

- Game projects hold large media under `Assets/Video`. The engine never touches `.gitignore` or `.gitattributes`; each
  project decides.
- Builds stop copying `Assets/Video` to `bin/`: an MSBuild target in the engine package and `MainframeGame.props`. Export
  refuses to ship it. The G19 pack writer must use the same predicate.
- Video mode stops working without a project, so the Project Manager's Video Editor button opens a project first.
- `SetPixels` textures update in place on the GPU, which every dynamic canvas texture benefits from.
- Old `.mfcut` projects no longer open. Their recordings still open as media.
- The editor no longer offers Cutback. Cutback itself is unchanged.

## Revisit when

- Previews of three or more layers cannot hold 30 fps without proxies: bring proxies (phase 3) forward.
- G16 ships a GPU video decoder fit for scrubbing: consider it for the preview.
- Keyframed animation (motion, opacity) becomes the common need: it was left out on purpose.
