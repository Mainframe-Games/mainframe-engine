namespace MainframeEngine;

public struct GameTime
{
    public uint FrameCount { get; set; }
    public double DeltaTime { get; set; }
    public uint FramesPerSecond { get; set; }
    public uint FramesTimeMs { get; set; }
}
