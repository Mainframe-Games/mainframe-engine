using MainframeEngine.EditorLink;

namespace MainframeEngine.Editor;

/// <summary>A counter as the Profiler tab knows it (from a game's ProfileMarkers, the editor's registry, or a trace).</summary>
public readonly record struct ProfilerCounterName(ushort Id, string Name, ProfilerCounterUnit Unit);

/// <summary>
/// What the editor's Profiler tab holds (G10): the frames received from one source (a running game over the editor link,
/// the editor process itself, or an opened trace), with the markers, counters and thread names they refer to. Times are
/// microseconds (<see cref="EditorLinkProfileFrame"/>). A ring of at most <see cref="MaxFrames"/> frames, oldest dropped;
/// only wire data, never game objects, so code reload is unaffected. Main thread.
/// </summary>
public sealed class ProfilerCapture
{
    /// <summary>Frames kept (60 s at 60 Hz).</summary>
    public const int MaxFrames = 3600;

    private readonly EditorLinkProfileFrame?[] _frames = new EditorLinkProfileFrame?[MaxFrames];
    private int _first;
    private int _count;
    private long _lastLocal = -1;

    public Dictionary<ushort, EditorLinkProfileMarker> Markers { get; } = [];

    public Dictionary<ushort, ProfilerCounterName> Counters { get; } = [];

    public Dictionary<byte, string> Threads { get; } = new() { [0] = "Main" };

    /// <summary>Frames held (index 0 = the oldest).</summary>
    public int Count => _count;

    public EditorLinkProfileFrame this[int index] =>
        (uint)index < (uint)_count ? _frames[(_first + index) % MaxFrames]! : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Frames the game dropped because the editor fell behind (ProfileDropped).</summary>
    public long DroppedFrames { get; private set; }

    /// <summary>Frames that fell out of the ring (more than <see cref="MaxFrames"/> received).</summary>
    public long DiscardedFrames { get; private set; }

    /// <summary>Changes on every add or clear (the panel's refresh key).</summary>
    public int Version { get; private set; }

    /// <summary>GPU timestamps were sampled at encoder boundaries for the newest frame (in-pass zones show "≈").</summary>
    public bool EncoderBoundaries { get; private set; }

    public string MarkerName(ushort id) => Markers.TryGetValue(id, out var marker) ? marker.Name : "#" + id;

    public void Clear()
    {
        Array.Clear(_frames);
        _first = 0;
        _count = 0;
        DroppedFrames = 0;
        DiscardedFrames = 0;
        Version++;
    }

    /// <summary>Forgets the markers, counters and threads too (another source).</summary>
    public void Reset()
    {
        Clear();
        Markers.Clear();
        Counters.Clear();
        Threads.Clear();
        Threads[0] = "Main";
        _lastLocal = -1;
    }

    /// <summary>Takes a profiler message from a game (markers, a frame, a drop report).</summary>
    public void Add(in EditorLinkMessage message)
    {
        switch (message.Type)
        {
            case EditorLinkMessageType.ProfileMarkers when message.ProfileMarkers is { } markers:
                foreach (var marker in markers)
                {
                    if (marker.Kind == ProfilerMarkerKind.Counter)
                        Counters[marker.Id] = new ProfilerCounterName(marker.Id, marker.Name, (ProfilerCounterUnit)Math.Clamp(marker.Line, 0, 255));
                    else
                        Markers[marker.Id] = marker;
                }

                break;
            case EditorLinkMessageType.ProfileFrame when message.ProfileFrame is { } frame:
                Add(frame);
                break;
            case EditorLinkMessageType.ProfileDropped:
                DroppedFrames += message.DroppedCount;
                Version++;
                break;
        }
    }

    public void Add(EditorLinkProfileFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        foreach (var (thread, name) in frame.Threads)
            Threads[thread] = name;
        EncoderBoundaries = frame.EncoderBoundaries;
        if (_count == MaxFrames)
        {
            _frames[_first] = frame;
            _first = (_first + 1) % MaxFrames;
            DiscardedFrames++;
        }
        else
        {
            _frames[(_first + _count) % MaxFrames] = frame;
            _count++;
        }

        Version++;
    }

