namespace MainframeEngine.Video.Media.Export;

public enum ExportMode
{
    /// <summary>Re-encode with libx264 / aac, or the best working H.264 encoder when this ffmpeg has no libx264. Frame-accurate cuts. Slow.</summary>
    Precise,

    /// <summary>Stream copy with cuts snapped to keyframes. Fast, but boundaries are approximate.</summary>
    Fast,
}
