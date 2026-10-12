namespace MainframeEngine;

/// <summary>
/// Exact audio sample counts per video frame: through frame <c>n</c> there are
/// <c>round(n · sampleRate / fps)</c> samples, so the total never drifts for rates that do not divide.
/// </summary>
internal static class SampleScheduler
{
    public static long SamplesThrough(long frames, int sampleRate, int fps) => (frames * sampleRate + fps / 2) / fps;

    public static int SamplesForFrame(long frame, int sampleRate, int fps) =>
        (int)(SamplesThrough(frame + 1, sampleRate, fps) - SamplesThrough(frame, sampleRate, fps));
}
