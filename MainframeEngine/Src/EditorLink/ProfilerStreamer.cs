using System.Diagnostics;
using System.Globalization;

namespace MainframeEngine.EditorLink;

/// <summary>A finished profiler frame copied for the editor link's thread (one of a few preallocated blocks).</summary>
internal sealed class ProfileFrameBlock
{
    public const byte SpikeFlag = 1;
    public const byte TruncatedFlag = 2;
    public const byte EncoderBoundariesFlag = 4;

    public long Frame;
    public long StartTicks;
    public long EndTicks;
    public long TickFrequency = Stopwatch.Frequency;
    public float CpuMs;
    public float WaitMs;
    public float GpuMs;
    public byte Flags;
    public readonly ushort[] CounterIds = new ushort[Profiler.MaxCounters];
    public readonly long[] CounterValues = new long[Profiler.MaxCounters];
    public int CounterCount;
    public readonly ProfilerEvent[] Cpu = new ProfilerEvent[EditorLinkProtocol.MaxProfileEvents];
    public int CpuCount;
    public readonly ProfilerEvent[] Gpu = new ProfilerEvent[EditorLinkProtocol.MaxProfileGpuZones];
    public int GpuCount;

    /// <summary>The marker registry's size when the frame was copied (the link sends the markers it has not yet).</summary>
    public int MarkerCount;
}

/// <summary>
/// Streams profiler frames to the editor (G10, <see cref="EditorCommandKind.ProfilerStart"/>): while started it holds the
/// profiler, and each game update copies frames whose GPU zones arrived into the link's preallocated blocks; the link's
/// thread serialises them. When no block is free the frame is dropped and counted, so the game never waits for the editor.
/// Main thread; allocation-free per frame.
/// </summary>
internal sealed class ProfilerStreamer(EditorLinkClient client) : IDisposable
{
    private readonly int[] _depthCounts = new int[256];
    private int _token;
    private long _lastSent = long.MaxValue;
    private bool _gpu = true;

    public bool IsStreaming => _token != 0;

    /// <summary>GPU timestamps are sampled at encoder boundaries (MoltenVK): flagged on every frame for the editor's "≈".</summary>
    public bool EncoderBoundaries { get; set; }

    /// <summary>Frames copied for the link so far.</summary>
    public long SentFrames { get; private set; }

    /// <summary>
    /// Starts (or reconfigures) streaming with <paramref name="argument"/>: <c>gpu=1;spike=33.3;freeze=0</c> (missing keys
    /// keep their defaults: GPU zones on, spikes off, no freeze). Frames recorded from now on are sent.
    /// </summary>
    public void Start(string argument)
    {
        var (gpu, spike, freeze) = ParseArgument(argument);
        _gpu = gpu;
        Profiler.SpikeThresholdMs = spike;
        Profiler.FreezeOnSpike = freeze;
        if (_token == 0)
        {
            _token = Profiler.Acquire();
            _lastSent = Profiler.FrameNumber;
        }

        Profiler.Paused = false;
    }

    public void Stop()
    {
        if (_token == 0)
            return;
        Profiler.Release(_token);
        _token = 0;
    }

    internal static (bool Gpu, float SpikeMs, bool Freeze) ParseArgument(string? argument)
    {
        var gpu = true;
        var spike = 0f;
        var freeze = false;
        foreach (var part in (argument ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
                continue;
            var key = part[..eq];
            var value = part[(eq + 1)..];
            switch (key)
            {
                case "gpu":
                    gpu = value != "0";
                    break;
                case "spike" when float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && float.IsFinite(ms):
                    spike = Math.Max(0f, ms);
                    break;
                case "freeze":
                    freeze = value == "1";
                    break;
            }
        }

        return (gpu, spike, freeze);
    }

    /// <summary>Hands the frames completed since the last poll to the link (call once per game update).</summary>
    public void Poll()
    {
        if (_token == 0)
            return;
        var snapshot = Profiler.Snapshot;
        var count = snapshot.FrameCount;
        var first = count;
        while (first > 0 && snapshot.GetFrame(first - 1).Number > _lastSent)
            first--;
        for (var i = first; i < count; i++)
        {
            var frame = snapshot.GetFrame(i);
            if (frame.GpuPending && Profiler.FrameNumber - frame.Number < 6)
                break; // its GPU zones arrive in a frame or two; frames go out in order
            _lastSent = frame.Number;
            if (client.RentProfileBlock() is not { } block)
            {
                client.CountProfileDropped();
                continue;
            }

            Copy(snapshot, frame, block);
            client.SendProfileFrame(block);
            SentFrames++;
        }
    }

    private void Copy(ProfilerSnapshot snapshot, in ProfilerFrame frame, ProfileFrameBlock block)
    {
        block.Frame = frame.Number;
        block.StartTicks = frame.StartTicks;
        block.EndTicks = frame.EndTicks;
        block.TickFrequency = Stopwatch.Frequency;
        block.CpuMs = frame.CpuMs;
        block.WaitMs = frame.WaitMs;
        block.GpuMs = _gpu ? frame.GpuMs : float.NaN;
        block.Flags = (byte)((frame.IsSpike ? ProfileFrameBlock.SpikeFlag : 0) | (EncoderBoundaries ? ProfileFrameBlock.EncoderBoundariesFlag : 0));
        block.MarkerCount = Profiler.MarkerCount;

        block.CounterCount = Math.Min(Profiler.CounterCount, block.CounterIds.Length);
        for (var c = 0; c < block.CounterCount; c++)
        {
            block.CounterIds[c] = (ushort)c;
            block.CounterValues[c] = snapshot.GetCounter(frame, (ushort)c);
        }

        var events = snapshot.GetEvents(frame);
        if (events.Length <= block.Cpu.Length)
        {
            block.CpuCount = events.CopyTo(block.Cpu);
        }
        else
        {
            // Too many for one message: keep the shallowest depths that fit (the frame's shape survives).
            Array.Clear(_depthCounts);
            for (var e = 0; e < events.Length; e++)
                _depthCounts[events[e].Depth]++;
            var maxDepth = -1;
            var kept = 0;
            while (maxDepth < 255 && kept + _depthCounts[maxDepth + 1] <= block.Cpu.Length)
                kept += _depthCounts[++maxDepth];
            var n = 0;
            for (var e = 0; e < events.Length; e++)
                if (events[e].Depth <= maxDepth)
                    block.Cpu[n++] = events[e];
            block.CpuCount = n;
            block.Flags |= ProfileFrameBlock.TruncatedFlag;
        }

        block.GpuCount = _gpu ? snapshot.GetGpuZones(frame).CopyTo(block.Gpu) : 0;
    }

    public void Dispose() => Stop();
}
