namespace MainframeEngine;

/// <summary>
/// Where <see cref="GameRecorder.Start"/> records and at what output frame rate. Live recordings are resampled to
/// <see cref="Fps"/> constant frames per second; offline ones (<c>--record</c> with <c>--fixed-fps</c>) write one
/// frame per update, so the host passes its fixed rate here. Init-only properties rather than a positional record, so
/// options can be added later without breaking callers.
/// </summary>
public sealed record RecordingOptions
{
    /// <summary>The output video (<c>.mp4</c>); the sidecar is written next to it as <c>&lt;Path&gt;.mfcap.json</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Output frames per second.</summary>
    public int Fps { get; init; } = 60;
}
