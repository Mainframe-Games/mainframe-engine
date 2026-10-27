using System.Buffers;
using MainframeEngine.EditorLink;
using MainframeEngine.Tests.Project;

namespace MainframeEngine.Tests.Profiling;

/// <summary>Editor link protocol v5 (G10): profile markers, frames, drops and the profiler commands; streaming from a session.</summary>
[Collection(nameof(SerialProfiler))]
public sealed class ProfilerLinkTests : IDisposable
{
    private static readonly ProfilerMarker Work = new("Link.Work", ProfilerCategory.Script);
    private static readonly ProfilerCounter Things = new("Link.Things");

    public ProfilerLinkTests() => Profiler.ResetForTests();

    public void Dispose() => Profiler.ResetForTests();

    private static EditorLinkMessage RoundTrip(Action<ArrayBufferWriter<byte>> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        var frame = buffer.WrittenSpan;
        Assert.True(EditorLinkProtocol.TryReadHeader(frame, out var length));
        Assert.True(length <= EditorLinkProtocol.MaxFrameLength);
        Assert.True(EditorLinkProtocol.TryDecode(frame[EditorLinkProtocol.HeaderLength..], out var message));
        return message;
    }

    [Fact]
    public void MarkersRoundTrip()
    {
        var message = RoundTrip(b => EditorLinkProtocol.WriteProfileMarkers(b, Work.Id, Work.Id + 1));
        Assert.Equal(EditorLinkMessageType.ProfileMarkers, message.Type);
        var marker = Assert.Single(message.ProfileMarkers!);
        Assert.Equal((Work.Id, "Link.Work", ProfilerCategory.Script, ProfilerMarkerKind.Cpu), (marker.Id, marker.Name, marker.Category, marker.Kind));
        Assert.EndsWith("ProfilerLinkTests.cs", marker.File);
        Assert.True(marker.Line > 0);
    }

    [Fact]
    public void FramesRoundTripInMicrosecondsRelativeToTheirStart()
    {
        var block = new ProfileFrameBlock
        {
            Frame = 42,
            TickFrequency = 1_000_000_000, // ns ticks
            StartTicks = 5_000_000_000,
            EndTicks = 5_016_000_000,
            CpuMs = 3.5f,
            WaitMs = 12f,
            GpuMs = 2.25f,
            Flags = ProfileFrameBlock.SpikeFlag | ProfileFrameBlock.EncoderBoundariesFlag,
            CounterCount = 1,
            CpuCount = 2,
            GpuCount = 1,
            MarkerCount = Profiler.MarkerCount,
        };
        block.CounterIds[0] = Things.Id;
        block.CounterValues[0] = -7;
        block.Cpu[0] = new ProfilerEvent(5_000_000_000, 5_003_000_000, EngineMarkers.Frame.Id, 0, 0);
        block.Cpu[1] = new ProfilerEvent(5_001_000_000, 5_001_500_000, Work.Id, 1, 0, 128);
        block.Gpu[0] = new ProfilerEvent(5_004_000_000, 5_006_250_000, EngineMarkers.GpuMainPass.Id, 0, ProfilerEvent.GpuThread);

        var frame = RoundTrip(b => EditorLinkProtocol.WriteProfileFrame(b, block, [0])).ProfileFrame!;
        Assert.Equal(42UL, frame.Frame);
        Assert.Equal(5_000_000, frame.StartMicroseconds);
        Assert.Equal(5_016_000, frame.EndMicroseconds);
        Assert.Equal((3.5f, 12f, 2.25f), (frame.CpuMs, frame.WaitMs, frame.GpuMs));
        Assert.True(frame.IsSpike);
        Assert.True(frame.EncoderBoundaries);
        Assert.False(frame.Truncated);
        Assert.Equal([(Things.Id, -7L)], frame.Counters);
        Assert.Equal([((byte)0, "Main")], frame.Threads);
        Assert.Equal(2, frame.CpuEvents.Length);
        Assert.Equal((5_001_000L, 5_001_500L, Work.Id, (byte)1, 128), (frame.CpuEvents[1].Start, frame.CpuEvents[1].End, frame.CpuEvents[1].Marker, frame.CpuEvents[1].Depth, frame.CpuEvents[1].AllocatedBytes));
        var gpu = Assert.Single(frame.GpuZones);
        Assert.Equal((5_004_000L, 5_006_250L, ProfilerEvent.GpuThread), (gpu.Start, gpu.End, gpu.Thread));
    }

