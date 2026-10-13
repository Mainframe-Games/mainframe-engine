namespace MainframeEngine.Video.Models;

/// <summary>Parameters for automatic silence detection. Edited per user; a project records the values its last run used.</summary>
/// <param name="PaddingMs">Extra kept audio on each side of a cut so consonants are not clipped.</param>
/// <param name="MinSilenceMs">Silences shorter than this are not cut.</param>
/// <param name="SilenceThresholdDb">Level below which audio counts as silence.</param>
/// <param name="MinKeepMs">Kept fragments shorter than this are merged into the neighbouring cut so the output does not machine-gun.</param>
public sealed record DetectionSettings(
    int PaddingMs,
    int MinSilenceMs,
    double SilenceThresholdDb,
    int MinKeepMs)
{
    public static DetectionSettings Default { get; } = new(
        PaddingMs: 60,
        MinSilenceMs: 400,
        SilenceThresholdDb: -34.0,
        MinKeepMs: 120);
}
