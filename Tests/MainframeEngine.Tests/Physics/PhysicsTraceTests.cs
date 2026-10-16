using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MainframeEngine.Tests.Physics;

/// <summary>
/// The B2/B3 refactor gate (ADR 0192): a bit-exact trace of a Jitter2 scene (every body's pose, velocities and sleep
/// state, contact/area/sleep signals, the character's floor state and four queries, for 240 steps) compared with a trace
/// recorded before the backend seam existed. Jitter2 reproduces only on one machine and build (ADR 0020), so the file is
/// per RID with the runtime and configuration in its header, and the test runs only on request, alone in its process
/// (Jitter2's shape ids are process-wide):
/// <c>MF_PHYSICS_TRACE=1 dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsTraceTests"</c>.
/// <c>MF_PHYSICS_TRACE=record</c> rewrites the file (only from code whose behaviour is the reference). Every step's text
/// feeds a running SHA-256 written per step; every 10th step's text is written in full (set <see cref="WriteEvery"/> to
/// 1 locally to see the first differing value).
/// </summary>
public sealed class PhysicsTraceTests
{
    private const int Steps = 240;
    private const int WriteEvery = 10;

    [Fact]
    public void Jitter2SceneMatchesTheRecordedTrace()
    {
        var mode = Environment.GetEnvironmentVariable("MF_PHYSICS_TRACE");
        if (mode is not ("1" or "record"))
            Assert.Skip("The bit-exact physics trace runs alone, with MF_PHYSICS_TRACE=1 (see the class remarks).");
        var path = Path.Combine(RepoRoot(), "Tests", "MainframeEngine.Tests", "Physics", "Traces", $"jitter2.{RuntimeInformation.RuntimeIdentifier}.trace");
        var actual = Record();
        if (mode == "record")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        if (!File.Exists(path))
            Assert.Skip($"No trace at {path}: record one with MF_PHYSICS_TRACE=record on the reference code.");
        var expected = File.ReadAllText(path);
        var recordedOn = expected[..expected.IndexOf('\n', StringComparison.Ordinal)];
        if (recordedOn != Header())
            Assert.Skip($"The trace was recorded on '{recordedOn}'; this run is '{Header()}'. Re-record it from the reference code.");
        if (expected == actual)
            return;
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var i = 0;
        while (i < e.Length && i < a.Length && e[i] == a[i])
            i++;
        Assert.Fail($"The physics trace differs from {path} at line {i + 1}:\n  expected: {(i < e.Length ? e[i] : "<end>")}\n  actual:   {(i < a.Length ? a[i] : "<end>")}");
    }

