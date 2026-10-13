namespace MainframeEngine.Video.Detection;

/// <summary>A stretch of audio below the silence threshold, as reported by the detector. Seconds.</summary>
public readonly record struct SilenceSpan(double Start, double End)
{
    public double Duration => End - Start;
}
