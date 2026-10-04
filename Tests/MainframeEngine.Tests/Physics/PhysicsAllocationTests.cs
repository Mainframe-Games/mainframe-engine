using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>
/// The physics allocation gate: steady-state physics frames over ~500 bodies (stepping, interpolation, contact
/// monitors, areas, characters, debug draw, queries) allocate no managed memory on the main thread.
/// </summary>
public sealed class PhysicsAllocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SteadyState3DPhysicsDoesNotAllocate(bool multiThreaded)
    {
        using var h = new PhysicsHarness3D(new PhysicsSettings3D { MultiThreaded = multiThreaded });
        h.Server.DebugDrawEnabled = true;
        h.AddFloor(size: 200);
        // 480 boxes in a loose grid falling onto the floor and stacking a little; some monitor contacts.
        for (var i = 0; i < 480; i++)
        {
            var box = h.AddBox(new Vector3(i % 24 * 1.6f - 19, 0.5f + i / 240 * 1.2f, i / 24 % 10 * 1.6f - 8), name: $"Box{i}");
            box.ContactMonitor = i % 8 == 0;
            box.CanSleep = i % 2 == 0; // half keep simulating forever
        }

        var area = h.AddArea(new Vector3(0, 1, 0), new Vector3(10, 2, 10));
        var entered = 0;
        area.BodyEntered += _ => entered++;
        for (var i = 0; i < 4; i++)
        {
            var character = new WalkerCharacter3D { Name = $"Walker{i}", Position = new Vector3(-30 + i * 3, 1, 30), Walk = new Vector3(0.5f, 0, -0.2f) };
            character.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.4f, Height = 1.8f } });
            h.Add(character);
        }

        var query = h.Query;
        var results = new List<CollisionObject3D>(64);
        var probe = new SphereShape3D { Radius = 0.5f };
        var probeAt = Transform3D.FromTrs(new Vector3(0, 5, 0), Quaternion.Identity, Vector3.One);

        void Frame(float delta)
        {
            h.Run(1, delta);
            query.RayCast(new Vector3(0.3f, 10, 0.3f), new Vector3(0.3f, -10, 0.3f), out _);
            query.ShapeCast(probe, probeAt, new Vector3(0, -10, 0), out _);
            results.Clear();
            query.IntersectShape(probe, probeAt, results);
            query.IntersectPoint(new Vector3(0.1f, 0.1f, 0.1f), results);
            h.Tree.Root.DebugLines.Clear(); // what the render server does each frame
        }

        // Warm-up: bodies settle, lists and pools reach their size, everything is JIT-compiled. Mixed frame lengths
        // exercise interpolation with zero, one and two steps per frame.
        for (var i = 0; i < 240; i++)
            Frame(i % 3 == 0 ? 1f / 90 : 1f / 40);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 120; i++)
            Frame(i % 3 == 0 ? 1f / 90 : 1f / 40);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated == 0, $"steady-state 3D physics frames allocated {allocated} bytes");
        Assert.True(h.Space.ObjectCount > 480);
    }
}
