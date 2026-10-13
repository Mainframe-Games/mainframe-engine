using MainframeEngine.Audio;

namespace MainframeEngine.Tests.Recording;

/// <summary>Start/Stop lifecycle, sidecar contents and failure handling, with a fake backend and frame source.</summary>
public sealed class GameRecorderTests
{
    [Fact]
    public void OfflineRecordingWritesOneFramePerUpdateAndExactAudio()
    {
        var (r, backend, frames, _, _) = RecorderFakes.Create(offline: true);
        r.Start(new RecordingOptions { Path = "/r/out.mp4" });
        for (var i = 0; i < 60; i++)
        {
            r.OnUpdate(loading: false, paused: false);
            frames.Render();
        }

        var result = r.StopAndWait(TimeSpan.FromSeconds(5))!;

        Assert.Null(result.Error);
        Assert.Equal(60, backend.VideoFrames);
        Assert.Equal(48_000, backend.AudioFrames);      // silence when there is no audio server, still exact
        Assert.Equal(1.0, backend.Info!.DurationSeconds);
        Assert.True(backend.Info.Offline);
    }

    [Fact]
    public void OfflineAudioIsRenderedFromTheManualNullDevice()
    {
        using var audio = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        var (r, backend, frames, _, _) = RecorderFakes.Create(offline: true, audio, hostFps: 30);
        r.Start(new RecordingOptions { Path = "/r/out.mp4", Fps = 30 });
        for (var i = 0; i < 30; i++)
        {
            r.OnUpdate(loading: false, paused: false);
            frames.Render();
        }

        r.StopAndWait(TimeSpan.FromSeconds(5));

        Assert.Equal(audio.SampleRate, backend.AudioWritten); // one second, every sample from the mix, none padded
        Assert.Empty(backend.Silences);
    }

    [Fact]
    public void OfflineRecordingsAlwaysUseTheHostsFixedFps()
    {
        // Offline, one update is one output frame: any other rate would play the game too fast or too slow.
        var (r, backend, frames, _, _) = RecorderFakes.Create(offline: true, hostFps: 30);
        var sink = new MemoryLogSink { Levels = Log.Level.Warning };
        Log.AddSink(sink);
        try
        {
            r.Start(new RecordingOptions { Path = "/r/out.mp4" }); // the default 60
        }
        finally
        {
            Log.RemoveSink(sink);
        }

        Assert.Equal(30, backend.Format!.Fps);
        Assert.Contains(sink.Snapshot(), e => e.Message.Contains("--fixed-fps 30", StringComparison.Ordinal));
        for (var i = 0; i < 30; i++)
        {
            r.OnUpdate(loading: false, paused: false);
            frames.Render();
        }

        Assert.Equal(1.0, r.StopAndWait(TimeSpan.FromSeconds(5))!.DurationSeconds);
    }

    [Fact]
    public void LiveFramesAreScheduledByTheClock()
    {
        var (r, backend, frames, setClock, _) = RecorderFakes.Create();
        r.Start(new RecordingOptions { Path = "/r/out.mp4" });
        foreach (var t in new[] { 0.0, 1 / 30.0, 2 / 30.0 })
        {
            setClock(t);
            r.OnUpdate(loading: false, paused: false);
            frames.Render();
        }

        r.StopAndWait(TimeSpan.FromSeconds(5));
        Assert.Equal(5, backend.VideoFrames); // 1 + 2 + 2
    }

    // A 60 Hz game presenting with ±3 ms of jitter whose first frame lands 0.1 ms after an output slot boundary (here
    // 50.1 ms after Start, mid-update). Deterministic jitter, the first frame exact.
    private static double[] JitteredSixtyHz(int count)
    {
        var random = new Random(1234);
        return [.. Enumerable.Range(0, count).Select(k => 0.0501 + k / 60.0 + (k == 0 ? 0 : (random.NextDouble() * 2 - 1) * 0.003))];
    }

