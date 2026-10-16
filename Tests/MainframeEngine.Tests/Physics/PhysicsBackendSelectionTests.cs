using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>Which library a space runs on (B2: Jitter2; selecting Jolt falls back with one warning per server).</summary>
public sealed class PhysicsBackendSelectionTests
{
    [Fact]
    public void Jitter2IsTheDefaultBackend()
    {
        using var h = new PhysicsHarness3D();
        h.AddFloor();
        Assert.Equal(PhysicsBackendKind.Jitter2, h.Space.BackendKind);
    }

    [Fact]
    public void SelectingJoltRunsJitter2AndWarnsOncePerServer()
    {
        var sink = new MemoryLogSink { Levels = Log.Level.Warning };
        Log.AddSink(sink);
        try
        {
            using var h = new PhysicsHarness3D(new PhysicsSettings3D { Backend = PhysicsBackendKind.Jolt, MultiThreaded = false });
            h.AddFloor();
            var viewport = h.Add(new SceneViewport { Name = "Sub" });
            viewport.AddChild(new StaticBody3D { Name = "Late" }); // a second space of the same server
            var box = h.AddBox(new Vector3(0, 2, 0));
            h.RunSeconds(2f);

            Assert.Equal(2, h.Server.Spaces.Count);
            Assert.All(h.Server.Spaces, s => Assert.Equal(PhysicsBackendKind.Jitter2, s.BackendKind));
            Assert.InRange(box.Position.Y, 0.45f, 0.55f);
            Assert.Single(sink.Snapshot(), e => e.Message.Contains("Jolt is not available on", StringComparison.Ordinal) &&
                                                e.Message.Contains("using Jitter2.", StringComparison.Ordinal));
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }
}
