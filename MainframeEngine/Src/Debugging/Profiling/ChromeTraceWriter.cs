using System.Globalization;
using System.Text.Json;

namespace MainframeEngine;

/// <summary>
/// Writes a <see cref="ProfilerTrace"/> in Chrome's Trace Event Format (JSON object form), which Perfetto
/// (<c>ui.perfetto.dev</c>) and <c>chrome://tracing</c> open: complete events (<c>"ph":"X"</c>, µs) per scope, thread and
/// process names (<c>"ph":"M"</c>; the CPU lanes are process 1, the GPU lane process 2, labelled "aligned to submit" or
/// "calibrated"), counters as <c>"ph":"C"</c> (written when they change) and a <c>"Frame n"</c> instant event at each
/// frame start whose <c>args</c> hold the frame's times (and <c>spike</c>).
/// </summary>
public static class ChromeTraceWriter
{
    /// <summary>Process id of the CPU lanes.</summary>
    public const int CpuProcess = 1;

    /// <summary>Process id of the GPU lane.</summary>
    public const int GpuProcess = 2;

    /// <summary>Writes <paramref name="trace"/> to <paramref name="path"/> (the directory is created).</summary>
    public static void Save(string path, ProfilerTrace trace)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
        using var stream = File.Create(path);
        Write(stream, trace);
    }

    public static void Write(Stream stream, ProfilerTrace trace)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(trace);
        using var json = new Utf8JsonWriter(stream);
        json.WriteStartObject();
        json.WriteStartArray("traceEvents");

        Metadata(json, "process_name", CpuProcess, 0, trace.Title);
        foreach (var (thread, name) in trace.Threads.OrderBy(t => t.Key))
            Metadata(json, "thread_name", CpuProcess, thread, name);
        Metadata(json, "process_name", GpuProcess, 0, trace.GpuCalibrated ? "GPU (calibrated)" : "GPU (aligned to submit)");
        Metadata(json, "thread_name", GpuProcess, 0, "GPU");

        var lastCounters = new Dictionary<ushort, long>();
        foreach (var frame in trace.Frames)
        {
            json.WriteStartObject();
            json.WriteString("name", "Frame " + frame.Number.ToString(CultureInfo.InvariantCulture));
            json.WriteString("ph", "i");
            json.WriteString("s", "p");
            json.WriteNumber("pid", CpuProcess);
            json.WriteNumber("tid", 0);
            json.WriteNumber("ts", Microseconds(trace, frame.StartTicks));
            json.WriteStartObject("args");
            json.WriteNumber("frame", frame.Number);
            json.WriteNumber("end", Microseconds(trace, frame.EndTicks));
            json.WriteNumber("cpuMs", Round(frame.CpuMs));
            json.WriteNumber("waitMs", Round(frame.WaitMs));
            if (float.IsFinite(frame.GpuMs))
                json.WriteNumber("gpuMs", Round(frame.GpuMs));
            if (frame.IsSpike)
                json.WriteBoolean("spike", true);
            json.WriteEndObject();
            json.WriteEndObject();

            for (var i = frame.EventStart; i < frame.EventStart + frame.EventCount && i < trace.Events.Count; i++)
                Complete(json, trace, trace.Events[i], CpuProcess, trace.Events[i].Thread);
            for (var i = frame.GpuStart; i < frame.GpuStart + frame.GpuCount && i < trace.GpuEvents.Count; i++)
                Complete(json, trace, trace.GpuEvents[i], GpuProcess, 0);

            foreach (var (counter, value) in frame.Counters)
            {
                if (lastCounters.TryGetValue(counter, out var last) && last == value)
                    continue;
                lastCounters[counter] = value;
                json.WriteStartObject();
                json.WriteString("name", trace.Counters.TryGetValue(counter, out var info) ? info.Name : "Counter " + counter.ToString(CultureInfo.InvariantCulture));
                json.WriteString("ph", "C");
                json.WriteNumber("pid", CpuProcess);
                json.WriteNumber("tid", 0);
                json.WriteNumber("ts", Microseconds(trace, frame.StartTicks));
                json.WriteStartObject("args");
                json.WriteNumber("value", value);
                json.WriteEndObject();
                json.WriteEndObject();
            }
        }

        json.WriteEndArray();
        json.WriteString("displayTimeUnit", "ms");
        json.WriteStartObject("otherData");
        json.WriteString("producer", "Mainframe Engine profiler");
        json.WriteString("version", EngineInfo.Version);
        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static void Metadata(Utf8JsonWriter json, string kind, int pid, int tid, string name)
    {
        json.WriteStartObject();
        json.WriteString("name", kind);
        json.WriteString("ph", "M");
        json.WriteNumber("pid", pid);
        json.WriteNumber("tid", tid);
        json.WriteStartObject("args");
        json.WriteString("name", name);
        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static void Complete(Utf8JsonWriter json, ProfilerTrace trace, in ProfilerEvent e, int pid, int tid)
    {
        trace.Markers.TryGetValue(e.Marker, out var marker);
        json.WriteStartObject();
        json.WriteString("name", marker.Name ?? "(unknown)");
        json.WriteString("cat", CategoryName(marker.Category));
        json.WriteString("ph", "X");
        json.WriteNumber("pid", pid);
        json.WriteNumber("tid", tid);
        json.WriteNumber("ts", Microseconds(trace, e.Start));
        json.WriteNumber("dur", Math.Max(0, Round((e.End - e.Start) * 1e6 / trace.TickFrequency)));
        if (e.AllocatedBytes != 0)
        {
            json.WriteStartObject("args");
            json.WriteNumber("alloc", e.AllocatedBytes);
            json.WriteEndObject();
        }

        json.WriteEndObject();
    }

    /// <summary>The category's name as the trace writes it (<c>"cat"</c>).</summary>
    public static string CategoryName(ProfilerCategory category) => category switch
    {
        ProfilerCategory.Engine => "Engine",
        ProfilerCategory.Script => "Script",
        ProfilerCategory.Physics => "Physics",
        ProfilerCategory.Render => "Render",
        ProfilerCategory.Ui => "Ui",
        ProfilerCategory.Audio => "Audio",
        ProfilerCategory.Loading => "Loading",
        ProfilerCategory.Network => "Network",
        _ => "Game",
    };

    private static double Microseconds(ProfilerTrace trace, long ticks) => Round((ticks - trace.Origin) * 1e6 / trace.TickFrequency);

    private static double Round(double value) => Math.Round(value, 3);
}

