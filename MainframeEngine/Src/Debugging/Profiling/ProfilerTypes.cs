using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>What a <see cref="ProfilerMarker"/> measures: the colour of its bars and the group of its row.</summary>
public enum ProfilerCategory : byte
{
    Engine,
    Script,
    Physics,
    Render,
    Ui,
    Audio,
    Loading,
    Network,
    Game,
}

/// <summary>Where a marker's samples come from (the editor link sends it with the marker's name).</summary>
public enum ProfilerMarkerKind : byte
{
    Cpu,
    Gpu,
    Counter,

    /// <summary>A GPU zone that begins or ends inside a render pass: approximate where timestamps are sampled at encoder boundaries (MoltenVK).</summary>
    GpuInPass,
}

/// <summary>How a <see cref="ProfilerCounter"/>'s value reads.</summary>
public enum ProfilerCounterUnit : byte
{
    Count,
    Bytes,
    Milliseconds,
    Percent,
    Microseconds,
}

/// <summary>
/// One recorded scope (or GPU zone): <see cref="System.Diagnostics.Stopwatch"/> ticks of its start and end, its marker,
/// nesting depth and thread lane. 24 bytes; GPU zones use <see cref="GpuThread"/> and start/end already aligned to the
/// CPU clock.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct ProfilerEvent(long start, long end, ushort marker, byte depth, byte thread, int allocatedBytes = 0)
{
    /// <summary>The lane GPU zones are recorded in.</summary>
    public const byte GpuThread = 255;

    /// <summary>Start, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</summary>
    public long Start { get; } = start;

    /// <summary>End, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</summary>
    public long End { get; } = end;

    public ushort Marker { get; } = marker;

    /// <summary>Nesting depth on its thread (0 = outermost).</summary>
    public byte Depth { get; } = depth;

    /// <summary>Thread lane (0 = main, <see cref="GpuThread"/> = GPU).</summary>
    public byte Thread { get; } = thread;

    /// <summary>Managed bytes the scope allocated on its thread (with <see cref="ProfilerOptions.TrackAllocations"/>; else 0).</summary>
    public int AllocatedBytes { get; } = allocatedBytes;

    public long DurationTicks => End - Start;
}

/// <summary>A registered marker: its name, category, kind and declaring source line.</summary>
public readonly record struct ProfilerMarkerInfo(ushort Id, string Name, ProfilerCategory Category, ProfilerMarkerKind Kind, string File, int Line);

/// <summary>A registered counter.</summary>
public readonly record struct ProfilerCounterInfo(ushort Id, string Name, ProfilerCounterUnit Unit);

/// <summary>
/// Recorder settings (<see cref="EngineOptions.Profiler"/>; <see cref="Profiler.Configure"/>). Sizes apply when recording
/// first starts; storage is preallocated then and never grows.
/// </summary>
public sealed record ProfilerOptions
{
    /// <summary>Record from the first frame (<c>--profile</c>) instead of only while something looks.</summary>
    public bool RecordFromStart { get; init; }

    /// <summary>Pass-level GPU zones (timestamps around each pass and post effect). Default on (ADR 0219).</summary>
    public bool GpuTimings { get; init; } = true;

    /// <summary>In-pass GPU zones too (one per overlay renderer). Off by default: they collapse onto pass boundaries on MoltenVK.</summary>
    public bool DetailedGpuZones { get; init; }

    /// <summary>Frames kept in the history ring (600 = 10 s at 60 Hz).</summary>
    public int HistoryFrames { get; init; } = 600;

    /// <summary>CPU events kept for the whole history (24 bytes each); when the ring wraps the oldest frames lose their events.</summary>
    public int EventCapacity { get; init; } = 65536;

    /// <summary>GPU zones per frame at most (timestamp queries: 2 per zone per frame slot).</summary>
    public int MaxGpuZones { get; init; } = 128;

    /// <summary>Spike frames kept apart from the ring (they survive it wrapping).</summary>
    public int SpikeSlots { get; init; } = 8;

    /// <summary>Scopes also read <see cref="GC.GetAllocatedBytesForCurrentThread"/> (about twice a plain scope's cost).</summary>
    public bool TrackAllocations { get; init; }

    /// <summary>The audio thread's callback records a "Mix" scope (opt-in: the callback must never wait).</summary>
    public bool AudioThread { get; init; }

    internal ProfilerOptions Validated()
    {
        if (HistoryFrames is < 2 or > 1 << 20)
            throw new ArgumentOutOfRangeException(nameof(HistoryFrames), HistoryFrames, "History must hold 2 .. 1M frames.");
        if (EventCapacity is < 64 or > 1 << 26)
            throw new ArgumentOutOfRangeException(nameof(EventCapacity), EventCapacity, "Event capacity must be 64 .. 64M.");
        if (MaxGpuZones is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(MaxGpuZones), MaxGpuZones, "GPU zones must be 1 .. 4096.");
        if (SpikeSlots is < 0 or > 256)
            throw new ArgumentOutOfRangeException(nameof(SpikeSlots), SpikeSlots, "Spike slots must be 0 .. 256.");
        return this;
    }
}
