using MainframeEngine.Audio;

namespace MainframeEngine.Tests.Recording;

/// <summary>A scriptable recording backend: records what the recorder writes and can fail on demand.</summary>
internal sealed class FakeBackend : IRecordingBackend
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

/// <summary>A frame source that delivers on demand, as the renderer would.</summary>
internal sealed class FakeFrames : IFrameStreamSource
{
    public IFrameStreamSink? Sink;
    public int Delivered;
    public bool CanStream => true;
    public PixelLayout StreamLayout => PixelLayout.Bgra;
    public (int Width, int Height) StreamSize => (4, 2);
    public void BeginStream(IFrameStreamSink sink) => Sink = sink;
    public void EndStream() => Sink = null;

    /// <summary>One rendered frame: ask, then deliver immediately (the fake GPU is instant).</summary>
    public void Render()
    {
        if (Sink is { } s && s.ShouldCapture(out var stamp))
        {
            Delivered++;
            s.OnFrame(new byte[4 * 2 * 4], 4, 2, stamp);
        }
    }
}

/// <summary>Recorders on fake backends and frame sources, shared by the recorder tests and the editor-link recording tests.</summary>
internal static class RecorderFakes
{
    /// <summary>
    /// A recorder on a fake live clock: <c>SetClock(t)</c> puts it <c>t</c> seconds after the moment of creation (Start
    /// is at 0 unless the clock was moved first). The clock is real <see cref="System.Diagnostics.Stopwatch"/> ticks, so
    /// it is comparable with the audio tap's first-block stamp.
    /// </summary>
    internal static (GameRecorder Recorder, FakeBackend Backend, FakeFrames Frames, Action<double> SetClock, long Base) Create(
        bool offline = false, AudioServer? audio = null, int hostFps = 60)
    {
        var backend = new FakeBackend();
        var frames = new FakeFrames();
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var now = start;
        var recorder = new GameRecorder(_ => backend, frames, audio, offline ? hostFps : 0,
            new CaptureInfoSource("Forest", () => "res://Scenes/Forest.scn", "0.42.0"), () => now);
        return (recorder, backend, frames, t => now = start + Ticks(t), start);
    }

    internal static long Ticks(double seconds) => (long)Math.Round(seconds * System.Diagnostics.Stopwatch.Frequency);
}
