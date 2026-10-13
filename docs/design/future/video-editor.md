# Proposal: Video editing mode in the editor

**Status:** ⬜ planned (spec agreed in chat 2026-10-13) · **Branch:** `feature/video-editor`, based on `main`, which has
Mainframe-Games/mainframe-engine#62 and #63 · **Depends on:** [Gameplay recording](../recording.md) (the recorder and
`.mfcap.json` sidecar, #62; Record from the editor, #63), [Song tab](../editor.md#song-tab-music-editor) (the canvas,
`SubViewport` and job patterns this copies), [Audio](../audio.md) (`AudioStreamGenerator`) ·
**Related:** [Video playback](video-playback.md) (G16, games only; this proposal does not use or replace it),
ADR 0190 (ffmpeg as a child process)

## Problem

The editor can record gameplay (#62, #63). To turn a recording into promo footage you then need a second app,
[Cutback](https://github.com/brogan89/Cutback), which:
- trims dead air and idle gameplay;
- transcribes speech and removes filler words;
- exports the result.

The goal is to do all of that inside the Mainframe Editor. The editor becomes a game-engine editor and a video editor,
with a DAW mode to follow later (not in this proposal). A mode toggle switches the whole editor UI between the two.

This is **one PR**. Cutback's logic is copied into the engine: Cutback.Core, Cutback.Media and Cutback.Analysis are
MIT, written by the same author, and sit in `/Users/brogan/dev/SimpleVideoEditor/src`. The Cutback repository and
its Avalonia app are not touched. This replaces the earlier plan to consume Cutback as NuGet packages (Phase 4a/4b of
Cutback's integration spec).

## Goals

- **A Game ↔ Video mode** for the whole editor:
  - each mode has its own layout, toolbar and menus;
  - the shell is shared: menu bar, dialogs, Output, settings and commands;
  - an `EditorMode` enum leaves room for `Audio` (DAW) later.
- **Every Cutback feature,** at Cutback's behaviour:
  - the timeline: kept and removed regions, gestures, waveform, markers;
  - Detect Silence and Detect Idle Gameplay;
  - Whisper transcription, the transcript panel, Remove Filler Words and Export Transcript;
  - Precise and Fast export;
  - snapshot undo.
- **A gapless preview** of the edit, decoded by ffmpeg child processes into an engine texture, with an engine audio
  voice as the clock.
- **Project files are `.mfcut`.** They use Cutback's project schema, so old `.cutback` files open too, and saving
  writes `.mfcut`.
- **Video mode works with or without a game project open.**

## Non-goals

- No multi-track timeline, transitions, titles, effects or music beds. Cutback's "not an NLE" rule applies.
- No DAW mode yet. The mode switch only has to leave room for it.
- No Claude analysis. Cutback's `ICutSuggester` is an unimplemented stub, and it is not ported.
- No OS file drag-and-drop onto the window, because the engine does not deliver SDL drop events yet.
- No change to games: neither the runtime nor shipped games reference any of this.
- No change to Cutback's repository.

## Decisions taken

| Question | Decision |
|---|---|
| Mode architecture | **One workspace, two layouts.** `EditorWorkspace.Mode` swaps the panel set and layout. A second workspace per mode was rejected because it duplicates the shell, and treating video as just a tab type was rejected because it gives no real mode. |
| How Cutback gets in | **Copy the source** into a new project, `MainframeEngine.Video`, in namespace `MainframeEngine.Video.*`. NuGet packages were rejected: they need a publishing step and a cross-repository version dance. |
| Scope | Everything, transcripts included. |
| Entering video mode | A Game\|Video toggle in the toolbar plus View › Video Editing Mode. Opening a recording or `.mfcut` switches to video mode automatically. |
| Project file | `.mfcut`, Cutback's JSON schema (version 3). `.cutback` is accepted on open. |
| Base branch | `main`, which has #62 and #63 merged, so the recording dialog can offer Open in Video Editor. |
| Probing | `ffprobe -of json`, parsed with System.Text.Json. FFMpegCore is dropped, saving a dependency. |
| Tests | Cutback's tests are ported to plain xunit `Assert` (engine convention). FluentAssertions is not added. |

## Code layout

![Where the code comes from](../../images/video-editor-architecture.svg)

```
MainframeEngine.Video/                    new library, net10.0, referenced only by MainframeEngine.Editor
  SegmentList.cs  SegmentEditor.cs  EditHistory.cs                 ← Cutback.Core root files (+ SegmentEditor, see below)
  Models/ Capture/ Detection/ Transcript/ Projects/ Playback/        ← Cutback.Core
  Media/   (FfmpegLocator, MediaProbe, WaveformExtractor, PcmExtractor, SilenceDetector, FfmpegProcess, …)
  Media/Export/ (Exporter, FilterGraphBuilder, KeyframeSnapper, DeliveryEncoder)
  Media/Preview/PreviewDecoding.cs
  Analysis/ (ITranscriber, WhisperTranscriber, WordAssembler, ModelStore, WhisperModel)
  LICENSE-CUTBACK                          the MIT notice the copied files came with
Tests/MainframeEngine.Video.Tests/        ported Cutback tests (plain Assert); never runs ffmpeg or loads a model
MainframeEngine.Editor/Src/Video/         the mode's UI (below)
MainframeEngine.Editor/Content/Editor/video*.rml
```

**Where each part comes from:**
- **Core:** copied file for file. The only changes are the namespaces and the project extension.
- **`SegmentEditor`:** the edit-session rules from Cutback's view model, written as a Core class so the UI never
  reimplements them:
  - one edit is one undo step, and an edit that changes nothing leaves none;
  - a boundary drag is one step;
  - a drawn section takes the opposite state of where it was anchored;
  - dirty means "differs from the last save".
- **`DeliveryEncoder`:** export's H.264 encoder choice, probe-encoded in order: libx264, then the platform's hardware
  encoder, then libopenh264. Export then works on an ffmpeg built without x264.
- **`PreviewDecoding`:** the decode arguments for the preview.
- **`MediaProbe`:** loses FFMpegCore. It runs `ffprobe -v error -print_format json -show_format -show_streams`.
- **`MainframeEngine.Video` dependencies:** System.Text.Json (in the framework), and `Whisper.net` plus
  `Whisper.net.Runtime`, pinned at 1.9.x like Cutback. Whisper.net is MIT and wraps whisper.cpp, which is MIT. Its
  runtime package brings native libraries for each platform, including Metal on macOS. This is the one new NuGet
  dependency, agreed in chat as the engine's CLAUDE.md requires.

**Editor side, `MainframeEngine.Editor/Src/Video/`:**

| Type | Role |
|---|---|
| `VideoDocument` | One project: `CutbackProject`, `SegmentList` + `SegmentEditor`, capture sidecar, transcript, waveform. Open (video or `.mfcut`/`.cutback`), save, detect, apply transcript and filler cuts. |
| `VideoTab : IEditorTab` | A document plus its timeline viewport, preview, view state and jobs. Shown only in video mode. |
| `VideoTimelineView` | Zoom, pan, hit-testing and ruler maths. Pure; ported constants from Cutback's `TimelineControl`. |
| `VideoTimelineCanvas` | A `Node2D` port of `TimelineControl`'s draw pass: segments, reasons, waveform, boundaries, pending section, ruler, markers, playhead. |
| `VideoTimelineController` | Timeline gestures (below). |
| `VideoPlaybackPlan` | Kept ranges from the playhead, laid end to end on an output timeline, mapped back to source time. Pure. |
| `VideoPreview` | ffmpeg decoders into a `Texture2D` in a `SubViewport`, and an `AudioStreamGenerator` voice as the clock. |
| `TranscriptPanel` | RmlUi word spans with the cut state, gestures and context menu. |
| `VideoJob<T>` | Background work polled each frame: waveform, silence detection, transcription, model download, export. |
| `VideoView` (`[Tool]` node) | Per-frame driver: places and publishes `engine://video-preview` and `engine://video-timeline`, routes input, polls jobs. |
| `VideoPanel` + `video.rml` | The video-mode chrome: toolbar, tab strip, preview, transcript, notice line, timeline. |
| `ExportVideoDialog` + `video_export.rml` | Output path, Precise/Fast, Export. |
| `VideoSettings` | Editor Settings › Video. |

## Modes

![One workspace, two modes](../../images/video-editor-modes.svg)

`EditorMode { Game, Video }` lives on `EditorWorkspace`. `Audio` is reserved for the DAW, and its name is not used
yet. `EditorWorkspace.SetMode(EditorMode)`:

1. **Swaps the layout.**
   - Game mode is today's editor.
   - Video mode hides the Scene tree, Inspector, FileSystem, Output (unless View › Output is on), the viewport panel
     and the song panel. It shows `VideoPanel` over the whole body below the toolbar.
   - Each mode remembers its own panel sizes.
2. **Swaps the menus.**
   - Game: File, Edit, View, Project, Run, Help, exactly as today.
   - Video: File, Edit, View, Video, Help.
   - The `MenuItems(menu)` tables become mode-aware. Command ids stay shared where the meaning is the same
     (`file.save`, `edit.undo`).
3. **Swaps the toolbar contents.** The Game|Video toggle sits at the toolbar's right edge in both modes.
4. **Filters the tab strip.**
   - Game mode shows scenes, songs and UI previews. Video mode shows `VideoTab`s.
   - Each mode remembers its active tab.
   - Tabs of the hidden mode stay open. Video tabs pause when hidden.
5. **Persists the mode** in editor settings (`EditorSettings.LastMode`), so the editor reopens in the last mode.

**Auto-switch.** These all switch to video mode before opening:
- opening a video (`.mp4 .mov .mkv .webm`), `.mfcut` or `.cutback` from File › Open, the FileSystem panel or the QA
  `open` verb;
- "Open in Video Editor" in the recording dialog.

**Without a project.** Video mode needs no project:
- It is reachable from the project manager with a "Video Editor" button.
- Recent videos are their own list (`EditorSettings.RecentVideos`, 10 entries).
- Opening a recording never calls `EditorSession.UseProjectOf`. Recordings live in the user-data folder, and switching
  projects would throw while scene tabs are open.

**Shortcuts per mode.**
- Video mode:
  - Space plays and pauses, Home goes to the start, and Delete/Backspace removes the hovered region;
  - Cmd/Ctrl+S saves, Cmd/Ctrl+Z undoes, Cmd/Ctrl+Shift+Z redoes, Cmd/Ctrl+E exports;
  - F10 (Play and Record) does nothing.
- Game mode is unchanged.

## The video-mode screen

![The editor in Video mode](../../images/video-editor-mode.svg)

- **Menus:**
  - **File:** Open… (Cmd+O), Open Recent ›, Save, Save As…, Export Video… (Cmd+E), Export Transcript…, Close.
  - **Edit:** Undo, Redo, Delete Region, Video Settings….
  - **View:** Transcript (toggle), Output (toggle), Video Editing Mode (toggle).
  - **Video:** Play/Pause, Transcribe, Remove Filler Words, Detect Silence, Detect Idle Gameplay.
- **Toolbar.** On the left, the transport and position (`m:ss.t / m:ss.t`). Then Transcribe, Remove Fillers, Detect
  Silence and Detect Idle:
  - Detect Idle is disabled without a sidecar; its tooltip says why.
  - Remove Fillers is disabled without a transcript.
  - A running job shows "Cancel n %" in its own button, and pressing it cancels the job.

  On the right: "Kept m:ss of m:ss", Export (with "Cancel n %" while exporting), and the Game|Video toggle.
- **Tab strip:** the editor's own tab strip, filtered to video tabs.
- **Panels:**
  - **Preview:** fitted to the video's aspect ratio, with black bars.
  - **Transcript:** on the right, 320 dp by default, resizable with a splitter. View › Transcript hides it. Before a
    transcript exists it says "No transcript yet. Transcribe to edit by text."
  - **Timeline:** at the bottom, 180 dp by default, resizable.
- **Notice line.** One line above the timeline. It holds open warnings (bad sidecar, source changed), the last
  operation's result ("Cut 3 stretches of idle gameplay, 0:42.1 removed.") and "ffmpeg not found". Those use Cutback's
  wording. The ffmpeg message comes with an "Editor Settings" link.
- **Empty state** (no video tab): "Open a recording (File › Open…) or record one in Game mode (F10)", plus the Recent
  Videos list.
- **Colours.** The timeline uses Cutback's palette as `Vector4` constants:

  | Colour | Value | Colour | Value |
  |---|---|---|---|
  | ruler | #18181C | body | #121215 |
  | kept | #1D2E4D | kept hover | #26406A |
  | removed | #1F1F24 | removed hover | #2A2A31 |
  | kept wave | #6FA3F0 | removed wave | #45454E |
  | boundary | #C8C8D2 at 0xB0 alpha | active boundary | #FFD166 |
  | section fill | #FFD166 at 0x38 alpha | playhead | #FF5C5C |
  | tick | #4E4E58 | label | #9A9AA6 |
  | reason | #7C7C88 | marker | #B48CFF |

  The chrome uses the editor's `theme.rcss`.

## Behaviour

### Preview and playback

![Gapless preview](../../images/video-editor-preview.svg)

- **Decoders.** `VideoPreview` runs a video decoder (raw RGBA) for each kept range, at most 960 px wide and 30 fps
  (`PreviewDecoding`). It runs an audio decoder (`f32le`, stereo, at `AudioServer.SampleRate`) one range after another.
- **Gapless video.** The next range's video decoder starts **1.0 s** before the current range ends, and frames switch
  at the boundary. Cutback's VLC preview hitches at every cut; this one does not.
- **Gapless audio.** All audio goes into one `AudioStreamGenerator` voice with a 1.0 s ring:
  - It plays on the master bus at `int.MaxValue` priority with `ProcessMode.Always`.
  - Each range is pushed after the previous one, so the audio has no gap, and one producer keeps the ring's
    single-producer rule.
  - The audio decode gets Cutback's 8 ms fade at every splice, so the preview sounds like the export.
- **Clock.** The voice's `FramesConsumed` is the clock, in output seconds. `VideoPlaybackPlan` maps output time back to
  source time for the playhead and the frame to show. With no audio device, a stopwatch is the clock.
- **Edits while playing.** The plan is rebuilt from the playhead whenever the document's version changes, so a newly
  cut region is skipped.
- **Stopping.** Playback stops at the end of the last kept range. It also pauses when the tab is hidden, the mode
  switches or the tab closes. Closing the tab also kills every decoder.
- **Scrubbing while paused** decodes one frame (`-ss t -frames:v 1`). If several requests arrive, only the newest
  runs.
- **Texture and size.** One `Texture2D` in its own `SubViewport` is published as `engine://video-preview`. It is
  updated with `SetPixels` at most once per displayed frame. The preview `<img>` is sized in code to fit its area at
  the video's aspect.
- **Failures.** A failed decoder logs `[Video] Preview decode failed: …` and stops playback. A preview problem never
  opens a dialog.

### Timeline

These gestures are Cutback's, with Cutback's constants. The ruler is 22 dp; a boundary grab is within 6 dp; a drag
starts after 4 dp; the minimum view is 0.25 s; snapping looks within 20 ms; wheel zoom is 1.25 per notch; the ruler's
labels are at least 80 dp apart.

- **Click** a region to toggle it. It becomes manual.
- **Drag a boundary** to move it. It snaps to the quietest waveform sample on release, and the whole drag is one undo
  step.
- **Drag in the body** to draw a section with the opposite state of where the drag started.
- **Shift+drag** pans.
- **Press or drag the ruler** to scrub.
- **The wheel** zooms about the cursor. Shift+wheel or a horizontal wheel pans.
- **Right-click** › Delete, or **Delete/Backspace** over a region, dissolves it into its neighbours.
- **While playing,** the view follows the playhead. It re-centres when the playhead leaves the view.
- **Losing the pointer** mid-drag ends a boundary drag where it is, as one undo step, and drops a section being drawn.

### Transcripts

- **Transcribe** runs `WhisperTranscriber` in a `VideoJob`:
  1. It extracts 16 kHz PCM with ffmpeg.
  2. If the model is missing, it downloads it into `{editor user data}/models` first. The button shows "Downloading
     model n %"; this is the editor's only network access for video.
  3. It runs Whisper with Cutback's disfluent prompt and DTW anchors.
  4. The result is stored in the project's `transcript` and saved in the `.mfcut`.
- **Model choice.** The model is set in Video Settings, from tiny.en (78 MB) to medium.en (1.5 GB). The default is
  base.en (148 MB).
- **Transcript panel.** Paragraphs break after 2 s pauses. One `<span>` per word, and a word is struck through when it
  is cut (`TranscriptView.IsCut`). The panel follows the playhead and highlights the current word.
  - **Click** a word to cut it, or restore it if struck.
  - **Drag across words** to change the run to the opposite state of the first word.
  - **Cmd/Ctrl+click** plays from the word.
  - **Right-click** offers Play from here and Cut/Restore.
  - Word edges snap within ±40 ms, and every edit is one undo step (`SetRange`, manual).
- **Remove Filler Words** runs `FillerDetector` → `FillerSpanLocator` (envelope bursts around DTW anchors) →
  `FillerCutPlanner` → `ApplyFillerCuts`, as one undo step. It never touches manual edits. The filler word list is in
  Video Settings, with Cutback's defaults.
- **A transcript made before anchors existed** still works with heuristic spans. The notice line suggests transcribing
  again.
- **Export Transcript…** writes `.txt` or `.srt` (`TranscriptExporter`), with times remapped by `OutputTimeline` to
  match the exported video.

### Detection

- **Detect Silence** runs ffmpeg's `silencedetect` in a `VideoJob`, then `SilenceCutPlanner` →
  `ReplaceAutoSegments`, as one undo step that keeps every non-auto segment. Its settings are padding 60 ms,
  min silence 400 ms, threshold −34 dB and min keep 120 ms, from Video Settings.
- **Detect Idle Gameplay** reads the `.mfcap.json` sidecar: `GameplayCutPlanner` → `ReplaceOriginCuts(Gameplay)`, as
  one undo step. Its settings are min idle 3000 ms, padding 500 ms, min keep 1000 ms and marker window 1500 ms. The
  rules are Cutback's:
  - idle is detected only in live recordings;
  - loading, pause and Skip are cut exactly;
  - Keep is never cut;
  - markers protect the footage around them.
- The results line uses Cutback's wording.

### Export

- **Export Video…** opens `ExportVideoDialog`:
  - The output path defaults to `<video>-cut.mp4` next to the source, numbered if taken; Browse opens the editor's save
    picker. The format follows the extension: `.mp4 .mov .mkv .webm`.
  - **Precise** (the default) re-encodes. The encoder comes from `DeliveryEncoder`: libx264 at CRF 20 when present,
    else hardware or OpenH264 at 0.1 bit per pixel per frame. Audio is AAC 192k, and the filter graph goes in a file.
  - **Fast** stream-copies with keyframe-snapped starts. It is labelled "approximate: cuts snap back to keyframes" and
    is not offered for `.webm`.
- **Running.** Export runs in a `VideoJob` with "Cancel n %" in the Export button. Cancelling deletes the partial file.
- **Finishing.** Success shows "Export finished" with Show in Folder and Close. Failure shows the ffmpeg error, with
  the tail of stderr.
- **Closing a tab** cancels its export.

### Opening, saving, undo

- **Opening a video:**
  - It is probed, and refused with "has no audio track" if it has none, since export splices audio.
  - It is hashed (Cutback's `SourceHash`) and starts as one kept segment.
  - The sidecar is read when present. A bad or newer sidecar is a notice-line warning, never a failed open.
- **Opening a `.mfcut` or `.cutback`:**
  - It uses the saved source info and is not re-probed.
  - It is refused, naming the path, when the source is missing.
  - It opens with a warning when the source's hash changed.
  - A `.cutback` opens as unsaved under the name `<name>.mfcut`. The `.cutback` is never overwritten.
- **Opening a file that is already open** activates its tab.
- **Save:**
  - It writes the project atomically.
  - An unsaved recording asks where, suggesting `<video dir>/<video name>.mfcut`.
  - Save As can move the project.
- **Undo** is `SegmentEditor`: one edit is one step, a boundary drag is one step, and undoing back to the saved
  partition is clean. The limit is 100 steps, set in Video Settings (10–1000).
- **Unsaved changes.** The tab title shows `*`. Close and Quit ask Save / Don't Save / Cancel, through the editor's
  existing unsaved-changes flow.

### Settings (Editor Settings › Video)

| Setting | Default | Range |
|---|---|---|
| ffmpeg | blank: `MAINFRAME_FFMPEG`, then PATH, then the usual install folders | a folder holding ffmpeg and ffprobe, or one of the two files |
| Whisper model | base.en | tiny.en, base.en, small.en, medium.en |
| Filler words | Cutback's list | comma-separated |
| Silence: threshold / min silence / padding / min keep | −34 dB / 400 / 60 / 120 ms | Cutback's clamps |
| Gameplay: min idle / padding / min keep / marker window | 3000 / 500 / 1000 / 1500 ms | Cutback's clamps |
| Undo history | 100 | 10–1000 |

The ffmpeg setting is also used by Play and Record: the editor passes `--ffmpeg` to the game when it is set.

## File format: `.mfcut`

The format is Cutback's project JSON, schema version 3, unchanged:

```jsonc
{ "version": 3,
  "source": { "path": "/abs/recording.mp4", "sha256": "…", "durationSeconds": 612.44, "width": 1920, "height": 1080, "frameRate": 60.0 },
  "segments": [ { "id": "…", "start": 0.0, "end": 3.21, "enabled": true, "origin": "auto", "reason": null } ],
  "transcript": [ { "text": "so", "start": 1.02, "end": 1.18, "confidence": 0.93, "anchor": 1.05 } ],
  "settings": { "paddingMs": 60, "minSilenceMs": 400, "silenceThresholdDb": -34.0, "minKeepMs": 120 } }
```

- **Segments partition `[0, duration]`.** This invariant is enforced and tested (`SegmentList`).
- **`origin`** is one of `auto | manual | claude | filler | gameplay`. Each detector replaces only its own cuts, and
  manual edits survive every detector.
- **Paths.**
  - The source path is absolute. The source is opened read-only and never modified.
  - The `.mfcut` lives next to the video by default, which is usually the recordings folder in user data. A `.mfcut`
    inside a project folder appears in the FileSystem panel with a "video" icon.
- **Compatibility.** The engine owns the format from now on. A `.mfcut` renamed to `.cutback` opens in Cutback today,
  but that is not promised.

## Licensing

- **The copied code is MIT.**
  - Cutback.Core and Cutback.Media are already MIT, each with its own `LICENSE`.
  - Cutback.Analysis, and the Cutback.App code this ports (`TimelineControl`'s draw pass and gestures,
    `TranscriptControl`'s gestures, the view model's open, detect and export flows), are GPL-3.0 in Cutback's
    repository today.
  - Cutback's sole author relicensed those ported parts as MIT for this copy, confirmed in chat on 2026-10-13. The GPL
    Cutback app itself is not touched.
  - The port reimplements the UI parts on the engine's canvas and RmlUi; it does not copy Avalonia code.
- **`MainframeEngine.Video/LICENSE-CUTBACK`** keeps the original copyright line. `THIRD_PARTY_NOTICES.md` gains three
  rows:
  - the Cutback code, MIT, at `MainframeEngine.Video`, shipped in `MainframeEngine.Video.dll` (editor only);
  - Whisper.net 1.9.x, MIT, a NuGet package, shipped in `Whisper.net.dll` plus native runtimes (editor only);
  - whisper.cpp, MIT, via the Whisper.net runtime (editor only).
- **ffmpeg** remains the user's, run as a separate program and never linked (ADR 0190). Fast export and precise export
  both just run it.
- **Shipped games are unaffected.** `MainframeEngine` never references `MainframeEngine.Video`.

## Testing

- **`Tests/MainframeEngine.Video.Tests`.** Cutback's Core, Media and Analysis tests (about 4,300 lines) are converted
  to plain `Assert`. They cover:
  - the partition invariant, `SetRange`, `Dissolve`, `Restore` and `EditHistory`;
  - the silence, filler and gameplay planners, `ReplaceOriginCuts`, and the sidecar reader;
  - project round-trips and migration, and source hashing;
  - the transcript view and exporters, and the output timeline;
  - the locator, peaks, parsers, capabilities and filter graphs, and keyframe snapping;
  - word assembly and model metadata.

  New tests cover `SegmentEditor`, `DeliveryEncoder`, `PreviewDecoding` and the ffprobe JSON parser. Nothing runs
  ffmpeg or loads a model.
- **Editor tests:**
  - **Modes:** layout, menus, the tab filter, persistence, and auto-switch on open.
  - **`VideoTab` in the session:** open, save `.mfcut`, import `.cutback`, the close prompt, and undo.
  - **Timeline:** the view maths, the gestures and the column maths.
  - **Playback:** the plan, and the preview with a fake decoder factory (kill on pause, close and mode switch; rebuild
    on edit).
  - **Transcript panel:** word click and drag map to `SetRange`.
  - **Dialogs and settings:** the export dialog defaults, and the Video Settings clamps.
  - **The recording dialog's** Open in Video Editor.
- **Render golden:** video mode's empty state on MoltenVK and lavapipe. It needs no ffmpeg, so CI checks it.
- **QA, run by hand:** `Tests/QA/video.qa` with a committed 12 s fixture recording, made by
  `build/make-video-fixture.sh`. The fixture has a tone that drops out for 2 s, a sidecar with a loading stretch, and
  a marker. The script:
  1. opens the fixture;
  2. runs Detect Idle Gameplay and Detect Silence;
  3. transcribes with tiny.en, then runs Remove Filler Words;
  4. edits on the timeline and in the transcript;
  5. plays;
  6. exports.

  It captures each step for review by eye.
- **Gates:**
  - `just test`;
  - the Release `dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false`;
  - `just test-render`;
  - `just format-check`.

## Docs

- **ADR 0191, "Video editing mode in the editor".** It records:
  - why video editing lives in the editor;
  - the one-workspace mode design;
  - copying the Cutback code instead of depending on packages;
  - Whisper.net as a dependency;
  - the rejected alternatives: separate workspaces, packages, and G16 playback.
- **Design docs.** `docs/design/video-editor.md` is this document once the work lands, as built. `editor.md` gets an
  "Editor modes" section and a pointer.
- **Elsewhere.**
  - `recording.md`: the dialog's buttons.
  - `CLAUDE.md`: the video mode, `MainframeEngine.Video`, and the never-shell-out-in-tests rule.
  - `THIRD_PARTY_NOTICES.md`.
  - The progress log.

## Acceptance

1. **The whole flow happens in the editor.** Record (F10), then Open in Video Editor, then Detect Idle Gameplay,
   Transcribe and Remove Filler Words, then edit on the timeline and in the transcript, then Export. The exported MP4
   plays, without the removed stretches.
2. **The toggle round-trips cleanly.** Switching Game ↔ Video keeps both modes' tabs and layout, and pauses playback.
3. **A `.mfcut` saves and reopens identically.** An old `.cutback` opens.
4. **The preview has no gap or hitch at cuts.** Check by eye in the QA run.
5. **The gates are green,** with the empty-state golden on both drivers.

## Known limits accepted

- **Preview cost.** The preview re-uploads a texture once per displayed frame, at most 960×540 and 30 fps. The CPU
  `Texture2D` path recreates the GPU image each time; G16's planned `VideoTexture` would remove that later.
- **No OS drag-and-drop** of files onto the window.
- **Whisper is trained to drop disfluencies.** Recall of "um"s depends on the prompt. This is Cutback's known issue,
  with the same mitigation.
- **A large first download.** The first transcription downloads 78 MB to 1.5 GB, depending on the model.

## Open questions

None blocking.