    private static string Header()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        return $"# jitter2 trace · {RuntimeInformation.RuntimeIdentifier} · {RuntimeInformation.FrameworkDescription} · {configuration}";
    }

    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MainframeEngine.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Repository root (MainframeEngine.slnx) not found above the test output.");
    }

    internal static string Record()
    {
        using var h = new PhysicsHarness3D(new PhysicsSettings3D { MultiThreaded = false, Deterministic = true });
        var signals = new StringBuilder();
        h.AddFloor(size: 60);
        h.AddStaticBox(new Vector3(6, 1, 0), new Vector3(4, 0.2f, 4), new Vector3(0, 0, 20), "Ramp");
        var wall = h.AddStaticBox(new Vector3(-6, 1.5f, 0), new Vector3(0.5f, 3, 8), name: "Wall");
        var bumps = new StaticBody3D { Name = "Bumps", Position = new Vector3(-3, 0, 6) };
        bumps.AddChild(new CollisionShape3D
        {
            Scale = new Vector3(2, 1, 2),
            Shape = new HeightMapShape3D
            {
                MapWidth = 5,
                MapDepth = 5,
                MapData = [0, 0.1f, 0.2f, 0.1f, 0, 0.1f, 0.3f, 0.4f, 0.3f, 0.1f, 0.2f, 0.4f, 0.6f, 0.4f, 0.2f, 0.1f, 0.3f, 0.4f, 0.3f, 0.1f, 0, 0.1f, 0.2f, 0.1f, 0],
            },
        });
        h.Add(bumps);

        var bodies = new List<RigidBody3D>();
        for (var i = 0; i < 4; i++)
            bodies.Add(h.AddBox(new Vector3(0.05f * i, 0.5f + i * 1.01f, 0), name: $"Stack{i}"));
        var ball = new RigidBody3D { Name = "Ball", Position = new Vector3(2, 3, 0), ContactMonitor = true, PhysicsMaterial = new PhysicsMaterial { Bounce = 0.4f } };
        ball.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.3f } });
        bodies.Add(h.Add(ball));
        var floaty = h.AddBox(new Vector3(-2, 4, 0), 0.5f, "Floaty");
        floaty.GravityScale = 0.25f;
        floaty.LinearDamp = 2f;
        floaty.AngularDamp = 1f;
        bodies.Add(floaty);
        var locked = h.AddBox(new Vector3(6, 3, 0), 0.6f, "Locked");
        locked.AxisLocks = AxisLock.LinearZ | AxisLock.AngularX;
        bodies.Add(locked);
        var capsule = new RigidBody3D { Name = "Capsule", Position = new Vector3(-2, 3, 6), Mass = 3, AngularVelocity = new Vector3(0, 0, 2), Ccd = true };
        capsule.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.3f, Height = 1.2f }, RotationDegrees = new Vector3(0, 0, 90) });
        bodies.Add(h.Add(capsule));
        var hull = new RigidBody3D { Name = "Hull", Position = new Vector3(-4, 3, 7), CanSleep = false };
        hull.AddChild(new CollisionShape3D
        {
            Scale = new Vector3(1.5f, 1, 1),
            Shape = new ConvexPolygonShape3D { Points = [new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f), new(0, 0.5f, -0.5f), new(0, 0, 0.5f)] },
        });
        bodies.Add(h.Add(hull));
        var pair = new RigidBody3D { Name = "Pair", Position = new Vector3(3, 2, -3) };
        pair.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.3f, Height = 0.8f }, Position = new Vector3(-0.4f, 0, 0) });
        pair.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.5f) }, Position = new Vector3(0.4f, 0, 0) });
        bodies.Add(h.Add(pair));
        var platform = new RigidBody3D { Name = "Platform", Mode = RigidBodyMode.Kinematic, Position = new Vector3(0, 0.25f, -6) };
        platform.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(3, 0.5f, 3) } });
        bodies.Add(h.Add(platform));
        bodies.Add(h.AddBox(new Vector3(0, 1.01f, -6), 0.5f, "Rider"));
        var walker = new WalkerCharacter3D { Name = "Walker", Position = new Vector3(-3, 1, 0), Walk = new Vector3(-1.5f, 0, 0.8f) };
        walker.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.4f, Height = 1.8f } });
        h.Add(walker);
        var area = h.AddArea(new Vector3(0, 1, 0), new Vector3(3, 2, 3));

        area.BodyEntered += n => signals.Append("  area+ ").Append(n.Name).Append('\n');
        area.BodyExited += n => signals.Append("  area- ").Append(n.Name).Append('\n');
        ball.BodyEntered += n => signals.Append("  ball+ ").Append(n.Name).Append('\n');
        ball.BodyExited += n => signals.Append("  ball- ").Append(n.Name).Append('\n');
        foreach (var body in bodies)
        {
            var b = body;
            b.SleepingStateChanged += () => signals.Append("  sleep ").Append(b.Name).Append(b.Sleeping ? " on\n" : " off\n");
        }

        var probe = new SphereShape3D { Radius = 0.25f };
        var probeFrom = Transform3D.FromTrs(new Vector3(2, 6, 0), Quaternion.Identity, Vector3.One);
        var probeAt = Transform3D.FromTrs(new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One);
        var overlaps = new List<CollisionObject3D>();
        var inArea = new List<Node3D>();
        var trace = new StringBuilder(128 * 1024).Append(Header()).Append('\n');
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var step = 0; step < Steps; step++)
        {
            platform.Position = new Vector3(MathF.Sin(step * 0.05f) * 2, 0.25f, -6); // a user move every step: a kinematic target
            if (step == 60)
                ball.ApplyImpulse(new Vector3(1.5f, 0, 0));
            if (step == 90)
                floaty.ApplyTorque(new Vector3(0, 3, 0));
            if (step == 120)
                wall.CollisionLayer = CollisionLayers.Layer(2); // a filter change under resting contacts
            if (step == 150)
                bodies[3].Teleport(Transform3D.FromTrs(new Vector3(1, 4, 1), Quaternion.CreateFromYawPitchRoll(0.3f, 0, 0), Vector3.One));
            if (step == 180)
                floaty.Mode = RigidBodyMode.Kinematic;
            if (step == 200)
                floaty.Mode = RigidBodyMode.Dynamic;
            h.Run(1);

            var block = new StringBuilder(4096).Append(signals);
            signals.Clear();
            foreach (var body in bodies)
            {
                body.GlobalTransform.Decompose(out var p, out var r, out _);
                block.Append("  ").Append(body.Name).Append(" p=").Append(Hex(p)).Append(" r=").Append(Hex(r)).Append(" v=").Append(Hex(body.LinearVelocity))
                    .Append(" w=").Append(Hex(body.AngularVelocity)).Append(" s=").Append(body.Sleeping ? '1' : '0').Append('\n');
            }

            walker.GlobalTransform.Decompose(out var wp, out _, out _);
            block.Append("  Walker p=").Append(Hex(wp)).Append(" floor=").Append(walker.IsOnFloor()).Append(" wall=").Append(walker.IsOnWall())
                .Append(" n=").Append(Hex(walker.GetFloorNormal())).Append(" v=").Append(Hex(walker.GetRealVelocity()))
                .Append(" hits=").Append(walker.GetSlideCollisionCount()).Append('\n');
            var q = h.Query;
            block.Append("  ray ").Append(q.RayCast(new Vector3(0.3f, 10, 0.3f), new Vector3(0.3f, -10, 0.3f), out var ray)
                ? $"{ray.Collider.Name} {ray.Shape?.Name} {Hex(ray.Fraction)} {Hex(ray.Normal)} {Hex(ray.Position)}"
                : "-").Append('\n');
            block.Append("  cast ").Append(q.ShapeCast(probe, probeFrom, new Vector3(0, -10, 0), out var cast)
                ? $"{cast.Collider.Name} {Hex(cast.Fraction)} {Hex(cast.Normal)} {Hex(cast.Position)}"
                : "-").Append('\n');
            overlaps.Clear();
            q.IntersectShape(probe, probeAt, overlaps);
            q.IntersectPoint(new Vector3(0.1f, 0.1f, 0.1f), overlaps);
            block.Append("  overlap ").AppendJoin(',', overlaps.Select(o => o.Name)).Append('\n');
            inArea.Clear();
            area.GetOverlappingBodies(inArea);
            block.Append("  area ").AppendJoin(',', inArea.Select(o => o.Name)).Append('\n');

            var text = block.ToString();
            hash.AppendData(Encoding.UTF8.GetBytes(text));
            trace.Append("step ").Append(step).Append(' ').Append(Convert.ToHexStringLower(hash.GetCurrentHash())).Append('\n');
            if (step % WriteEvery == WriteEvery - 1)
                trace.Append(text);
        }

        return trace.ToString();
    }

    private static string Hex(float f) => BitConverter.SingleToUInt32Bits(f).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    private static string Hex(Vector3 v) => $"{Hex(v.X)}/{Hex(v.Y)}/{Hex(v.Z)}";

    private static string Hex(Quaternion q) => $"{Hex(q.X)}/{Hex(q.Y)}/{Hex(q.Z)}/{Hex(q.W)}";
}
