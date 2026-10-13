namespace MainframeEngine.Video.Detection;

/// <summary>A region the detector proposes to remove, after padding and minimum-keep rules. Seconds.</summary>
public sealed record PlannedCut(double Start, double End, string Reason)
{
    public double Duration => End - Start;
}
