namespace MainframeEngine.Video.Detection;

/// <summary>A filler word's extent in the source, in seconds, with its edges already snapped by the caller.</summary>
public readonly record struct FillerSpan(double Start, double End, string Word);
