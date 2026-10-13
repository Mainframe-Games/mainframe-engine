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

/// <summary>
/// The outcome of a recording, raised by <see cref="GameRecorder.Stopped"/>. <see cref="Error"/> is null on success; on
/// failure the partial files are kept and named in it. Init-only properties so fields can be added without breaking callers.
/// </summary>
public sealed record RecordingResult
{
    /// <summary>The output video the recording was written to (or would have been).</summary>
    public required string Path { get; init; }

    /// <summary>Length of the output video in seconds (output frames / fps).</summary>
    public double DurationSeconds { get; init; }

    /// <summary>Frames the encoder could not take in time (live); the next frame written repeated to cover them.</summary>
    public long DroppedFrames { get; init; }

    /// <summary>
    /// Output frames that repeat the previous one (live: a frame that took longer than one output interval, or the
    /// make-up copies after a drop). A steady 60 Hz game recorded at 60 fps should have almost none.
    /// </summary>
    public long DuplicatedFrames { get; init; }

    /// <summary>Why the recording failed or ended early; null on success.</summary>
    public string? Error { get; init; }
}