    [Fact]
    public void LiveSlotsAreAnchoredMidSlotAtTheFirstFrameSoJitterNeitherRepeatsNorSkipsFrames()
    {
        var times = JitteredSixtyHz(600);

        // Without the anchor (slots counted from Start, as before) the same frames straddle slot boundaries.
        var unanchored = new FrameScheduler(60, offline: false);
        int duplicated = 0, skipped = 0;
        foreach (var t in times)
        {
            if (!unanchored.ShouldCapture(t))
            {
                skipped++;
                continue;
            }

            var copies = unanchored.CopiesFor(t);
            duplicated += Math.Max(0, copies - 1);
            unanchored.Committed(copies);
        }

        Assert.True(duplicated > 50 && skipped > 50, $"expected judder without the anchor: {duplicated} repeated, {skipped} skipped");

        var (r, backend, frames, setClock, _) = RecorderFakes.Create();
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        foreach (var t in times)
        {
            setClock(t);
            r.OnUpdate(loading: false, paused: false);
            frames.Render();
        }

        var result = r.StopAndWait(TimeSpan.FromSeconds(5))!;

        Assert.Equal(600, frames.Delivered);      // nothing skipped
        Assert.Equal(600, backend.VideoFrames);   // nothing repeated: one output frame per rendered frame
        Assert.Equal(0, result.DuplicatedFrames);
        Assert.Equal(10.0, backend.Info!.DurationSeconds);
    }

    [Fact]
    public void RepeatedOutputFramesAreCounted()
    {
        var (r, _, frames, setClock, _) = RecorderFakes.Create();
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        setClock(0.0); frames.Render();       // origin at Start: slot 0
        setClock(4.5 / 60); frames.Render();  // a hitch: slots 1-4, three of them repeats
        setClock(5.5 / 60); frames.Render();  // slot 5

        var result = r.StopAndWait(TimeSpan.FromSeconds(5))!;

        Assert.Equal(3, result.DuplicatedFrames);
    }

    [Fact]
    public void TimelineEventsAreOnTheAnchoredClockAndEarlierOnesClampToZero()
    {
        var (r, backend, frames, setClock, _) = RecorderFakes.Create();
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        setClock(0.2);
        r.Mark("before the first frame");
        frames.Render();                      // origin = 0.2 - 1/120
        setClock(1.2 - 1 / 120.0);
        r.Mark("one second in");
        frames.Render();

        r.StopAndWait(TimeSpan.FromSeconds(5));

        Assert.Equal([new CaptureMarker(0, "before the first frame"), new CaptureMarker(1.0, "one second in")],
            backend.Spans!.Markers.Select(m => m with { Time = Math.Round(m.Time, 9) }));
    }

