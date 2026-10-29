namespace MainframeEngine.Tests.Profiling;

/// <summary><c>--profile</c>, <c>--profile-trace</c> and <c>--profile-frames</c> (G10), parsed and run headless.</summary>
[Collection(nameof(SerialProfiler))] // also configures the catalogs (HeadlessHost): nothing else runs alongside
public sealed class ProfilerCommandLineTests : IDisposable
{
    private static readonly ProfilerMarker Tick = new("CommandLine.Tick");

    public ProfilerCommandLineTests() => Profiler.ResetForTests();

    public void Dispose() => Profiler.ResetForTests();

    [Fact]
    public void FlagsParseAndRecordFromTheFirstFrame()
    {
        var options = GameHostOptions.Parse(["--profile-trace", "out.json", "--profile-frames", "20"]);
        Assert.Equal("out.json", options.ProfileTracePath);
        Assert.Equal(20, options.ProfileFrames);
        var engine = options.Apply(new EngineOptions { GameName = "T" });
        Assert.True(engine.Profiler.RecordFromStart);
        Assert.True(engine.Profiler.HistoryFrames >= 28);
        Assert.True(GameHostOptions.Parse(["--profile"]).Apply(new EngineOptions { GameName = "T" }).Profiler.RecordFromStart);
        Assert.False(new GameHostOptions().Apply(new EngineOptions { GameName = "T" }).Profiler.RecordFromStart);
        Assert.Throws<ArgumentException>(() => GameHostOptions.Parse(["--profile-frames", "5"]));
        Assert.Throws<ArgumentException>(() => GameHostOptions.Parse(["--profile-frames", "0", "--profile-trace", "a.json"]));
    }

    [Fact]
    public void HeadlessWritesTheTraceAfterProfileFramesAndQuits()
    {
        var directory = Directory.CreateTempSubdirectory("mf-headless-trace-");
        try
        {
            var path = Path.Combine(directory.FullName, "trace.json");
            var settings = new ProjectSettings { Name = "TraceTest" };
            settings.Audio.Enabled = false;
            using var host = new HeadlessHost(settings, GameHostOptions.Parse(["--fixed-fps", "240", "--profile-trace", path, "--profile-frames", "12"]));
            host.Tree.Root.AddChild(new ScopeNode());
            Assert.Equal(ExitCode.Ok, host.Run());
            Assert.Equal(12u, host.Frames);

            var trace = ChromeTraceReader.Load(path);
            Assert.Equal("TraceTest", trace.Title);
            Assert.Equal(12, trace.Frames.Count);
            Assert.All(trace.Frames, f =>
            {
                var names = trace.Events.GetRange(f.EventStart, f.EventCount).Select(e => trace.MarkerName(e.Marker)).ToHashSet();
                Assert.Contains("Frame", names);
                Assert.Contains("Tree.Tick", names);
                Assert.Contains("Process", names);
                Assert.Contains("CommandLine.Tick", names);
            });
            Assert.Contains(trace.Counters.Values, c => c.Name == "Nodes");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class ScopeNode : Node
    {
        protected override void OnProcess(in GameTime gameTime)
        {
            using var _ = Tick.Auto();
        }
    }
}
