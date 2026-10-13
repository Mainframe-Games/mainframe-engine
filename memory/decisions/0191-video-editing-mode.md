# ADR 0191 — Video editing mode in the editor

- **Date:** 2026-10-14
- **Status:** accepted
- **Milestone:** —
- **Docs:** docs/design/video-editor.md, docs/design/editor.md#editor-modes

## Context

Promo footage made with the editor's gameplay recorder (ADR 0190) needs trimming: dead air, idle gameplay, loading and
pause stretches, filler words. Until now that meant a second app, Cutback, which reads the recording's `.mfcap.json`
sidecar. The editor already records; it should also trim, so a recording goes from F10 to an exported MP4 without
leaving it. Cutback's logic (Core, Media and Analysis, plus the app's timeline, transcript and view-model flows) is
written by the engine's author, and the parts that were GPL were relicensed MIT for this copy.

## Decision

- **One workspace with an `EditorMode`** (Game, Video; Audio for a DAW later). `EditorWorkspace.SetMode` swaps the
  layout, the menu tables, the toolbar group and the tab filter; the shell (menu bar, dialogs, Output, settings,
  commands, session) is shared. The mode persists in editor settings, and opening a recording switches to it.
- **Cutback's MIT logic is copied** into a new editor-only library, `MainframeEngine.Video` (namespaces
  `MainframeEngine.Video.*`, `CutbackProject` renamed `VideoProject`), with its tests in plain `Assert`. Only the editor
  and its tests reference it; the runtime and shipped games never do. The engine owns the copy from now on.
- **Whisper.net and Whisper.net.Runtime (1.9.1, MIT)** are dependencies of that library, for local transcription; the
  model downloads on first use into the editor's user data.
- **ffmpeg runs as a child process** (ADR 0190), for probing, waveform, silence detection, the preview's decoders and
  export. FFMpegCore is dropped: `ffprobe -of json` is parsed with System.Text.Json.
- **Projects are `.mfcut` files using Cutback's schema** (version 3, unchanged); `.cutback` files open and save as
  `.mfcut`.

## Alternatives considered

- **Separate workspaces per mode:** duplicates the shell (menus, dialogs, Output, settings, commands).
- **NuGet packages from Cutback's repository:** need a publish step and a cross-repository version dance, and suffer
  version skew between the app and the editor.
- **A Clips tab only** (video as one more tab type in the game layout): not a real mode; the timeline and transcript need
  the whole window.
- **G16 video playback for the preview:** it targets AV1/WebM playback in games, not frame-accurate scrubbing of H.264
  recordings in the editor; the preview decodes with ffmpeg instead.

## Consequences

- Three new managed dependencies, editor only: `MainframeEngine.Video`, `Whisper.net` and `Whisper.net.Runtime` (with
  per-platform natives, Metal on macOS). `THIRD_PARTY_NOTICES.md` lists the Cutback code, Whisper.net and whisper.cpp.
- The preview re-uploads its texture every displayed frame (the CPU `Texture2D` path recreates its GPU image); G16's
  `VideoTexture` could remove that later.
- The first transcription downloads a model (78 MB for tiny.en up to 1.5 GB for medium.en).
- Video mode works without a game project; recent videos are their own list.
- Crash recovery still covers scenes only, and files cannot be dragged in from the OS yet.
- Cutback keeps working: the recording dialog offers Open in Video Editor first and keeps Open in Cutback.
