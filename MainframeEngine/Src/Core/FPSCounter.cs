using System.Diagnostics;

namespace MainframeEngine;

internal sealed class FPSCounter
{
    private readonly Stopwatch _stopwatch = new();
    private uint FrameCount { get; set; }
    public uint TotalFrameCount { get; private set; }
    public uint Fps { get; private set; }
    public uint Ms { get; private set; }

    public FPSCounter()
    {
        _stopwatch.Start();
    }

    public void Update()
    {
        FrameCount++;

        if (_stopwatch.ElapsedMilliseconds >= 500)
        {
            var ms = _stopwatch.ElapsedMilliseconds;
            Fps = (uint)(FrameCount / (ms / 1000.0f));
            Ms = (uint)(1000.0f / Fps);
            FrameCount = 0;
            _stopwatch.Restart();
        }

        TotalFrameCount++;
    }
}