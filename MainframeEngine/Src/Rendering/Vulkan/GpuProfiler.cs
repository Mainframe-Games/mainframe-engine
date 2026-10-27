using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace MainframeEngine;

/// <summary>
/// Named GPU zones (G10): timestamp pairs from one shared query pool, keyed by frame slot and read back right after the
/// slot's fence wait, so it never waits. The renderer records the pass-level zones (uploads, shadows, offscreen, prepass,
/// main pass, each post effect, tonemap, overlay); code drawing in <c>OnRenderMainPass</c> adds its own with
/// <see cref="Zone"/>. Every zone also records a <c>VK_EXT_debug_utils</c> label of the same name when the extension is
/// present, so RenderDoc and Xcode captures show the passes by name. Owned by the renderer
/// (<see cref="IVulkanContext.GpuProfiler"/>).
/// </summary>
/// <remarks>
/// Drivers without timestamps on the graphics queue (<c>timestampComputeAndGraphics</c>, <c>timestampPeriod</c>,
/// <c>timestampValidBits</c>) get labels only (<see cref="IsSupported"/> false). Apple GPUs (MoltenVK) sample timestamps at
/// encoder boundaries (<see cref="SamplesAtEncoderBoundaries"/>): zones bracketing whole passes or compute/blit work
/// measure correctly, zones inside one render pass collapse onto its boundaries.
/// </remarks>
public sealed unsafe class GpuProfiler : IDisposable
{
    private const int MaxStack = 64;

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly ExtDebugUtils? _debugUtils;
    private QueryPool _pool;
    private readonly int _maxZones;
    private readonly double _periodNs;
    private readonly ulong _mask;
    private readonly Slot[] _slots;
    private readonly ulong[] _results;
    private readonly ProfilerEvent[] _events;
    private readonly double[] _lastMs = new double[Profiler.MaxMarkers];
    private readonly long[] _lastSeen = new long[Profiler.MaxMarkers];
    private readonly nint[] _labels = new nint[Profiler.MaxMarkers];
    private readonly int[] _stackZone = new int[MaxStack];
    private int _stackDepth;
    private int _timedDepth;
    private int _slot = -1;
    private bool _frameTimed; // the slot's queries were reset this frame (Enabled may change mid-frame)
    private long _readbacks;
    private long _dropped;
    private bool _disposed;

    private sealed class Slot(int capacity)
    {
        public readonly ushort[] Markers = new ushort[capacity];
        public readonly byte[] Depths = new byte[capacity];
        public int Count;
        public long ProfilerFrame;
        public long SubmitTicks;
        public bool Pending;
        public bool Recorded;
    }

    /// <summary>A profiler without a device (benchmarks, tests): unsupported, no labels; every zone is a no-op.</summary>
    internal GpuProfiler()
    {
        _vk = null!;
        _maxZones = 1;
        _slots = [new Slot(1), new Slot(1)];
        _results = new ulong[2];
        _events = new ProfilerEvent[1];
    }

    internal GpuProfiler(IVulkanContext ctx, ExtDebugUtils? debugUtils, bool encoderBoundaries, int maxZones)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _vk = ctx.Vk;
        _device = ctx.Device;
        _debugUtils = debugUtils;
        _maxZones = Math.Clamp(maxZones, 1, 4096);
        SamplesAtEncoderBoundaries = encoderBoundaries;
        _slots = new Slot[IVulkanContext.MaxFramesInFlight];
        for (var i = 0; i < _slots.Length; i++)
            _slots[i] = new Slot(_maxZones);
        _results = new ulong[_maxZones * 2];
        _events = new ProfilerEvent[_maxZones];

