namespace MainframeEngine;

/// <summary>What a recording backend is asked to produce; the frame and sample rates come from the run, not the backend.</summary>
internal sealed record RecordingFormat(string OutputPath, int Width, int Height, int Fps, PixelLayout Layout, int SampleRate, int Channels, bool Offline);

/// <summary>
/// Where captured frames and audio go. Behind an interface so the recorder's pacing logic never depends on
/// a child process; the only implementation today is <see cref="FfmpegRecordingBackend"/>.
/// </summary>
internal interface IRecordingBackend : IDisposable
{
    void Begin(RecordingFormat format);

    /// <summary>Queues <paramref name="copies"/> copies of a frame. False when the encoder is behind (live: drop it).</summary>
    bool TryWriteVideo(ReadOnlySpan<byte> pixels, int copies, bool wait);

    void WriteAudio(ReadOnlySpan<float> interleaved);

    void WriteSilence(long frames);

    /// <summary>Encodes the rest, pads audio to <paramref name="videoFrames"/>, muxes, writes the sidecar, removes .partial.</summary>
    Task<RecordingResult> FinishAsync(long videoFrames, CaptureInfo info, CaptureSpans spans, CancellationToken ct);
}

/// <summary>The outcome of a recording. <see cref="Error"/> is null on success; on failure the partial files are kept and named in it.</summary>
public sealed record RecordingResult(string Path, double DurationSeconds, long DroppedFrames, string? Error);
