using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// Joints at scale (G9, ADR 0211/0212): one fixed step of a 100-link pinned chain swinging from the world, and one of 50
/// simulating ragdolls of 11 parts each (cone-twist and hinge joints) lying on a floor. Single-threaded Jitter2, through
/// the scene tree; bodies never sleep.
/// </summary>
[MemoryDiagnoser]
public class JointBenchmarks : IDisposable
{
    private SceneTree _chain = null!;
    private SceneTree _ragdolls = null!;
    private readonly GameTime _step = new() { DeltaTime = 1f / 60f, FrameCount = 1 };

    [GlobalSetup]
    public void Setup()
    {
        _chain = NewTree();
        var chain = new Node3D { Name = "Chain" };
        var link = new SphereShape3D { Radius = 0.1f };
        for (var i = 0; i < 100; i++)
        {
            var body = new RigidBody3D { Name = $"Link{i}", Position = new Vector3((i + 1) * 0.3f, 40, 0), CanSleep = false };
            body.AddChild(new CollisionShape3D { Shape = link });
            chain.AddChild(body);
            chain.AddChild(new PinJoint3D
            {
                Name = $"Pin{i}",
                Position = new Vector3(i * 0.3f + (i == 0 ? 0 : 0.15f), 40, 0),
                NodeA = i == 0 ? default(NodePath) : $"../Link{i - 1}",
                NodeB = $"../Link{i}",
            });
        }

        _chain.ChangeScene(chain);

        _ragdolls = NewTree();
        var scene = new Node3D { Name = "Ragdolls" };
        var floor = new StaticBody3D { Name = "Floor", Position = new Vector3(0, -0.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(200, 1, 200) } });
        scene.AddChild(floor);
        for (var i = 0; i < 50; i++)
            scene.AddChild(Ragdoll($"Ragdoll{i}", new Vector3(i % 10 * 3f - 15, 0, i / 10 * 3f - 7.5f)));
        _ragdolls.ChangeScene(scene);

        for (var i = 0; i < 120; i++)
        {
            _chain.Tick(_step);
            _ragdolls.Tick(_step);
        }
    }

    /// <summary>One frame with one physics step: a 100-link chain of pins.</summary>
    [Benchmark]
    public void StepChain100Pins() => _chain.Tick(_step);

    /// <summary>One frame with one physics step: 50 simulating ragdolls of 11 parts (500 joints).</summary>
    [Benchmark]
    public void Step50Ragdolls() => _ragdolls.Tick(_step);

    // Pelvis, chest, head, upper and lower arms and legs: 11 capsules, 10 joints.
    private static Ragdoll3D Ragdoll(string name, Vector3 at)
    {
        var ragdoll = new Ragdoll3D { Name = name, Position = at, Simulating = true };
        var down = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);
        void Part(string part, Vector3 position, float radius, float height)
        {
            var body = new RigidBody3D { Name = part, Position = position, CanSleep = false };
            body.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = radius, Height = height } });
            ragdoll.AddChild(body);
        }

        void Join(Joint3D joint, Vector3 position, string a, string b)
        {
            joint.Position = position;
            joint.NodeA = "../" + a;
            joint.NodeB = "../" + b;
            ragdoll.AddChild(joint);
        }

        Part("Pelvis", new Vector3(0, 1.0f, 0), 0.15f, 0.35f);
        Part("Chest", new Vector3(0, 1.4f, 0), 0.17f, 0.45f);
        Part("Head", new Vector3(0, 1.8f, 0), 0.12f, 0.24f);
        Join(new ConeTwistJoint3D { Name = "Spine", Rotation = down }, new Vector3(0, 1.18f, 0), "Pelvis", "Chest");
        Join(new ConeTwistJoint3D { Name = "Neck", Rotation = down }, new Vector3(0, 1.65f, 0), "Chest", "Head");
        for (var side = -1; side <= 1; side += 2)
        {
            var s = side < 0 ? "L" : "R";
            Part($"UpperArm{s}", new Vector3(side * 0.3f, 1.45f, 0), 0.06f, 0.3f);
            Part($"LowerArm{s}", new Vector3(side * 0.3f, 1.15f, 0), 0.05f, 0.3f);
            Part($"UpperLeg{s}", new Vector3(side * 0.1f, 0.65f, 0), 0.08f, 0.4f);
            Part($"LowerLeg{s}", new Vector3(side * 0.1f, 0.25f, 0), 0.07f, 0.4f);
            Join(new ConeTwistJoint3D { Name = $"Shoulder{s}", Rotation = down }, new Vector3(side * 0.3f, 1.6f, 0), "Chest", $"UpperArm{s}");
            Join(new HingeJoint3D { Name = $"Elbow{s}", UseLimit = true, LowerAngleDegrees = 0, UpperAngleDegrees = 140 }, new Vector3(side * 0.3f, 1.3f, 0),
                $"UpperArm{s}", $"LowerArm{s}");
            Join(new ConeTwistJoint3D { Name = $"Hip{s}", Rotation = down }, new Vector3(side * 0.1f, 0.85f, 0), "Pelvis", $"UpperLeg{s}");
            Join(new HingeJoint3D { Name = $"Knee{s}", UseLimit = true, LowerAngleDegrees = -140, UpperAngleDegrees = 0 }, new Vector3(side * 0.1f, 0.45f, 0),
                $"UpperLeg{s}", $"LowerLeg{s}");
        }

        return ragdoll;
    }

    private static SceneTree NewTree()
    {
        var servers = new ServerRegistry();
        servers.Register(new PhysicsServer3D(new PhysicsSettings3D { MultiThreaded = false }));
        return new SceneTree(servers);
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _chain?.Shutdown();
        _chain?.Servers.Dispose();
        _ragdolls?.Shutdown();
        _ragdolls?.Servers.Dispose();
        GC.SuppressFinalize(this);
    }
}
