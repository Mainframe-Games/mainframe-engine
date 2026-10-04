namespace MainframeEngine;

/// <summary>
/// Contains all relevant data per frame
/// </summary>
public struct GameTime
{
    public uint FrameCount { get; internal set; }
    public float DeltaTime { get; internal set; }
    public uint FramesPerSecond { get; internal set; }
    public uint FramesTimeMs { get; internal set; }
}
