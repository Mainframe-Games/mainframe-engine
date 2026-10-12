namespace MainframeEngine;

/// <summary>
/// Where <see cref="GameRecorder.Start"/> records and at what output frame rate. Live recordings are resampled to
/// <paramref name="Fps"/> constant frames per second; offline ones (<c>--record</c> with <c>--fixed-fps</c>) write one
/// frame per update, so the host passes its fixed rate here.
/// </summary>
/// <param name="Path">The output video (<c>.mp4</c>); the sidecar is written next to it as <c>&lt;Path&gt;.mfcap.json</c>.</param>
/// <param name="Fps">Output frames per second.</param>
public sealed record RecordingOptions(string Path, int Fps = 60);