/// <summary>Reads a trace written by <see cref="ChromeTraceWriter"/> back into a <see cref="ProfilerTrace"/> (the editor's Open trace).</summary>
public static class ChromeTraceReader
{
    /// <summary>Ticks per second of a read trace (ns: the writer keeps µs with three decimals).</summary>
    public const long TickFrequency = 1_000_000_000;

    public static ProfilerTrace Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>Parses <paramref name="stream"/>; throws <see cref="InvalidDataException"/> when it is not a trace.</summary>
    public static ProfilerTrace Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stream);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Not a JSON trace: " + e.Message, e);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("traceEvents", out var events) || events.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Not a Chrome trace: no \"traceEvents\" array.");
            var trace = new ProfilerTrace { TickFrequency = TickFrequency };
            var markerIds = new Dictionary<(string, string), ushort>();
            var counterIds = new Dictionary<string, ushort>();
            var cpu = new List<ProfilerEvent>();
            var gpu = new List<ProfilerEvent>();
            var counterEvents = new List<(long Ticks, ushort Counter, long Value)>();
            foreach (var e in events.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("ph", out var phase))
                    continue;
                var ph = phase.GetString();
                var name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var pid = e.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
                var tid = e.TryGetProperty("tid", out var t) && t.TryGetInt32(out var tv) ? tv : 0;
                var ts = e.TryGetProperty("ts", out var tsv) && tsv.TryGetDouble(out var tsd) ? Ticks(tsd) : 0;
                switch (ph)
                {
                    case "M":
                        if (e.TryGetProperty("args", out var margs) && margs.TryGetProperty("name", out var mn) && mn.GetString() is { } metaName)
                        {
                            if (name == "process_name" && pid == ChromeTraceWriter.CpuProcess)
                                trace.Title = metaName;
                            else if (name == "process_name" && pid == ChromeTraceWriter.GpuProcess)
                                trace.GpuCalibrated = metaName.Contains("calibrated", StringComparison.Ordinal);
                            else if (name == "thread_name" && pid == ChromeTraceWriter.CpuProcess && tid is >= 0 and < 255)
                                trace.Threads[(byte)tid] = metaName;
                        }

                        break;
                    case "i" when name.StartsWith("Frame ", StringComparison.Ordinal) && e.TryGetProperty("args", out var fargs):
                        {
                            var frame = new ProfilerTraceFrame
                            {
                                Number = fargs.TryGetProperty("frame", out var fn) && fn.TryGetInt64(out var number) ? number : trace.Frames.Count,
                                StartTicks = ts,
                                EndTicks = fargs.TryGetProperty("end", out var fe) && fe.TryGetDouble(out var end) ? Ticks(end) : ts,
                                CpuMs = Float(fargs, "cpuMs"),
                                WaitMs = Float(fargs, "waitMs"),
                                GpuMs = fargs.TryGetProperty("gpuMs", out _) ? Float(fargs, "gpuMs") : float.NaN,
                                IsSpike = fargs.TryGetProperty("spike", out var spike) && spike.ValueKind == JsonValueKind.True,
                            };
                            trace.Frames.Add(frame);
                            break;
                        }

                    case "X":
                        {
                            var category = e.TryGetProperty("cat", out var c) ? c.GetString() ?? "" : "";
                            var dur = e.TryGetProperty("dur", out var d) && d.TryGetDouble(out var dd) ? Ticks(dd) : 0;
                            if (!markerIds.TryGetValue((name, category), out var id))
                            {
                                id = (ushort)Math.Min(ushort.MaxValue, markerIds.Count + 1);
                                markerIds[(name, category)] = id;
                                trace.Markers[id] = new ProfilerMarkerInfo(id, name, ParseCategory(category),
                                    pid == ChromeTraceWriter.GpuProcess ? ProfilerMarkerKind.Gpu : ProfilerMarkerKind.Cpu, "", 0);
                            }

                            var alloc = e.TryGetProperty("args", out var xargs) && xargs.TryGetProperty("alloc", out var a) && a.TryGetInt32(out var av) ? av : 0;
                            var thread = pid == ChromeTraceWriter.GpuProcess ? ProfilerEvent.GpuThread : (byte)Math.Clamp(tid, 0, 254);
                            (pid == ChromeTraceWriter.GpuProcess ? gpu : cpu).Add(new ProfilerEvent(ts, ts + dur, id, 0, thread, alloc));
                            break;
                        }

                    case "C" when e.TryGetProperty("args", out var cargs) && cargs.TryGetProperty("value", out var value) && value.TryGetInt64(out var v):
                        if (!counterIds.TryGetValue(name, out var counter))
                        {
                            counter = (ushort)counterIds.Count;
                            counterIds[name] = counter;
                            trace.Counters[counter] = new ProfilerCounterInfo(counter, name, ProfilerCounterUnit.Count);
                        }

                        counterEvents.Add((ts, counter, v));
                        break;
                }
            }

            trace.Frames.Sort((a, b) => a.StartTicks.CompareTo(b.StartTicks));
            Assign(trace, cpu, trace.Events, gpuLane: false);
            Assign(trace, gpu, trace.GpuEvents, gpuLane: true);
            AssignCounters(trace, counterEvents);
            return trace;
        }
    }

    // Puts each event in the frame it ended in (GPU zones: the frame they started after), per frame in begin order per
    // thread with nesting depths recomputed.
    private static void Assign(ProfilerTrace trace, List<ProfilerEvent> source, List<ProfilerEvent> into, bool gpuLane)
    {
        if (trace.Frames.Count == 0)
            return;
        var perFrame = new List<ProfilerEvent>[trace.Frames.Count];
        foreach (var e in source)
        {
            var key = gpuLane ? e.Start : e.End;
            var index = FrameIndex(trace, key);
            (perFrame[index] ??= []).Add(e);
        }

        for (var f = 0; f < perFrame.Length; f++)
        {
            var frame = trace.Frames[f];
            var list = perFrame[f] ?? [];
            list.Sort(static (a, b) => a.Thread != b.Thread ? a.Thread.CompareTo(b.Thread)
                : a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
            if (gpuLane)
                frame.GpuStart = into.Count;
            else
                frame.EventStart = into.Count;
            var stack = new Stack<long>();
            var thread = -1;
            foreach (var e in list)
            {
                if (e.Thread != thread)
                {
                    stack.Clear();
                    thread = e.Thread;
                }

                while (stack.Count > 0 && stack.Peek() <= e.Start)
                    stack.Pop();
                var depth = (byte)Math.Min(254, stack.Count);
                stack.Push(e.End);
                into.Add(new ProfilerEvent(e.Start, e.End, e.Marker, depth, e.Thread, e.AllocatedBytes));
            }

            if (gpuLane)
                frame.GpuCount = list.Count;
            else
                frame.EventCount = list.Count;
        }
    }

    private static void AssignCounters(ProfilerTrace trace, List<(long Ticks, ushort Counter, long Value)> events)
    {
        if (trace.Frames.Count == 0)
            return;
        events.Sort((a, b) => a.Ticks.CompareTo(b.Ticks));
        var current = new SortedDictionary<ushort, long>();
        var next = 0;
        foreach (var frame in trace.Frames)
        {
            while (next < events.Count && events[next].Ticks <= frame.StartTicks)
            {
                current[events[next].Counter] = events[next].Value;
                next++;
            }

            foreach (var (counter, value) in current)
                frame.Counters.Add((counter, value));
        }
    }

    private static int FrameIndex(ProfilerTrace trace, long ticks)
    {
        var lo = 0;
        var hi = trace.Frames.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (trace.Frames[mid].StartTicks <= ticks)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }

    private static long Ticks(double microseconds) => (long)Math.Round(microseconds * 1000.0);

    private static float Float(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.TryGetDouble(out var d) ? (float)d : 0f;

    private static ProfilerCategory ParseCategory(string category) =>
        Enum.TryParse<ProfilerCategory>(category, ignoreCase: false, out var parsed) ? parsed : ProfilerCategory.Game;
}