    /// <summary>The "Editor" source: copies the editor process's own frames recorded since the last call.</summary>
    public int AddLocal(ProfilerSnapshot snapshot, bool encoderBoundaries)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        for (var id = (ushort)Markers.Count; id < snapshot.MarkerCount; id++)
        {
            var info = snapshot.GetMarker(id);
            Markers[id] = new EditorLinkProfileMarker(id, info.Category, info.Kind, info.Name, info.File, info.Line);
        }

        for (var id = (ushort)Counters.Count; id < snapshot.CounterCount; id++)
        {
            var info = snapshot.GetCounterInfo(id);
            Counters[id] = new ProfilerCounterName(id, info.Name, info.Unit);
        }

        var added = 0;
        var count = snapshot.FrameCount;
        var first = count;
        while (first > 0 && snapshot.GetFrame(first - 1).Number > _lastLocal)
            first--;
        for (var i = first; i < count; i++)
        {
            var frame = snapshot.GetFrame(i);
            if (frame.GpuPending && i >= count - 4)
                break;
            _lastLocal = frame.Number;
            Add(FromLocal(snapshot, frame, encoderBoundaries));
            added++;
        }

        return added;
    }

    private EditorLinkProfileFrame FromLocal(ProfilerSnapshot snapshot, in ProfilerFrame frame, bool encoderBoundaries)
    {
        var events = snapshot.GetEvents(frame);
        var cpu = new ProfilerEvent[events.Length];
        var threads = new List<(byte, string)>();
        for (var i = 0; i < events.Length; i++)
        {
            var e = events[i];
            cpu[i] = new ProfilerEvent(Micro(e.Start), Micro(e.End), e.Marker, e.Depth, e.Thread, e.AllocatedBytes);
            if (!Threads.ContainsKey(e.Thread) && snapshot.GetThreadName(e.Thread) is { } name)
            {
                Threads[e.Thread] = name;
                threads.Add((e.Thread, name));
            }
        }

        var zones = snapshot.GetGpuZones(frame);
        var gpu = new ProfilerEvent[zones.Length];
        for (var i = 0; i < zones.Length; i++)
            gpu[i] = new ProfilerEvent(Micro(zones[i].Start), Micro(zones[i].End), zones[i].Marker, zones[i].Depth, ProfilerEvent.GpuThread);
        var counters = new (ushort, long)[snapshot.CounterCount];
        for (var c = 0; c < counters.Length; c++)
            counters[c] = ((ushort)c, snapshot.GetCounter(frame, (ushort)c));
        return new EditorLinkProfileFrame
        {
            Frame = (ulong)frame.Number,
            StartMicroseconds = Micro(frame.StartTicks),
            EndMicroseconds = Micro(frame.EndTicks),
            CpuMs = frame.CpuMs,
            WaitMs = frame.WaitMs,
            GpuMs = frame.GpuMs,
            IsSpike = frame.IsSpike,
            EncoderBoundaries = encoderBoundaries,
            Counters = counters,
            Threads = [.. threads],
            CpuEvents = cpu,
            GpuZones = gpu,
        };

        static long Micro(long ticks) => (long)Math.Round(ticks * 1e6 / System.Diagnostics.Stopwatch.Frequency);
    }

    /// <summary>Adds <paramref name="first"/> .. <paramref name="last"/>'s events into <paramref name="cpu"/> and <paramref name="gpu"/> (cleared first).</summary>
    public void Accumulate(int first, int last, ProfilerStatistics cpu, ProfilerStatistics gpu)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentNullException.ThrowIfNull(gpu);
        cpu.Clear();
        gpu.Clear();
        for (var i = Math.Max(0, first); i <= Math.Min(last, _count - 1); i++)
        {
            var frame = this[i];
            cpu.Add(frame.CpuEvents);
            cpu.Frames++;
            gpu.Add(frame.GpuZones);
            gpu.Frames++;
        }
    }

    /// <summary>The frames as a <see cref="ProfilerTrace"/> (µs ticks), for <see cref="ChromeTraceWriter"/>.</summary>
    public ProfilerTrace ToTrace(string title, int first = 0, int last = int.MaxValue)
    {
        var trace = new ProfilerTrace { TickFrequency = 1_000_000, Title = title };
        foreach (var (id, marker) in Markers)
            trace.Markers[id] = new ProfilerMarkerInfo(id, marker.Name, marker.Category, marker.Kind, marker.File, marker.Line);
        foreach (var (id, counter) in Counters)
            trace.Counters[id] = new ProfilerCounterInfo(id, counter.Name, counter.Unit);
        foreach (var (id, name) in Threads)
            trace.Threads[id] = name;
        for (var i = Math.Max(0, first); i <= Math.Min(last, _count - 1); i++)
        {
            var frame = this[i];
            var traced = new ProfilerTraceFrame
            {
                Number = (long)frame.Frame,
                StartTicks = frame.StartMicroseconds,
                EndTicks = frame.EndMicroseconds,
                CpuMs = frame.CpuMs,
                WaitMs = frame.WaitMs,
                GpuMs = frame.GpuMs,
                IsSpike = frame.IsSpike,
                EventStart = trace.Events.Count,
                EventCount = frame.CpuEvents.Length,
                GpuStart = trace.GpuEvents.Count,
                GpuCount = frame.GpuZones.Length,
            };
            traced.Counters.AddRange(frame.Counters);
            trace.Events.AddRange(frame.CpuEvents);
            trace.GpuEvents.AddRange(frame.GpuZones);
            trace.Frames.Add(traced);
        }

        return trace;
    }

    /// <summary>Replaces the capture with <paramref name="trace"/>'s frames (Open trace).</summary>
    public void Load(ProfilerTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        Reset();
        foreach (var (id, marker) in trace.Markers)
            Markers[id] = new EditorLinkProfileMarker(id, marker.Category, marker.Kind, marker.Name, marker.File, marker.Line);
        foreach (var (id, counter) in trace.Counters)
            Counters[id] = new ProfilerCounterName(id, counter.Name, counter.Unit);
        foreach (var (id, name) in trace.Threads)
            Threads[id] = name;
        var scale = 1e6 / trace.TickFrequency;
        long Micro(long ticks) => (long)Math.Round(ticks * scale);
        var frames = trace.Frames.Count > MaxFrames ? trace.Frames.GetRange(trace.Frames.Count - MaxFrames, MaxFrames) : trace.Frames;
        foreach (var f in frames)
        {
            var cpu = new ProfilerEvent[f.EventCount];
            for (var i = 0; i < cpu.Length; i++)
            {
                var e = trace.Events[f.EventStart + i];
                cpu[i] = new ProfilerEvent(Micro(e.Start), Micro(e.End), e.Marker, e.Depth, e.Thread, e.AllocatedBytes);
            }

            var gpu = new ProfilerEvent[f.GpuCount];
            for (var i = 0; i < gpu.Length; i++)
            {
                var e = trace.GpuEvents[f.GpuStart + i];
                gpu[i] = new ProfilerEvent(Micro(e.Start), Micro(e.End), e.Marker, e.Depth, ProfilerEvent.GpuThread);
            }

            Add(new EditorLinkProfileFrame
            {
                Frame = (ulong)Math.Max(0, f.Number),
                StartMicroseconds = Micro(f.StartTicks),
                EndMicroseconds = Micro(f.EndTicks),
                CpuMs = f.CpuMs,
                WaitMs = f.WaitMs,
                GpuMs = f.GpuMs,
                IsSpike = f.IsSpike,
                Counters = [.. f.Counters],
                CpuEvents = cpu,
                GpuZones = gpu,
            });
        }
    }
}
