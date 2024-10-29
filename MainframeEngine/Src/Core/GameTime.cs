namespace MainframeEngine;

/// <summary>
/// Contains all relevant data per frame
/// </summary>
public struct GameTime
{
    public uint FrameCount { get; set; }
    public double DeltaTime { get; set; }
    public uint FramesPerSecond { get; set; }
    public uint FramesTimeMs { get; set; }
}
