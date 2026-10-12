using MainframeEngine.Audio;

namespace MainframeEngine.Tests.Recording;

/// <summary>Start/Stop lifecycle, sidecar contents and failure handling, with a fake backend and frame source.</summary>
public sealed class GameRecorderTests
{
    private sealed class FakeBackend : IRecordingBackend
    {
        public RecordingFormat? Format;
        public int VideoFrames;
        public long AudioFrames;
        public long AudioWritten;
        public readonly List<long> Silences = [];
        public CaptureSpans? Spans;
        public CaptureInfo? Info;
        public bool Full;
        public string? FailWith;
        public Exception? BeginThrows;
        public Exception? VideoThrows;
        public Exception? AudioThrows;
        public Exception? FinishThrows;
        public Exception? DisposeThrows;
        public bool Disposed;

        public void Begin(RecordingFormat format)
        {
            if (BeginThrows is { } e)
                throw e;
            Format = format;
        }

        public bool TryWriteVideo(ReadOnlySpan<byte> pixels, int copies, bool wait)
        {
            if (VideoThrows is { } e)
                throw e;
            if (Full && !wait)
                return false;
            VideoFrames += copies;
            return true;
        }

        public void WriteAudio(ReadOnlySpan<float> interleaved)
        {
            if (AudioThrows is { } e)
                throw e;
            AudioFrames += interleaved.Length / 2;
            AudioWritten += interleaved.Length / 2;
        }

        public void WriteSilence(long frames)
        {
            if (AudioThrows is { } e)
                throw e;
            AudioFrames += frames;
            Silences.Add(frames);
        }

        public Task<RecordingResult> FinishAsync(long videoFrames, CaptureInfo info, CaptureSpans spans, CancellationToken ct)
        {
            if (FinishThrows is { } e)
                throw e;
            Info = info;
            Spans = spans;
            return Task.FromResult(new RecordingResult
            {
                Path = Format!.OutputPath,
                DurationSeconds = info.DurationSeconds,
                DroppedFrames = info.DroppedFrames,
                Error = FailWith is null ? null : FailWith + " The partial recording is in " + Format.OutputPath + ".partial.",
            });
        }

        public void Dispose()
        {
            Disposed = true;
            if (DisposeThrows is { } e)
                throw e;
        }
    }

    private sealed class FakeFrames : IFrameStreamSource
    {
        public IFrameStreamSink? Sink;
        public bool CanStream => true;
        public PixelLayout StreamLayout => PixelLayout.Bgra;
        public (int Width, int Height) StreamSize => (4, 2);
        public void BeginStream(IFrameStreamSink sink) => Sink = sink;
        public void EndStream() => Sink = null;

        /// <summary>One rendered frame: ask, then deliver immediately (the fake GPU is instant).</summary>
        public void Render()
        {
            if (Sink is { } s && s.ShouldCapture(out var stamp))
                s.OnFrame(new byte[4 * 2 * 4], 4, 2, stamp);
        }
    }

    private static (GameRecorder Recorder, FakeBackend Backend, FakeFrames Frames, Func<double, double> SetClock) Create(
        bool offline = false, AudioServer? audio = null)
    {
        var backend = new FakeBackend();
        var frames = new FakeFrames();
        var now = 0.0;
        var recorder = new GameRecorder(_ => backend, frames, audio, offline,
            new CaptureInfoSource("Forest", () => "res://Scenes/Forest.scn", "0.42.0"), () => now);
        return (recorder, backend, frames, t => now = t);
    }

    [Fact]
    public void OfflineRecordingWritesOneFramePerUpdateAndExactAudio()
    {
        var (r, backend, frames, _) = Create(offline: true);
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
        var (r, backend, frames, _) = Create(offline: true, audio);
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
    public void LiveFramesAreScheduledByTheClock()
    {
        var (r, backend, frames, setClock) = Create();
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

    [Fact]
    public void AFrameTheEncoderCannotTakeIsDroppedAndTheNextOneMakesUpTheTime()
    {
        var (r, backend, frames, setClock) = Create();
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
        var (r, backend, frames, setClock) = Create();
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
        var (r, backend, frames, setClock) = Create();
        r.Start(new RecordingOptions { Path = "/r/out.mp4" });
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
        var (r, _, _, _) = Create();
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
        var (r, _, _, _) = Create();
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        Assert.Throws<InvalidOperationException>(() => r.Start(new RecordingOptions { Path = "/r/b.mp4" }));
    }

    [Fact]
    public void AFailedStartLeavesTheRecorderIdleAndReusable()
    {
        var (r, backend, frames, _) = Create();
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
        var (r, _, frames, _) = Create();
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
        var (r, backend, frames, setClock) = Create();
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
        var (r, backend, frames, _) = Create(offline: true);
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
        var (r, backend, frames, _) = Create();
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
        var (r, backend, frames, _) = Create();
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
        var (r, backend, frames, _) = Create();
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
        var (r, backend, frames, _) = Create();
        backend.DisposeThrows = new InsufficientMemoryException("Simulated.");
        RecordingResult? stopped = null;
        r.Stopped += result => stopped = result;
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        frames.Render();

        var result = r.StopAndWait(TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.Contains("Simulated.", result.Error);
        Assert.Equal("/r/a.mp4", result.Path);
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
        var (r, backend, frames, _) = Create();
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
        var (r, backend, _, _) = Create();
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });

        var result = r.StopAndWait(TimeSpan.FromSeconds(5))!;

        Assert.Equal("Recording stopped before any frame was captured.", result.Error);
        Assert.Null(backend.Info); // FinishAsync never ran: no mux of nothing, no sidecar with durationSeconds 0
        Assert.True(backend.Disposed);
    }

    [Fact]
    public void LiveOverflowIsPaddedWithSilenceSoAudioNeverDrifts()
    {
        using var audio = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        var (r, backend, frames, _) = Create(audio: audio);
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
        var (r, backend, frames, _) = Create();
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
        var (r, backend, frames, _) = Create();
        backend.FailWith = "The encoder failed.";
        r.Start(new RecordingOptions { Path = "/r/a.mp4" });
        frames.Render();

        var result = r.StopAndWait(TimeSpan.FromSeconds(5))!;

        Assert.Contains(".partial", result.Error);
    }

    [Fact]
    public void MarkKeepAndSkipAreHarmlessWhenNotRecording()
    {
        var (r, _, _, _) = Create();
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
