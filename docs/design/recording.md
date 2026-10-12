# Gameplay recording

## Purpose

Records a running game to an H.264 `.mp4` plus a `.mfcap.json` **sidecar** that says what the engine knew while it
recorded: when the player gave input, when the game was loading or paused, and what the game marked as worth keeping or
cutting. [Cutback](https://github.com/brogan89/SimpleVideoEditor) (and later the editor's Clips tab) reads the sidecar to
trim idle play, loading and pauses. Two modes:

- **Live** (`--record file.mp4`): real time. Frames are resampled to a constant rate, audio is tapped from the master
  mix, and the player keeps playing and hearing the game.
- **Offline** (`--record file.mp4 --fixed-fps 60`): every update is exactly one output frame, and the audio is rendered from the manual null
  audio device so that `round(n · sampleRate / fps)` samples exist through frame `n` (exact totals for any fps). A slow
  4K render therefore still plays back smoothly and in sync. The player hears nothing, by design.

The engine never links, bundles or P/Invokes an encoder: frames go to the stdin of the user's own **ffmpeg** run as a
child process (found by `FfmpegResolver`; without it `--record` fails with an install hint). Decision and rejected
alternatives: [ADR 0190](../../memory/decisions/0190-gameplay-recording-ffmpeg-child-process.md). The renderer half
(frame stream) is in [Vulkan renderer](vulkan-renderer.md) and the audio half (capture tap) in [Audio](audio.md#architecture);
this document does not repeat them.

Command line (documented in `GameHostOptions` and [Projects & GameHost](project-and-gamehost.md)):

| Flag | Effect |
|---|---|
| `--record <file.mp4>` | Record until quit or `Recorder.Stop()`. Implies frame capture. Offline when `--fixed-fps` is also given, else live. |
| `--record-scale <n>` | `EngineOptions.ContentScale`: pixels per point of the window size (a 1920×1080-point window at 2 is 3840×2160). Needs a display big enough for the scaled window, see [Known issues](#known-issues). |
| `--ffmpeg <path>` | The ffmpeg to use; else `MAINFRAME_FFMPEG`, then `PATH`, then common install folders. |

Game code uses `SceneTree.Recorder` (`GameRecorder`; null under `--headless`): `Start(RecordingOptions)`, `Stop()`,
`Mark(label)`, `Keep(label)` / `Skip(label)` (scopes, `IDisposable`), `IsRecording`, `Elapsed`, and the `Started` /
`Stopped` events. `Mark`, `Keep` and `Skip` are no-ops when not recording, so games call them unconditionally. The dev
overlay is hidden when a recording starts (F12 brings it back, into the recording); a game that wants a clean feed hides
its own HUD on `Started` (the Forest's FPS readout does, and returns on `Stopped`).

## Key types

All internal except `GameRecorder`, `RecordingOptions`, `RecordingResult` and `FfmpegNotFoundException`.

| Type | File | Notes |
|---|---|---|
| `GameRecorder` (+ `CaptureInfoSource`) | [Recording/GameRecorder.cs](../../MainframeEngine/Src/Recording/GameRecorder.cs) | the state machine; `IFrameStreamSink`; owns the clock, the timeline and the audio path; finalises in the background |
| `RecordingOptions`, `RecordingResult` | [Recording/RecordingOptions.cs](../../MainframeEngine/Src/Recording/RecordingOptions.cs), [IRecordingBackend.cs](../../MainframeEngine/Src/Recording/IRecordingBackend.cs) | path + fps in; path, duration, dropped frames and an error (null on success) out |
| `FrameScheduler` | [Recording/FrameScheduler.cs](../../MainframeEngine/Src/Recording/FrameScheduler.cs) | pure; how many times each rendered frame is written so the output is constant frame rate (live: `floor(t·fps) − written + 1` copies, 0 drops it; offline: one in, one out) |
| `SampleScheduler` | [Recording/SampleScheduler.cs](../../MainframeEngine/Src/Recording/SampleScheduler.cs) | pure; `round(n · sampleRate / fps)` samples through frame `n`, so the total never drifts for rates that do not divide |
| `CaptureTimeline` | [Recording/CaptureTimeline.cs](../../MainframeEngine/Src/Recording/CaptureTimeline.cs) | pure; builds the sidecar's spans from per-frame flags and events: input (250 ms tail, held controls keep it open, gaps under 100 ms rejoin), loading, paused, keep, skip, markers |
| `InputActivity` | [Recording/InputActivity.cs](../../MainframeEngine/Src/Recording/InputActivity.cs) | classifies input events and tracks held controls; `Reset()` when the window loses focus |
| `CaptureSidecarWriter`, `CaptureInfo`, `CaptureSpans` | [Recording/CaptureSidecarWriter.cs](../../MainframeEngine/Src/Recording/CaptureSidecarWriter.cs) | the `.mfcap.json` writer (source-generated `System.Text.Json`); written through a temp file and a move |
| `IRecordingBackend`, `RecordingFormat` | [Recording/IRecordingBackend.cs](../../MainframeEngine/Src/Recording/IRecordingBackend.cs) | where frames and audio go, so the pacing logic never depends on a child process |
| `FfmpegRecordingBackend` | [Recording/FfmpegRecordingBackend.cs](../../MainframeEngine/Src/Recording/FfmpegRecordingBackend.cs) | the only backend: encoder child process + writer thread + WAV + mux + sidecar |
| `FfmpegResolver`, `EncoderSelector`, `FfmpegArgs`, `FfmpegProcess` | [Recording/](../../MainframeEngine/Src/Recording/) | ffmpeg lookup, hardware-first encoder choice, the command lines (argument lists, never a shell string), the process wrapper |
| `StreamingWavWriter` | [Recording/StreamingWavWriter.cs](../../MainframeEngine/Src/Recording/StreamingWavWriter.cs) | 32-bit float WAV written as the recording runs (`WavWriter` needs the whole buffer); padded or truncated to the exact length at `Finish` |
| `IFrameStreamSource`, `IFrameStreamSink` | [Rendering/IFrameStream.cs](../../MainframeEngine/Src/Rendering/IFrameStream.cs) | the renderer's presented-frame stream (implemented by `VulkanRenderer`) and the recorder's receiving end |
| `AudioCaptureTap` | [Audio/Graph/AudioCaptureTap.cs](../../MainframeEngine/Src/Audio/Graph/AudioCaptureTap.cs) | a copy of every master-mix block into a lock-free ring (live mode) |

## Architecture

```mermaid
flowchart LR
    subgraph Main["Main thread"]
        GH["GameHost.OnUpdate<br/>Recorder.OnUpdate(loading, paused)"] --> TL["CaptureTimeline"]
        IN["SceneTree.DispatchInput"] --> IA["InputActivity"] --> TL
        R["VulkanRenderer<br/>EndFrame copy → BeginFrame delivers"] -- "IFrameStreamSink.OnFrame" --> GR["GameRecorder<br/>FrameScheduler"]
        GR --> BE["FfmpegRecordingBackend"]
    end
    subgraph Audio["Audio thread"]
        MIX["AudioMixRoot (after the fault guard)"] -- "AudioCaptureTap.Write" --> RING["SPSC ring"]
    end
    RING -- "Drain in OnUpdate / Stop" --> GR
    BE -- "bounded queue of 4 pooled buffers" --> W["Video writer thread"] --> ENC["ffmpeg encoder<br/>stdin → video.mkv"]
    BE -- "StreamingWavWriter" --> WAV["audio.wav"]
    subgraph Fin["Thread-pool task (Stop)"]
        MUX["pad WAV · mux to MP4 · sidecar · remove .partial"]
    end
    ENC --> MUX
    WAV --> MUX
    TL --> MUX
```

- **Start.** `--record` starts on the **first update** (`GameHost.OnUpdate`, just before the session starts), so the time
  between creating the window and showing it stays out of the video, and the start scene's **loading is recorded**
  (as `loading` spans). Consequently an offline video is longer than `--max-frames`, which counts loaded frames only.
  `Start` resolves ffmpeg, picks and probes the encoder, creates `<out>.partial/`, starts the encoder and the writer
  thread, attaches the audio tap (live) and begins the frame stream. A recording that cannot start makes a `--record`
  run exit with an error rather than play unrecorded.
- **Clock.** Live: seconds of a monotonic `Stopwatch` started by `Start` (never game time). Offline: `frameIndex / fps`
  exactly. Every sidecar time is on this clock, which is the output video's timeline.
- **Video.** The renderer copies the presented swapchain image into one host-visible buffer per frame in flight (two).
  `BeginFrame` waits the slot's own fence before reusing it, which proves the copy finished, so the frame is read there:
  recording adds a copy, never a stall. `FrameScheduler` is asked before the copy is recorded, so a frame that would be
  dropped is never copied. The recorder copies the pixels into a pooled buffer and queues it (4 deep). Live and the
  queue is full: the frame is dropped and counted (`droppedFrames`), and the next frame written gets the missing
  copies, so length and sync hold and only smoothness suffers. Offline: the game thread waits for room, which is
  harmless because game time does not move. If the swapchain changes **size** the stream ends and so does the
  recording ("window size changed"); a same-size recreation (VSync toggle) keeps it. Details:
  [Vulkan renderer](vulkan-renderer.md).
- **Odd dimensions.** yuv420p needs even sizes, so an odd width or height is cropped by one pixel in the encoder
  (`crop` at the head of the `-vf` chain); the sidecar's `width`/`height` are the swapchain's, the video's are the even ones.
- **Colour.** ffmpeg's implicit `bgra` → `yuv420p` conversion uses BT.601 and leaves the stream untagged, while players
  assume BT.709 for HD, so reds and greens would shift. The encoder converts explicitly
  (`scale=out_color_matrix=bt709:out_range=tv,setparams=color_primaries=bt709:color_trc=bt709`) and tags the output
  (`-colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv`); the tags survive the mux. `setparams` is
  needed because the filter graph's frame properties override the output options, and `scale` sets only the matrix and
  range. Verified with ffmpeg 9.0.2: a pure red frame decodes to Y 63, Cb 102, Cr 240 (BT.709 limited), not 81/90/240.
- **Live audio.** The tap sits after the mixer's fault guard, so it records exactly what the device plays (silence after
  a fault) and takes **whole blocks only**: a block that does not fit the ring (about 2.7 s of 48 kHz stereo) is
  dropped, counted in `OverflowSamples`, and the recorder writes that much silence so the length still matches. The
  first block's `Stopwatch` timestamp aligns the audio to `t0`: the gap before it is silence. The recorder requires
  a stereo mix. See [Audio](audio.md#architecture).
- **Offline audio.** The host starts the engine with `AudioDeviceMode.NullManual`; after each update the recorder
  calls `AudioServer.RenderNullDevice` for exactly the samples `SampleScheduler` says that frame owes. With no audio
  server the WAV is silence of the right length.
- **Input.** `SceneTree.DispatchInput` hands every event to the recorder before the UI can consume it (menus clicked
  while recording are still play). Keys, mouse and gamepad buttons, actions (`InputEventAction`, held by name), text,
  the wheel, mouse motion over 2 px and a stick or trigger beyond 0.25 count as input; so does any event type the
  recorder does not know, because a missed input cuts footage the player was acting in while a false positive only
  keeps a little idle time. A held control keeps a span open, so walking with W down is never idle.
  `InputActivity.Reset()` runs when the window loses focus, because a key released in another window never sends its
  key-up.
- **Loading and pause** are polled once per update (`SceneTree.IsLoading`, `SceneTree.Paused`) into spans.
- **Encoding.** `EncoderSelector` lists `ffmpeg -encoders` and takes the first candidate that passes a probe encode:
  macOS `h264_videotoolbox`; Windows `h264_nvenc`, `h264_qsv`, `h264_amf`, `h264_mf`; Linux `h264_nvenc`; then
  `libx264` (`-preset ultrafast -crf 14`) and `libopenh264` everywhere. Hardware encoders and `libopenh264` get a
  bitrate of `width · height · fps / 4` bits/s (about 31 Mbit/s at 1080p60); the GOP is one second (`-g fps`) so a
  trimming tool never moves a cut more than a second. The result is a high-quality intermediate, not a delivery file.
  The result is probed once per process and ffmpeg path and cached.
- **Files.** While recording: `<out>.partial/video.mkv` (MKV, so a crash leaves a playable file) and
  `<out>.partial/audio.wav`. At `Stop` the WAV is **padded or truncated to the video's exact sample count**
  (`SampleScheduler.SamplesThrough(frames, sampleRate, fps)`) in both modes, then
  `ffmpeg -i video.mkv -i audio.wav -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 192k -movflags +faststart <out>` muxes, the sidecar is
  written, and `.partial/` is deleted. On failure `.partial/` is kept and `RecordingResult.Error` names it.
  A recording that captured **zero frames is abandoned**: no mux and no sidecar (a sidecar with
  `durationSeconds` 0 is rejected by Cutback), and `Error` says so.
- **Failure policy.** Nothing the recorder does may take the game down: a failing encoder, a full disk or a resize ends
  the recording with a readable `RecordingResult.Error`, never an exception from the renderer, the audio path or the
  update. Only `Start` throws. After a failure the next update (or the `Stop` in progress) ends the recording with the
  first reason. A **live** run logs the error and keeps playing; an **offline** `--record` run (a capture script or CI
  job whose whole purpose is the file) whose recording ends with an error exits **non-zero**
  (`GameHost.ShouldQuitOnRecordingError`).
- **Shutdown.** `GameHost.OnClose` stops the recording and waits up to two minutes for finalising, while the renderer
  and the audio server are still alive.

### Threading

| Thread | What runs |
|---|---|
| Main | `GameRecorder` (start, stop, `OnUpdate`, `Mark`/`Keep`/`Skip`), the renderer's frame stream and its sink callbacks, `CaptureTimeline`, input observation, draining the audio tap, offline audio rendering, raising `Started`/`Stopped` |
| Audio | `AudioCaptureTap.Write` only: one `EnqueueBatch` into the ring (lock-free, allocation-free; the audio thread's guarantees in [Audio](audio.md) hold) |
| Video writer | `FfmpegRecordingBackend.WriteFrames`: takes frames off the bounded queue and writes them to the encoder's stdin; keeps draining (without writing) after a failure so a producer blocked on a full queue is released |
| Thread-pool task | Finalising after `Stop`: finish the encoder, pad the WAV, mux, sidecar, cleanup. `Stop` returns without waiting for the encode and mux (it flushes the frame stream, drains audio and, live, waits about 20 ms for the last audio block); the next update raises `Stopped` on the main thread. A new `Start` throws until then, because the previous recording still owns its files |

## Sidecar format

`<video file name>.mfcap.json`, next to the video (`forest-20261012-143005.mp4.mfcap.json`). It is written atomically
(temp file and rename) after the mux succeeds. All times are seconds on the output video's timeline.

```jsonc
{
  "format": "mainframe-capture",
  "version": 1,
  "video": "forest-20261012-143005.mp4",   // file name, relative to the sidecar
  "engineVersion": "0.42.0",
  "game": "Forest",
  "scene": "res://Scenes/Forest.scn",
  "mode": "live",                          // "live" | "offline"
  "frameRate": 60,
  "width": 1920,
  "height": 1080,
  "durationSeconds": 60.0,
  "droppedFrames": 0,
  "input":   [ { "start": 0.0,  "end": 8.0 } ],
  "loading": [ { "start": 14.5, "end": 19.0 } ],
  "paused":  [ { "start": 56.0, "end": 60.0 } ],
  "keep":    [ { "start": 32.0, "end": 35.0, "label": "cutscene" } ],
  "skip":    [ ],
  "markers": [ { "time": 26.0, "label": "boss intro" } ]
}
```

Rules:

- Every span list is sorted and non-overlapping within itself. Lists may overlap each other.
- Unknown fields are ignored. A higher `version` is a new contract, and readers refuse it with a clear message.
- The engine owns the format. The canonical example is
  `Tests/MainframeEngine.Tests/Recording/Fixtures/sample.mfcap.json`, and it **must stay byte-identical** to the copy in
  Cutback's tests. Both test suites parse it, so drift fails CI on both sides. Change the format only by changing both.

## Performance

- Streaming adds one GPU copy of the presented image per recorded frame and no fence stall: two readback buffers (one per
  frame in flight), each read after the fence wait `BeginFrame` already does. Frames the scheduler would drop are
  never copied.
- On the CPU each recorded frame is one copy into a pooled (`ArrayPool`) buffer; the encoder pipe is written from the
  video writer thread, so a slow encoder shows up as dropped frames (live) or a waiting game thread (offline), not as
  an unbounded queue (the queue holds 4 frames).
- The audio callback stays lock-free and allocation-free; a full ring drops whole blocks and counts them.
- Verified on a Mac (`build/forest-record.sh`, `h264_videotoolbox`): an offline Forest flyover at 1920×1080 wrote
  4039 frames (7.317 s of loading plus exactly 60.000 s), audio 67.316667 s, equal to frames / 60; a live autowalk
  of 22.2 s dropped 2 frames; both sidecars were valid. The script needs ffmpeg and ffprobe on `PATH`, an awake display
  and the LFS art, so it runs locally, not in CI.
- The tests under `Tests/MainframeEngine.Tests/Recording/` never run ffmpeg: schedulers, timeline, argument builders, the
  WAV writer, the sidecar against the fixture, the audio tap and `GameRecorder` itself.

## Known issues

- **`--record-scale` is display-limited.** A fixed content scale needs a window of `WindowSize × scale` pixels, and the
  engine refuses to start when the display cannot hold it: a 1920×1080-point window at scale 2 fails on a smaller display.
  4K offline capture is therefore display-dependent (verified on the development Mac); `ContentScale` does not give
  4K on any display.
- **Any window resize ends a recording** ("window size changed"; the footage so far is kept). A game that resizes
  after startup, such as the Forest's `--resolution` dev flag, ends a `--record` run, and `build/forest-record.sh`
  avoids it.
- **Live audio runs on the device clock**, video on the monotonic clock. Drift is not corrected (the length is fixed to
  the video's at `Stop`).
- After a long stall (longer than the ring, about 2.7 s), the overflow silence is written after the audio that was
  drained, so a few milliseconds of sound can land early; the total length stays exact.
- A synchronous start-scene load (`--sync-load`) is not marked as a `loading` span in live mode.
- The first recording in a process **probes the encoders**, up to about 1 s on the game thread, and the probes have no
  timeout.
- WAV sizes clamp past about 3 hours (the header's 32-bit sizes); ffmpeg still reads to the end of the file.
- `--headless --record` records nothing: `HeadlessHost` has no recorder.
- `StartRecording` from the editor mid-session (Phase 3) needs the game launched with `--frame-capture`, because the
  swapchain needs `TRANSFER_SRC`.
- Recording requires a stereo audio mix; a device that mixes other channel counts ends a live recording with an error.

## Related docs

[Vulkan renderer](vulkan-renderer.md) (frame stream) · [Audio](audio.md) (capture tap) ·
[Projects & GameHost](project-and-gamehost.md) (host flags) · [Engine lifecycle](engine-lifecycle.md) ·
[Testing](testing.md) · [ADR 0190](../../memory/decisions/0190-gameplay-recording-ffmpeg-child-process.md)
