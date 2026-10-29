using System.Text;
using System.Text.Json;

namespace MainframeEngine.Tests.Profiling;

[Collection(nameof(SerialProfiler))]
public sealed class ChromeTraceTests : IDisposable
{
    public ChromeTraceTests() => Profiler.ResetForTests();

    public void Dispose() => Profiler.ResetForTests();

    // A trace on a 1 MHz clock (1 tick = 1 µs) with fixed values: the JSON is deterministic.
    private static ProfilerTrace SampleTrace()
    {
        var trace = new ProfilerTrace { TickFrequency = 1_000_000, Title = "Sample" };
        trace.Markers[1] = new ProfilerMarkerInfo(1, "Update", ProfilerCategory.Engine, ProfilerMarkerKind.Cpu, "", 0);
        trace.Markers[2] = new ProfilerMarkerInfo(2, "Enemy.Think", ProfilerCategory.Script, ProfilerMarkerKind.Cpu, "", 0);
        trace.Markers[3] = new ProfilerMarkerInfo(3, "Main pass", ProfilerCategory.Render, ProfilerMarkerKind.Gpu, "", 0);
        trace.Markers[4] = new ProfilerMarkerInfo(4, "Load scene", ProfilerCategory.Loading, ProfilerMarkerKind.Cpu, "", 0);
        trace.Counters[0] = new ProfilerCounterInfo(0, "Draw calls", ProfilerCounterUnit.Count);
        trace.Threads[0] = "Main";
        trace.Threads[1] = "Loader";
        trace.Events.Add(new ProfilerEvent(1000, 1500, 1, 0, 0));
        trace.Events.Add(new ProfilerEvent(1100, 1200, 2, 1, 0, 64));
        trace.Events.Add(new ProfilerEvent(1050, 1600, 4, 0, 1));
        trace.Events.Add(new ProfilerEvent(2000, 2400, 1, 0, 0));
        trace.GpuEvents.Add(new ProfilerEvent(1700, 1900, 3, 0, ProfilerEvent.GpuThread));
        var first = new ProfilerTraceFrame { Number = 7, StartTicks = 1000, EndTicks = 1650, CpuMs = 0.5f, WaitMs = 0.125f, GpuMs = 0.2f, EventStart = 0, EventCount = 3, GpuStart = 0, GpuCount = 1 };
        first.Counters.Add((0, 12));
        var second = new ProfilerTraceFrame { Number = 8, StartTicks = 2000, EndTicks = 2500, CpuMs = 33.5f, WaitMs = 0f, IsSpike = true, EventStart = 3, EventCount = 1, GpuStart = 1 };
        second.Counters.Add((0, 12)); // unchanged: no second counter event
        trace.Frames.Add(first);
        trace.Frames.Add(second);
        return trace;
    }

    private static string Write(ProfilerTrace trace)
    {
        using var stream = new MemoryStream();
        ChromeTraceWriter.Write(stream, trace);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void WritesTheGoldenJson()
    {
        var json = Write(SampleTrace());
        const string expected = """
            {"traceEvents":[
            {"name":"process_name","ph":"M","pid":1,"tid":0,"args":{"name":"Sample"}},
            {"name":"thread_name","ph":"M","pid":1,"tid":0,"args":{"name":"Main"}},
            {"name":"thread_name","ph":"M","pid":1,"tid":1,"args":{"name":"Loader"}},
            {"name":"process_name","ph":"M","pid":2,"tid":0,"args":{"name":"GPU (aligned to submit)"}},
            {"name":"thread_name","ph":"M","pid":2,"tid":0,"args":{"name":"GPU"}},
            {"name":"Frame 7","ph":"i","s":"p","pid":1,"tid":0,"ts":0,"args":{"frame":7,"end":650,"cpuMs":0.5,"waitMs":0.125,"gpuMs":0.2}},
            {"name":"Update","cat":"Engine","ph":"X","pid":1,"tid":0,"ts":0,"dur":500},
            {"name":"Enemy.Think","cat":"Script","ph":"X","pid":1,"tid":0,"ts":100,"dur":100,"args":{"alloc":64}},
            {"name":"Load scene","cat":"Loading","ph":"X","pid":1,"tid":1,"ts":50,"dur":550},
            {"name":"Main pass","cat":"Render","ph":"X","pid":2,"tid":0,"ts":700,"dur":200},
            {"name":"Draw calls","ph":"C","pid":1,"tid":0,"ts":0,"args":{"value":12}},
            {"name":"Frame 8","ph":"i","s":"p","pid":1,"tid":0,"ts":1000,"args":{"frame":8,"end":1500,"cpuMs":33.5,"waitMs":0,"spike":true}},
            {"name":"Update","cat":"Engine","ph":"X","pid":1,"tid":0,"ts":1000,"dur":400}
            ],"displayTimeUnit":"ms","otherData":{"producer":"Mainframe Engine profiler","version":"VERSION"}}
            """;
        var normalized = expected.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal)
            .Replace("VERSION", EngineInfo.Version, StringComparison.Ordinal);
        Assert.Equal(normalized, json.Replace("0.2000000029802322", "0.2", StringComparison.Ordinal));
        using var parsed = JsonDocument.Parse(json); // valid JSON
        Assert.Equal(13, parsed.RootElement.GetProperty("traceEvents").GetArrayLength());
    }

