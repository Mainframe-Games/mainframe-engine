using System.Diagnostics;

namespace MainframeEngine;

/// <summary>One frame of a <see cref="ProfilerTrace"/>: its times and the ranges of its events in the trace's lists.</summary>
public sealed class ProfilerTraceFrame
{
    public long Number { get; set; }
    public long StartTicks { get; set; }
    public long EndTicks { get; set; }
    public float CpuMs { get; set; }
    public float WaitMs { get; set; }
    public float GpuMs { get; set; } = float.NaN;
    public bool IsSpike { get; set; }

    /// <summary>The frame's CPU events are <see cref="ProfilerTrace.Events"/>[EventStart .. EventStart + EventCount).</summary>
    public int EventStart { get; set; }
    public int EventCount { get; set; }
    public int GpuStart { get; set; }
    public int GpuCount { get; set; }

    /// <summary>Counter values of the frame (counter id, value).</summary>
    public List<(ushort Counter, long Value)> Counters { get; } = [];
}

/// <summary>
/// A self-contained copy of profiler frames: what <see cref="ChromeTraceWriter"/> writes and <see cref="ChromeTraceReader"/>
/// reads back. <see cref="Profiler.CaptureTrace"/> makes one from the history (on the main thread; it allocates, a save is
/// not per-frame); the editor builds one from frames received over the editor link.
/// </summary>
public sealed class ProfilerTrace
{
    /// <summary>Ticks per second of every tick value (<see cref="Stopwatch.Frequency"/> for a capture).</summary>
    public long TickFrequency { get; set; } = Stopwatch.Frequency;

    /// <summary>The process name shown on the CPU lanes (the game).</summary>
    public string Title { get; set; } = "Mainframe Engine";

    /// <summary>GPU lane label: true when GPU times were calibrated against the CPU clock, false when aligned to each submit.</summary>
    public bool GpuCalibrated { get; set; }

    public List<ProfilerTraceFrame> Frames { get; } = [];

    /// <summary>CPU events (each frame's range in begin order per thread).</summary>
    public List<ProfilerEvent> Events { get; } = [];

    /// <summary>GPU zones (already on the CPU clock).</summary>
    public List<ProfilerEvent> GpuEvents { get; } = [];

    /// <summary>The markers the events name, by id.</summary>
    public Dictionary<ushort, ProfilerMarkerInfo> Markers { get; } = [];

    /// <summary>The counters the frames name, by id.</summary>
    public Dictionary<ushort, ProfilerCounterInfo> Counters { get; } = [];

    /// <summary>Thread lane names (0 = main).</summary>
    public Dictionary<byte, string> Threads { get; } = [];

    /// <summary>Milliseconds from the trace's start (its first frame) to <paramref name="ticks"/>.</summary>
    public double ToMilliseconds(long ticks) => (ticks - Origin) * 1000.0 / TickFrequency;

    /// <summary>The first frame's start, the zero of the trace's timeline.</summary>
    public long Origin => Frames.Count > 0 ? Frames[0].StartTicks : 0;

    public string MarkerName(ushort id) => Markers.TryGetValue(id, out var info) ? info.Name : "(unknown)";
}

public static partial class Profiler
{
    /// <summary>
    /// Copies the newest <paramref name="lastFrames"/> frames (0 = the whole history) with their events, GPU zones,
    /// counters, markers and threads (main thread; allocates).
    /// </summary>
    public static ProfilerTrace CaptureTrace(int lastFrames = 0, string? title = null) => CaptureTrace(lastFrames, title, 0);

    /// <summary>Like <see cref="CaptureTrace(int, string?)"/>, leaving out the newest <paramref name="skipNewest"/> frames.</summary>
    internal static ProfilerTrace CaptureTrace(int lastFrames, string? title, int skipNewest)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(skipNewest);
        var trace = new ProfilerTrace { Title = title ?? "Mainframe Engine" };
        lock (s_gate)
        {
            if (s_history is not { } history)
                return trace;
            var newest = history.FramesWritten - Math.Min(skipNewest, history.FrameCount); // one past the newest kept
            var count = (int)Math.Min(history.FrameCount, newest - (history.FramesWritten - history.FrameCount));
            if (lastFrames > 0)
                count = Math.Min(count, lastFrames);
            for (var i = 0; i < count; i++)
            {
                ref readonly var record = ref history.FrameAt(newest - count + i);
                var frame = new ProfilerTraceFrame
                {
                    Number = record.Number,
                    StartTicks = record.StartTicks,
                    EndTicks = record.EndTicks,
                    CpuMs = record.CpuMs,
                    WaitMs = record.WaitMs,
                    GpuMs = record.GpuMs,
                    IsSpike = record.IsSpike,
                };
                var events = Snapshot.GetEvents(new ProfilerFrame(record));
                frame.EventStart = trace.Events.Count;
                frame.EventCount = events.Length;
                for (var e = 0; e < events.Length; e++)
                    Note(trace, events[e]);
                AddAll(trace.Events, events);
                var gpu = Snapshot.GetGpuZones(new ProfilerFrame(record));
                frame.GpuStart = trace.GpuEvents.Count;
                frame.GpuCount = gpu.Length;
                for (var e = 0; e < gpu.Length; e++)
                    Note(trace, gpu[e]);
                AddAll(trace.GpuEvents, gpu);
                var counters = history.CountersOf(newest - count + i);
                for (var c = 0; c < Math.Min(CounterCount, counters.Length); c++)
                {
                    frame.Counters.Add(((ushort)c, counters[c]));
                    trace.Counters.TryAdd((ushort)c, CounterAt((ushort)c));
                }

                trace.Frames.Add(frame);
            }
        }

        return trace;

        static void AddAll(List<ProfilerEvent> into, ProfilerEventSpans events)
        {
            foreach (var e in events.First)
                into.Add(e);
            foreach (var e in events.Second)
                into.Add(e);
        }

        static void Note(ProfilerTrace trace, in ProfilerEvent e)
        {
            trace.Markers.TryAdd(e.Marker, MarkerAt(e.Marker));
            if (e.Thread != ProfilerEvent.GpuThread && !trace.Threads.ContainsKey(e.Thread))
                trace.Threads[e.Thread] = ThreadName(e.Thread) ?? $"Thread {e.Thread}";
        }
    }

    /// <summary>
    /// Writes the newest <paramref name="lastFrames"/> frames (0 = the whole history) to <paramref name="path"/> as a Chrome
    /// trace (Perfetto, <c>chrome://tracing</c>): the history is copied now, on the calling (main) thread, and written on a
    /// worker. The directory is created.
    /// </summary>
    public static Task SaveTraceAsync(string path, int lastFrames = 0, string? title = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var trace = CaptureTrace(lastFrames, title);
        return Task.Run(() => ChromeTraceWriter.Save(path, trace));
    }
}
