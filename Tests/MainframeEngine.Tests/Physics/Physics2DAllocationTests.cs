using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>
/// 2D allocation gate. Box2D.NET 3.1.654 allocates inside <c>b2World_Step</c> itself (a step context and solver arrays,
/// a few hundred bytes per step; ADR 0023), so the gate measures the engine's share: everything a steady-state frame
/// allocates minus what the library allocated inside its steps must be zero.
/// </summary>
[Collection(nameof(SerialBox2D))]
public sealed class Physics2DAllocationTests
{
    [Fact]
    public void SteadyState2DPhysicsAllocatesNothingBeyondBox2DItself()
    {
        using var h = new PhysicsHarness2D();
        h.Server.DebugDrawEnabled = true;
        h.AddGround(width: 20000);
        for (var i = 0; i < 480; i++)
        {
            var box = h.AddBox(new Vector2(i % 60 * 70 - 2100, 25 + i / 60 * 60), 50, $"Box{i}");
            box.ContactMonitor = i % 8 == 0;
            box.CanSleep = i % 2 == 0;
        }

        var area = h.AddArea(new Vector2(0, 100), new Vector2(1000, 200));
        var entered = 0;
        area.BodyEntered += _ => entered++;
        for (var i = 0; i < 4; i++)
        {
            var character = new WalkerCharacter2D { Name = $"Walker{i}", Position = new Vector2(3000 + i * 200, 100), Walk = new Vector2(40, 0) };
            character.AddChild(new CollisionShape2D { Shape = new CapsuleShape2D { Radius = 20, Height = 80 } });
            h.Add(character);
        }

        var query = h.Query;
        var results = new List<CollisionObject2D>(64);
        var probe = new CircleShape2D { Radius = 20 };
        var probeAt = Transform2D.FromTrs(new Vector2(0, 600), 0, Vector2.One);

        void Frame(float delta)
        {
            h.Run(1, delta);
            query.RayCast(new Vector2(30, 1000), new Vector2(30, -1000), out _);
            query.ShapeCast(probe, probeAt, new Vector2(0, -1000), out _);
            results.Clear();
            query.IntersectShape(probe, probeAt, results);
            query.IntersectPoint(new Vector2(10, 10), results);
            h.Tree.Root.DebugLines.Clear();
        }

        PhysicsSpace2D.MeasureLibraryAllocations = true;
        try
        {
            for (var i = 0; i < 240; i++)
                Frame(i % 3 == 0 ? 1f / 90 : 1f / 40);

            var libraryBefore = h.Space.LibraryAllocatedBytes;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 120; i++)
                Frame(i % 3 == 0 ? 1f / 90 : 1f / 40);
            var total = GC.GetAllocatedBytesForCurrentThread() - before;
            var library = h.Space.LibraryAllocatedBytes - libraryBefore;

            Assert.True(total - library == 0, $"2D frames allocated {total} bytes, {library} of them inside b2World_Step");
        }
        finally
        {
            PhysicsSpace2D.MeasureLibraryAllocations = false;
        }
    }
}