    [Fact]
    public void LiveAudioBeforeTheAnchoredOriginIsDropped()
    {
        using var audio = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        var (r, backend, frames, setClock, start) = RecorderFakes.Create(audio: audio);
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });

        // The audio starts at once (a few real milliseconds after Start) while the first frame is half a second later.
        var before = System.Diagnostics.Stopwatch.GetTimestamp();
        audio.RenderNullDevice(48_000);
        var after = System.Diagnostics.Stopwatch.GetTimestamp();
        setClock(0.5);
        frames.Render();
        r.OnUpdate(loading: false, paused: false);
        r.StopAndWait(TimeSpan.FromSeconds(5));

        // The origin is 0.5 s - half a slot after Start: the audio mixed before it is not in the video.
        var origin = start + RecorderFakes.Ticks(0.5 - 1 / 120.0);
        static long Frames(long ticks) => (long)Math.Round((double)ticks / System.Diagnostics.Stopwatch.Frequency * 48_000);
        Assert.Empty(backend.Silences); // no lead silence: the first block came before the origin
        Assert.InRange(backend.AudioWritten, 48_000 - Frames(origin - before) - 1, 48_000 - Frames(origin - after) + 1);
    }

    [Fact]
    public void LiveAudioAfterTheAnchoredOriginIsLedBySilence()
    {
        using var audio = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        var (r, backend, frames, setClock, start) = RecorderFakes.Create(audio: audio);
        setClock(-1.0);
        r.Start(new RecordingOptions { Path = "/r/a.mp4" }); // Start one second before "now" on the real clock
        setClock(-0.5);
        frames.Render();                                       // origin half a second before the real now

        var before = System.Diagnostics.Stopwatch.GetTimestamp();
        audio.RenderNullDevice(4_800);
        var after = System.Diagnostics.Stopwatch.GetTimestamp();
        r.OnUpdate(loading: false, paused: false);
        r.StopAndWait(TimeSpan.FromSeconds(5));

        var origin = start + RecorderFakes.Ticks(-0.5 - 1 / 120.0);
        static long Frames(long ticks) => (long)Math.Round((double)ticks / System.Diagnostics.Stopwatch.Frequency * 48_000);
        Assert.InRange(backend.Silences[0], Frames(before - origin) - 1, Frames(after - origin) + 1);
        Assert.Equal(4_800, backend.AudioWritten);
    }

    [Fact]
    public void AFrameTheEncoderCannotTakeIsDroppedAndTheNextOneMakesUpTheTime()
    {
        var (r, backend, frames, setClock, _) = RecorderFakes.Create();
        r.Start(new RecordingOptions { Path = "/r/out.mp4" });
        setClock(0.0); frames.Render();
        backend.Full = true;
        setClock(1 / 30.0); frames.Render();
        backend.Full = false;
        setClock(2 / 30.0); frames.Render();

        r.StopAndWait(TimeSpan.FromSeconds(5));

        Assert.Equal(5, backend.VideoFrames); // 1 + 0 + 4: length and sync hold, only smoothness suffered
        Assert.Equal(1, backend.Info!.DroppedFrames);
    }

    [Fact]
    public void MarksKeepsSkipsInputLoadingAndPauseReachTheSidecar()
    {
        var (r, backend, frames, setClock, _) = RecorderFakes.Create();
        r.Start(new RecordingOptions { Path = "/r/out.mp4" });
        setClock(0.0); r.OnUpdate(loading: true, paused: false); frames.Render();
        setClock(1.0); r.OnUpdate(loading: false, paused: false); frames.Render();
        r.ObserveInput(new InputEventKey { Key = Silk.NET.Input.Key.Space, Pressed = true });
        r.ObserveInput(new InputEventKey { Key = Silk.NET.Input.Key.Space, Pressed = false });
        r.Mark("boss intro");
        using (r.Keep("cutscene"))
        {
            setClock(2.0); r.OnUpdate(loading: false, paused: false); frames.Render();
        }

        setClock(3.0); r.OnUpdate(loading: false, paused: true); frames.Render();
        r.StopAndWait(TimeSpan.FromSeconds(5));

        var s = backend.Spans!;
        Assert.Equal([new CaptureSpan(0.0, 1.0)], s.Loading);
        Assert.Equal([new CaptureSpan(1.0, 1.25)], s.Input);
        Assert.Equal([new CaptureMarker(1.0, "boss intro")], s.Markers);
        Assert.Equal("cutscene", Assert.Single(s.Keep).Label);
        Assert.Single(s.Paused);
        Assert.Equal("Forest", backend.Info!.Game);
        Assert.Equal("res://Scenes/Forest.scn", backend.Info.Scene);
        Assert.Equal("out.mp4", backend.Info.Video);
    }

    [Fact]
    public void LosingFocusReleasesHeldInput()
    {
        var (r, backend, frames, setClock, _) = RecorderFakes.Create();
        r.Start(new RecordingOptions { Path = "/r/out.mp4" });
        setClock(0.0); frames.Render(); // the first frame: the video's time 0 is Start
        setClock(1.0); r.OnUpdate(loading: false, paused: false); frames.Render();
        r.ObserveInput(new InputEventKey { Key = Silk.NET.Input.Key.W, Pressed = true });

        // The key-up went to another window: without the reset, W stays held until the recording ends.
        r.ReleaseHeldInput();
        setClock(2.0); r.OnUpdate(loading: false, paused: false); frames.Render();
        setClock(3.0); r.OnUpdate(loading: false, paused: false); frames.Render();
        r.StopAndWait(TimeSpan.FromSeconds(5));

        Assert.Equal([new CaptureSpan(1.0, 1.25)], backend.Spans!.Input);
    }

    [Fact]
    public void StartWhileFinalisingThrows()
    {
        var (r, _, _, _, _) = RecorderFakes.Create();
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        r.Stop();

        // Until PollCompletion (or StopAndWait) has raised Stopped, the previous recording still owns its files.
        Assert.Throws<InvalidOperationException>(() => r.Start(new RecordingOptions { Path = "/r/b.mp4" }));
        r.StopAndWait(TimeSpan.FromSeconds(5));
        r.Start(new RecordingOptions { Path = "/r/b.mp4" }); // and afterwards a new one may start
    }

    [Fact]
    public void StartWhileRecordingThrows()
    {
        var (r, _, _, _, _) = RecorderFakes.Create();
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        Assert.Throws<InvalidOperationException>(() => r.Start(new RecordingOptions { Path = "/r/b.mp4" }));
    }

    [Fact]
    public void AFailedStartLeavesTheRecorderIdleAndReusable()
    {
        var (r, backend, frames, _, _) = RecorderFakes.Create();
        var started = 0;
        r.Started += () => started++;
        backend.BeginThrows = new IOException("This ffmpeg has no usable H.264 encoder.");

        Assert.Throws<IOException>(() => r.Start(new RecordingOptions { Path = "/r/a.mp4" }));

        Assert.False(r.IsRecording);
        Assert.False(r.IsFinalising);
        Assert.Null(frames.Sink);
        Assert.Equal(0, started);
        backend.BeginThrows = null;
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        Assert.True(r.IsRecording);
        Assert.Equal(1, started);
    }

    [Fact]
    public void StreamBreakStopsWithReason()
    {
        var (r, _, frames, _, _) = RecorderFakes.Create();
        RecordingResult? stopped = null;
        r.Stopped += result => stopped = result;
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        frames.Render();

        frames.Sink!.OnStreamEnded("window size changed");
        r.StopAndWait(TimeSpan.FromSeconds(5));
        r.PollCompletion();

        Assert.False(r.IsRecording);
        Assert.Contains("window size changed", stopped!.Error);
    }

    [Fact]
    public void AnEncoderFailureNeverEscapesTheRendererAndStopsAtTheNextUpdate()
    {
        var (r, backend, frames, setClock, _) = RecorderFakes.Create();
        RecordingResult? stopped = null;
        r.Stopped += result => stopped = result;
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        frames.Render();
        backend.VideoThrows = new IOException("Broken pipe");

        setClock(1 / 60.0); frames.Render(); // OnFrame runs inside VulkanRenderer.BeginFrame: it must not throw
        setClock(2 / 60.0); frames.Render();
        r.OnUpdate(loading: false, paused: false);
        Assert.False(r.IsRecording);

        var result = r.StopAndWait(TimeSpan.FromSeconds(5))!; // nothing left to stop: waits for the finalising one
        Assert.Contains("Broken pipe", result.Error);
        Assert.Same(result, stopped);
        Assert.Equal(1, backend.VideoFrames);
    }

    [Fact]
    public void AnAudioWriteFailureStopsTheRecordingInsteadOfCrashing()
    {
        var (r, backend, frames, _, _) = RecorderFakes.Create(offline: true);
        RecordingResult? stopped = null;
        r.Stopped += result => stopped = result;
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        r.OnUpdate(loading: false, paused: false);
        frames.Render();
        backend.AudioThrows = new IOException("No space left on device");

        r.OnUpdate(loading: false, paused: false); // writes the first frame's audio: fails

        Assert.False(r.IsRecording);
        WaitForStopped(r, () => stopped);
        Assert.Contains("No space left on device", stopped!.Error);
    }

    [Fact]
    public void AThrowingFinishBecomesAnErrorResult()
    {
        var (r, backend, frames, _, _) = RecorderFakes.Create();
        backend.FinishThrows = new OperationCanceledException("The mux was cancelled.");
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        frames.Render();

        var result = r.StopAndWait(TimeSpan.FromSeconds(5))!;

        Assert.Contains("The mux was cancelled.", result.Error);
        Assert.True(backend.Disposed);
        Assert.False(r.IsFinalising);
    }

    [Fact]
    public void AnUnexpectedFinishFailureIsReportedNotThrown()
    {
        var (r, backend, frames, _, _) = RecorderFakes.Create();
        backend.FinishThrows = new NotSupportedException("Something nobody planned for.");
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        frames.Render();

        var result = r.StopAndWait(TimeSpan.FromSeconds(5))!;

        Assert.Contains("Something nobody planned for.", result.Error);
        r.PollCompletion();
        Assert.False(r.IsFinalising);
    }

    [Fact]
    public void ABackendWhoseDisposeFailsToKillTheEncoderStillFinishes()
    {
        // Process.Kill throws Win32Exception when the OS refuses; it used to fault the finishing task.
        var (r, backend, frames, _, _) = RecorderFakes.Create();
        backend.DisposeThrows = new System.ComponentModel.Win32Exception(5, "Access is denied.");
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        frames.Render();

        var result = r.StopAndWait(TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.Null(result.Error); // the recording itself was finalised; failing to tidy up is only a warning
        Assert.False(r.IsFinalising);
        backend.DisposeThrows = null;
        r.Start(new RecordingOptions { Path = "/r/b.mp4" });
        Assert.True(r.IsRecording);
    }

    [Fact]
    public void AFaultedFinishingTaskBecomesAnErrorResultAndTheRecorderStaysUsable()
    {
        // An OutOfMemoryException (InsufficientMemoryException is one) is the only failure DisposeQuietly lets through: if the task
        // faults anyway, StopAndWait and PollCompletion must report it, never rethrow it from .Result.
        var (r, backend, frames, _, _) = RecorderFakes.Create();
        backend.DisposeThrows = new InsufficientMemoryException("Simulated.");
        RecordingResult? stopped = null;
        r.Stopped += result => stopped = result;
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        frames.Render();

        var result = r.StopAndWait(TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.Contains("Simulated.", result.Error);
        Assert.Equal(Path.GetFullPath("/r/a.mp4"), result.Path); // the recorder stores full paths (D:\r\a.mp4 on Windows)
        Assert.Same(result, stopped);
        r.PollCompletion(); // nothing left: must not throw
        Assert.False(r.IsFinalising);
        backend.DisposeThrows = null;
        r.Start(new RecordingOptions { Path = "/r/b.mp4" });
        Assert.True(r.IsRecording);
    }

    [Fact]
    public void PollCompletionReportsAFaultedFinishingTaskInsteadOfThrowing()
    {
        var (r, backend, frames, _, _) = RecorderFakes.Create();
        backend.DisposeThrows = new InsufficientMemoryException("Simulated.");
        RecordingResult? stopped = null;
        r.Stopped += result => stopped = result;
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        frames.Render();

        r.Stop();
        var result = WaitForStopped(r, () => stopped);

        Assert.Contains("Simulated.", result.Error);
        Assert.False(r.IsFinalising);
    }

    [Fact]
    public void AZeroFrameRecordingIsAnErrorAndWritesNoSidecar()
    {
        var (r, backend, _, _, _) = RecorderFakes.Create();
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });

        var result = r.StopAndWait(TimeSpan.FromSeconds(5))!;

        Assert.Equal("Recording stopped before any frame was captured.", result.Error);
        Assert.Null(backend.Info); // FinishAsync never ran: no mux of nothing, no sidecar with durationSeconds 0
        Assert.True(backend.Disposed);
    }

    [Fact]
    public void AZeroFrameRecordingRemovesItsPartialFolder()
    {
        var dir = Directory.CreateTempSubdirectory("mfrec");
        try
        {
            var output = Path.Combine(dir.FullName, "a.mp4");
            var (r, backend, _, _, _) = RecorderFakes.Create();
            r.Start(new RecordingOptions { Path = output });
            // What the real backend leaves for a recording that never got a frame: an empty TS and a WAV header.
            Directory.CreateDirectory(output + ".partial");
            File.WriteAllBytes(Path.Combine(output + ".partial", "video.ts"), []);
            File.WriteAllBytes(Path.Combine(output + ".partial", "audio.wav"), new byte[44]);

            var result = r.StopAndWait(TimeSpan.FromSeconds(5))!;

            Assert.False(Directory.Exists(output + ".partial")); // nothing in it is worth keeping
            Assert.Equal("Recording stopped before any frame was captured.", result.Error);
            Assert.True(backend.Disposed);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void StopFromTheStartedEventAbandonsTheEmptyRecording()
    {
        // The earliest a Stop can arrive: inside Start, from a Started handler, before any frame was captured.
        var (r, backend, frames, _, _) = RecorderFakes.Create();
        RecordingResult? stopped = null;
        r.Started += () => r.Stop();
        r.Stopped += result => stopped = result;

        r.Start(new RecordingOptions { Path = "/r/a.mp4" });

        Assert.False(r.IsRecording);
        Assert.Null(frames.Sink); // the stream Start began was ended again
        var result = WaitForStopped(r, () => stopped);
        Assert.Equal("Recording stopped before any frame was captured.", result.Error);
        Assert.Null(backend.Info); // no mux, no sidecar
        Assert.True(backend.Disposed);
        Assert.False(r.IsFinalising);
    }

    [Fact]
    public void StopWhileStartIsFailingIsANoOp()
    {
        // Start's backend fails (no encoder, ffmpeg gone): Stop and the shutdown path find nothing to stop or wait for.
        var (r, backend, frames, _, _) = RecorderFakes.Create();
        RecordingResult? stopped = null;
        r.Stopped += result => stopped = result;
        backend.BeginThrows = new IOException("The encoder could not be started.");

        Assert.Throws<IOException>(() => r.Start(new RecordingOptions { Path = "/r/a.mp4" }));
        r.Stop();

        Assert.Null(r.StopAndWait(TimeSpan.FromSeconds(1)));
        Assert.Null(stopped);
        Assert.Null(frames.Sink);
        Assert.True(backend.Disposed);
        Assert.False(r.IsRecording || r.IsFinalising);
    }

    [Fact]
    public void LiveOverflowIsPaddedWithSilenceSoAudioNeverDrifts()
    {
        using var audio = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        var (r, backend, frames, _, _) = RecorderFakes.Create(audio: audio);
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        frames.Render();

        audio.RenderNullDevice(200_000); // more than the tap's ring holds: whole blocks overflow
        r.OnUpdate(loading: false, paused: false);
        audio.RenderNullDevice(1_000);
        r.StopAndWait(TimeSpan.FromSeconds(5));

        // The first silence aligns the first block to the start (wall-clock dependent); the rest stand in for dropped blocks.
        Assert.True(backend.Silences.Count >= 2);
        Assert.Equal(201_000, backend.AudioWritten + backend.Silences.Skip(1).Sum());
    }

    [Fact]
    public void StopDuringCloseFinalisesSynchronously()
    {
        var (r, backend, frames, _, _) = RecorderFakes.Create();
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        frames.Render();

        var result = r.StopAndWait(TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.NotNull(backend.Spans);
        Assert.False(r.IsRecording);
        Assert.False(r.IsFinalising);
    }

    [Fact]
    public void BackendFailureKeepsPartialPathInError()
    {
        var (r, backend, frames, _, _) = RecorderFakes.Create();
        backend.FailWith = "The encoder failed.";
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        frames.Render();

        var result = r.StopAndWait(TimeSpan.FromSeconds(5))!;

        Assert.Contains(".partial", result.Error);
    }

    [Fact]
    public void MarkKeepAndSkipAreHarmlessWhenNotRecording()
    {
        var (r, _, _, _, _) = RecorderFakes.Create();
        r.Mark("x");
        r.Keep("y").Dispose();
        r.Skip("z").Dispose();
        Assert.False(r.IsRecording);
    }

    // OnUpdate already stopped the recording, so StopAndWait has nothing to stop: poll until Stopped is raised.
    private static RecordingResult WaitForStopped(GameRecorder r, Func<RecordingResult?> stopped)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (stopped() is null && DateTime.UtcNow < deadline)
        {
            r.PollCompletion();
            Thread.Sleep(1);
        }

        return stopped() ?? throw new TimeoutException("Stopped was never raised.");
    }
}