        _vk.GetPhysicalDeviceProperties(ctx.PhysicalDevice, out var props);
        var validBits = GraphicsTimestampBits(_vk, ctx.PhysicalDevice);
        if (!props.Limits.TimestampComputeAndGraphics || props.Limits.TimestampPeriod <= 0f || validBits == 0)
            return;
        var info = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = (uint)(IVulkanContext.MaxFramesInFlight * _maxZones * 2),
        };
        _vk.CreateQueryPool(_device, in info, null, out _pool).Check("vkCreateQueryPool (GPU profiler)");
        _periodNs = props.Limits.TimestampPeriod;
        _mask = validBits >= 64 ? ulong.MaxValue : (1ul << (int)validBits) - 1;
        TimestampValidBits = (int)validBits;
    }

    /// <summary>Timestamps work on this device's graphics queue; false: zones record debug labels only.</summary>
    public bool IsSupported => _pool.Handle != 0;

    /// <summary>The GPU samples timestamps at encoder boundaries (MoltenVK): in-pass zones are approximate ("≈").</summary>
    public bool SamplesAtEncoderBoundaries { get; }

    /// <summary>Whether zones write timestamps (<see cref="ProfilerOptions.GpuTimings"/>); labels are recorded either way.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>In-pass zones (per overlay renderer) too (<see cref="ProfilerOptions.DetailedGpuZones"/>).</summary>
    public bool DetailedZones { get; set; }

    /// <summary>Command buffers carry debug-utils labels (the extension is present).</summary>
    public bool HasDebugLabels => _debugUtils is not null;

    /// <summary>The graphics queue's <c>timestampValidBits</c> (0 when unsupported).</summary>
    public int TimestampValidBits { get; }

    /// <summary>Zones dropped because a frame had more than <see cref="ProfilerOptions.MaxGpuZones"/> (total).</summary>
    public long DroppedZones => _dropped;

    /// <summary>
    /// Opens a zone named <paramref name="marker"/> on <paramref name="cb"/> (the frame's command buffer); disposing it closes
    /// it. Zones nest; prefer whole passes or compute work (see <see cref="SamplesAtEncoderBoundaries"/>).
    /// </summary>
    public GpuZone Zone(CommandBuffer cb, in ProfilerMarker marker)
    {
        var token = Begin(cb, marker.Id);
        return token < 0 ? default : new GpuZone(this, cb, token);
    }

    /// <summary>
    /// GPU milliseconds of <paramref name="marker"/>'s zones in the most recent frame read back (summed when a frame has
    /// several); 0 when untimed or not seen in the last few frames.
    /// </summary>
    public double LastMilliseconds(in ProfilerMarker marker)
    {
        var id = marker.Id;
        if (id == 0 || _readbacks - _lastSeen[id] > 8 || _lastSeen[id] == 0)
            return 0;
        return _lastMs[id];
    }

    // ── Frame lifecycle (renderer) ───────────────────────────────────────────────────────────────────────────

    /// <summary>After the slot's fence wait: its previous zones are complete; read them without waiting.</summary>
    internal void ReadBack(int slot)
    {
        var s = _slots[slot];
        if (!s.Pending)
            return;
        s.Pending = false;
        if (_pool.Handle == 0 || s.Count == 0)
        {
            if (s.Recorded)
                Profiler.CancelGpu(s.ProfilerFrame);
            return;
        }

        var queries = (uint)(s.Count * 2);
        Result result;
        fixed (ulong* data = _results)
            result = _vk.GetQueryPoolResults(_device, _pool, (uint)(slot * _maxZones * 2), queries, queries * 8, data, 8,
                QueryResultFlags.Result64Bit);
        if (result != Result.Success)
        {
            if (s.Recorded)
                Profiler.CancelGpu(s.ProfilerFrame);
            return;
        }

        _readbacks++;
        var first = _results[0];
        ulong span = 0;
        for (var i = 0; i < s.Count; i++)
        {
            var end = (_results[i * 2 + 1] - first) & _mask;
            if (end > span && end < _mask / 2)
                span = end;
        }

        var ticksPerNs = Stopwatch.Frequency / 1e9;
        for (var i = 0; i < s.Count; i++)
        {
            var begin = (_results[i * 2] - first) & _mask;
            var end = (_results[i * 2 + 1] - first) & _mask;
            if (begin > _mask / 2)
                begin = 0; // sampled before the first zone (cannot happen in submission order; guards a bogus value)
            if (end < begin || end > _mask / 2)
                end = begin;
            var ms = (end - begin) * _periodNs / 1e6;
            var marker = s.Markers[i];
            if (_lastSeen[marker] != _readbacks)
            {
                _lastSeen[marker] = _readbacks;
                _lastMs[marker] = 0;
            }

            _lastMs[marker] += ms;
            if (s.Recorded)
            {
                // "Aligned to submit": the frame's first timestamp is placed at its submit time on the CPU clock.
                var start = s.SubmitTicks + (long)(begin * _periodNs * ticksPerNs);
                var stop = s.SubmitTicks + (long)(end * _periodNs * ticksPerNs);
                _events[i] = new ProfilerEvent(start, stop, marker, s.Depths[i], ProfilerEvent.GpuThread);
            }
        }

        if (s.Recorded)
            Profiler.SubmitGpu(s.ProfilerFrame, _events.AsSpan(0, s.Count), (float)(span * _periodNs / 1e6));
    }

    /// <summary>The command buffer was begun: reset this slot's queries (outside any render pass) and start its zones.</summary>
    internal void BeginFrame(CommandBuffer cb, int slot)
    {
        _slot = slot;
        _stackDepth = 0;
        _timedDepth = 0;
        var s = _slots[slot];
        s.Count = 0;
        s.Pending = false;
        s.ProfilerFrame = Profiler.FrameNumber;
        s.Recorded = Profiler.IsRecording;
        _frameTimed = _pool.Handle != 0 && Enabled;
        if (_frameTimed)
            _vk.CmdResetQueryPool(cb, _pool, (uint)(slot * _maxZones * 2), (uint)(_maxZones * 2));
    }

    /// <summary>Before the command buffer ends: closes zones and labels left open (balanced for validation).</summary>
    internal void EndFrame(CommandBuffer cb)
    {
        if (_slot < 0)
            return;
        EndTo(cb, 0);
    }

    /// <summary>The frame was submitted: its zones are pending until the slot comes round again.</summary>
    internal void Submitted(long submitTicks)
    {
        if (_slot < 0)
            return;
        var s = _slots[_slot];
        s.SubmitTicks = submitTicks;
        s.Pending = s.Count > 0 || s.Recorded;
        if (s.Recorded && s.Count > 0)
            Profiler.ExpectGpu(s.ProfilerFrame);
        _slot = -1;
    }

    /// <summary>Opens a zone; returns the stack depth to pass to <see cref="EndTo"/>, or -1 when nothing was recorded.</summary>
    internal int Begin(CommandBuffer cb, ushort marker)
    {
        if (_slot < 0 || _disposed || cb.Handle == 0 || _stackDepth >= MaxStack)
            return -1;
        var timed = _frameTimed;
        if (!timed && _debugUtils is null)
            return -1;
        var token = _stackDepth;
        var zone = -1;
        if (timed)
        {
            var s = _slots[_slot];
            if (s.Count < _maxZones)
            {
                zone = s.Count++;
                s.Markers[zone] = marker;
                s.Depths[zone] = (byte)Math.Min(254, _timedDepth);
                _timedDepth++;
                _vk.CmdWriteTimestamp(cb, PipelineStageFlags.TopOfPipeBit, _pool, (uint)(_slot * _maxZones * 2 + zone * 2));
            }
            else
            {
                _dropped++;
            }
        }

        _stackZone[_stackDepth++] = zone;
        if (_debugUtils is { } debug)
        {
            var label = new DebugUtilsLabelEXT { SType = StructureType.DebugUtilsLabelExt, PLabelName = Label(marker) };
            var colour = CategoryColour(Profiler.MarkerAt(marker).Category);
            label.Color[0] = colour.X;
            label.Color[1] = colour.Y;
            label.Color[2] = colour.Z;
            label.Color[3] = 1f;
            debug.CmdBeginDebugUtilsLabel(cb, &label);
        }

        return token;
    }

    /// <summary>Closes zones until the stack is back at <paramref name="token"/> (a token from <see cref="Begin"/>).</summary>
    internal void EndTo(CommandBuffer cb, int token)
    {
        if (token < 0 || _slot < 0)
            return;
        while (_stackDepth > token)
        {
            var zone = _stackZone[--_stackDepth];
            if (zone >= 0)
            {
                _timedDepth--;
                _vk.CmdWriteTimestamp(cb, PipelineStageFlags.BottomOfPipeBit, _pool, (uint)(_slot * _maxZones * 2 + zone * 2 + 1));
            }

            _debugUtils?.CmdEndDebugUtilsLabel(cb);
        }
    }

    // The marker's name as a NUL-terminated UTF-8 string, marshalled once per marker.
    private byte* Label(ushort marker)
    {
        var label = _labels[marker];
        if (label == 0)
            _labels[marker] = label = Marshal.StringToCoTaskMemUTF8(Profiler.MarkerAt(marker).Name);
        return (byte*)label;
    }

    /// <summary>The colour of a category's bars (overlay, editor) and debug labels (linear-ish RGB, 0–1).</summary>
    public static System.Numerics.Vector3 CategoryColour(ProfilerCategory category) => category switch
    {
        ProfilerCategory.Engine => new(0.55f, 0.60f, 0.70f),
        ProfilerCategory.Script => new(0.38f, 0.65f, 0.98f),
        ProfilerCategory.Physics => new(0.98f, 0.62f, 0.25f),
        ProfilerCategory.Render => new(0.30f, 0.82f, 0.50f),
        ProfilerCategory.Ui => new(0.75f, 0.52f, 0.98f),
        ProfilerCategory.Audio => new(0.98f, 0.82f, 0.30f),
        ProfilerCategory.Loading => new(0.40f, 0.85f, 0.85f),
        ProfilerCategory.Network => new(0.98f, 0.45f, 0.62f),
        _ => new(0.85f, 0.85f, 0.85f),
    };

    // The smallest timestampValidBits of the graphics-capable families (0 = no timestamps).
    private static uint GraphicsTimestampBits(Vk vk, PhysicalDevice device)
    {
        uint familyCount = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, null);
        familyCount = Math.Min(familyCount, 64u);
        var families = stackalloc QueueFamilyProperties[(int)familyCount];
        vk.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, families);
        var bits = 64u;
        var any = false;
        for (var i = 0; i < familyCount; i++)
        {
            if ((families[i].QueueFlags & QueueFlags.GraphicsBit) == 0)
                continue;
            any = true;
            bits = Math.Min(bits, families[i].TimestampValidBits);
        }

        return any ? bits : 0;
    }

    /// <summary>Destroys the pool (device idle) and frees the label strings.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_pool.Handle != 0)
            _vk.DestroyQueryPool(_device, _pool, null);
        _pool = default;
        for (var i = 0; i < _labels.Length; i++)
        {
            if (_labels[i] != 0)
                Marshal.FreeCoTaskMem(_labels[i]);
            _labels[i] = 0;
        }
    }
}

/// <summary>An open GPU zone (<see cref="GpuProfiler.Zone"/>); disposing it closes the zone (and any opened inside it).</summary>
public ref struct GpuZone
{
    private readonly GpuProfiler? _profiler;
    private readonly CommandBuffer _cb;
    private readonly int _token;

    internal GpuZone(GpuProfiler profiler, CommandBuffer cb, int token)
    {
        _profiler = profiler;
        _cb = cb;
        _token = token;
    }

    public readonly void Dispose() => _profiler?.EndTo(_cb, _token);
}