    [Fact]
    public void DropsAndCommandsRoundTrip()
    {
        Assert.Equal(9, RoundTrip(b => EditorLinkProtocol.WriteProfileDropped(b, 9)).DroppedCount);
        var start = new EditorLinkCommand(EditorCommandKind.ProfilerStart, "gpu=1;spike=33.3;freeze=0");
        Assert.Equal(start, RoundTrip(b => EditorLinkProtocol.WriteCommand(b, start)).Command);
        var stop = new EditorLinkCommand(EditorCommandKind.ProfilerStop);
        Assert.Equal(stop, RoundTrip(b => EditorLinkProtocol.WriteCommand(b, stop)).Command);
        Assert.Equal((false, 12.5f, true), ProfilerStreamer.ParseArgument("gpu=0; spike=12.5 ;freeze=1"));
        Assert.Equal((true, 0f, false), ProfilerStreamer.ParseArgument(""));
    }

    [Fact]
    public void AFrameTooBigForOneMessageKeepsItsShallowestDepthsAndIsFlagged()
    {
        using var client = new EditorLinkClient(1, new EditorLinkHello(EditorLinkProtocol.Version, 1, "T", "0")); // nobody listens
        using var streamer = new ProfilerStreamer(client);
        streamer.Start("gpu=1");
        Profiler.BeginFrame();
        for (var i = 0; i < EditorLinkProtocol.MaxProfileEvents / 2 + 10; i++)
        {
            using var outer = Work.Auto();
            using var inner = Work.Auto(); // depth 2: these go
        }

        Profiler.EndFrame(1f, 0f);
        Profiler.BeginFrame(); // the poll sends frames before the newest pending ones
        Profiler.EndFrame(1f, 0f);
        streamer.Poll();
        var block = client.TakePendingProfileBlock();
        Assert.NotNull(block);
        Assert.NotEqual(0, block.Flags & ProfileFrameBlock.TruncatedFlag);
        Assert.Equal(1 + EditorLinkProtocol.MaxProfileEvents / 2 + 10, block.CpuCount); // the frame and the outer scopes
        Assert.All(block.Cpu.AsSpan(0, block.CpuCount).ToArray(), e => Assert.True(e.Depth <= 1));
        var frame = RoundTrip(b => EditorLinkProtocol.WriteProfileFrame(b, block, [])).ProfileFrame!;
        Assert.True(frame.Truncated);
        client.ReturnProfileBlock(block);
    }

    [Fact]
    public void ASlowLinkDropsFramesAndTheGameNeverWaits()
    {
        using var client = new EditorLinkClient(1, new EditorLinkHello(EditorLinkProtocol.Version, 1, "T", "0")); // never connects
        using var streamer = new ProfilerStreamer(client);
        streamer.Start("");
        for (var f = 0; f < 30; f++)
        {
            Profiler.BeginFrame();
            using (Work.Auto())
            {
            }

            Profiler.EndFrame(1f, 0f);
            streamer.Poll();
        }

        Assert.Equal(EditorLinkClient.ProfileBlockCount, streamer.SentFrames);
        Assert.True(client.DroppedProfileFrames >= 20);
    }

    [Fact]
    public void ASessionStreamsFramesToTheEditorBetweenStartAndStop()
    {
        using var server = new EditorLinkServer();
        var tree = new SceneTree();
        try
        {
            using var session = new GameSession(tree, new ProjectSettings { Name = "LinkProfile" }, new GameHostOptions { EditorPort = server.Port });
            var seen = new List<EditorLinkMessage>();
            Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Hello);
            Assert.True(Wait.Until(() => session.Link!.IsConnected));

            session.Apply(new EditorLinkCommand(EditorCommandKind.ProfilerStart, "gpu=1;spike=0;freeze=0"));
            Assert.True(Profiler.IsRecording);
            var time = new GameTime { DeltaTime = 1f / 60f };
            for (var f = 0; f < 10; f++)
            {
                Profiler.BeginFrame();
                Things.Set(f);
                session.Update(time);
                using (Work.Auto())
                    tree.Tick(time);
                Profiler.EndFrame(2f, 0.25f);
            }

            var markers = Wait.For(server, seen, m => m.Type == EditorLinkMessageType.ProfileMarkers).ProfileMarkers!;
            Assert.Contains(markers, m => m.Name == "Link.Work");
            Assert.Contains(markers, m => m.Name == "Tree.Tick");
            var frame = Wait.For(server, seen, m => m.Type == EditorLinkMessageType.ProfileFrame && m.ProfileFrame!.CpuEvents.Length > 1).ProfileFrame!;
            Assert.Equal(2f, frame.CpuMs);
            Assert.Contains(frame.CpuEvents, e => e.Marker == Work.Id);
            Assert.Contains(frame.Counters, c => c.Counter == Things.Id);
            Assert.All(frame.CpuEvents, e => Assert.True(e.Start >= frame.StartMicroseconds && e.End <= frame.EndMicroseconds + 1));

            session.Apply(new EditorLinkCommand(EditorCommandKind.ProfilerStop));
            Assert.False(Profiler.IsRecording);
        }
        finally
        {
            tree.Shutdown();
        }
    }
}