    [Fact]
    public void ReadsBackWhatItWrote()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Write(SampleTrace())));
        var trace = ChromeTraceReader.Read(stream);
        Assert.Equal("Sample", trace.Title);
        Assert.False(trace.GpuCalibrated);
        Assert.Equal(2, trace.Frames.Count);
        var first = trace.Frames[0];
        Assert.Equal(7, first.Number);
        Assert.Equal(0.5f, first.CpuMs);
        Assert.Equal(0.125f, first.WaitMs);
        Assert.Equal(0.2f, first.GpuMs, 3);
        Assert.Equal(0.65, trace.ToMilliseconds(first.EndTicks), 6);
        Assert.Equal(3, first.EventCount);
        Assert.Equal(1, first.GpuCount);
        Assert.True(trace.Frames[1].IsSpike);
        Assert.True(float.IsNaN(trace.Frames[1].GpuMs));

        var events = trace.Events.GetRange(first.EventStart, first.EventCount);
        Assert.Equal(["Update", "Enemy.Think", "Load scene"], events.Select(e => trace.MarkerName(e.Marker)));
        Assert.Equal([0, 1, 0], events.Select(e => (int)e.Depth));
        Assert.Equal(64, events[1].AllocatedBytes);
        Assert.Equal("Loader", trace.Threads[events[2].Thread]);
        Assert.Equal(ProfilerCategory.Script, trace.Markers[events[1].Marker].Category);
        Assert.Equal("Main pass", trace.MarkerName(trace.GpuEvents[first.GpuStart].Marker));
        Assert.Equal([(0, 12L)], trace.Frames[1].Counters.Select(c => ((int)c.Counter, c.Value)));
        Assert.Equal("Draw calls", trace.Counters[0].Name);
    }

    [Fact]
    public void RejectsWhatIsNotATrace()
    {
        using var notJson = new MemoryStream("nope"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => ChromeTraceReader.Read(notJson));
        using var noEvents = new MemoryStream("{\"a\":1}"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => ChromeTraceReader.Read(noEvents));
    }

    [Fact]
    public async Task SavesARecordedHistoryThatParses()
    {
        var marker = new ProfilerMarker("Trace.Work");
        var counter = new ProfilerCounter("Trace.Count");
        var token = Profiler.Acquire();
        for (var f = 0; f < 5; f++)
        {
            Profiler.BeginFrame();
            counter.Set(f);
            using (marker.Auto())
            {
            }

            Profiler.EndFrame(1f, 0f);
        }

        Profiler.Release(token);
        var directory = Directory.CreateTempSubdirectory("mf-trace-");
        try
        {
            var path = Path.Combine(directory.FullName, "sub", "trace.json");
            await Profiler.SaveTraceAsync(path, lastFrames: 3, title: "Test game");
            var trace = ChromeTraceReader.Load(path);
            Assert.Equal("Test game", trace.Title);
            Assert.Equal(3, trace.Frames.Count);
            Assert.All(trace.Frames, f => Assert.Contains(trace.Events.GetRange(f.EventStart, f.EventCount), e => trace.MarkerName(e.Marker) == "Trace.Work"));
            var id = trace.Counters.Single(c => c.Value.Name == "Trace.Count").Key;
            Assert.Equal([2L, 3L, 4L], trace.Frames.Select(f => f.Counters.Single(c => c.Counter == id).Value));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
