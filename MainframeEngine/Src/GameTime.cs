using System.Text;

namespace MainframeEngine;

public struct GameTime()
{
    public uint FrameCount;
    public double DeltaTime;
    public uint FramesPerSecond;
    public uint FramesTimeMs;

    private StringBuilder? _str = new();
    
    public override string ToString()
    {
        _str ??= new StringBuilder();
        _str.Clear();
        _str
            .Append("FrameCount: ").Append(FrameCount)
            .Append(" DeltaTime: ").Append(DeltaTime)
            .Append(" FPS: ").Append(FramesPerSecond)
            .Append(" FramesTimeMs: ").Append(FramesTimeMs)
            ;
        
        return _str.ToString();
    }
}
