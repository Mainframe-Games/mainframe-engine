using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// G9 joints (ADR 0211) with the physics debug draw on: a hinged plank bridge between two posts, a cone-twist mannequin
/// hanging from a bar, and a shelf whose breakable weld a dropped crate breaks (drawn grey once broken) next to a lamp on
/// an unbroken weld. Deterministic, single-threaded Jitter2 with one step per frame, as <see cref="PhysicsScene"/>.
/// </summary>
public sealed class PhysicsJointsScene(HostOptions host) : RenderTestGame(host)
{
    private static readonly Color Wood = Color.FromArgb(255, 190, 140, 90);
    private static readonly Color Cloth = Color.FromArgb(255, 90, 150, 220);
    private static readonly Color Stone = Color.FromArgb(255, 150, 150, 160);
    private static readonly Color Crate = Color.FromArgb(255, 230, 120, 80);

    private Node3D _scene = null!;

    protected override void LoadScene()
    {
        var physics = Servers.Get<PhysicsServer3D>()!;
        physics.Settings.MultiThreaded = false;
        physics.Settings.Deterministic = true;
        physics.DebugDrawEnabled = true;

        _scene = new Node3D { Name = "Joints" };
        var camera = new Camera3D { Name = "Camera", Position = new Vector3(-0.15f, 2.7f, 7.6f) };
        camera.LookAt(new Vector3(-0.15f, 1.8f, 0));
        _scene.AddChild(camera);
        _scene.AddChild(new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });
        var sun = new DirectionalLight3D { Name = "Sun", Position = new Vector3(0, 6, 0), Color = new Vector3(1f, 0.95f, 0.8f), Energy = 0.9f };
        sun.LookAt(sun.Position + Vector3.Normalize(new Vector3(-0.4f, -1f, -0.5f)));
        _scene.AddChild(sun);

        var floor = new StaticBody3D { Name = "Floor" };
        floor.AddChild(new CollisionShape3D { Name = "Shape", Position = new Vector3(0, -0.5f, 0), Shape = new BoxShape3D { Size = new Vector3(14, 1, 8) } });
        floor.AddChild(new MeshInstance3D { Name = "Visual", Mesh = new PlaneMesh { Size = new Vector2(14, 8) } });
        _scene.AddChild(floor);

        AddBridge();
        AddMannequin();
        AddShelf();
        Tree.ChangeScene(_scene);
    }

    // A rope bridge: seven planks hinged edge to edge between two posts, authored sagging (a chain between two fixed
    // points needs slack to hang). Hinges about Z: the bridge swings in the camera's view plane.
    private void AddBridge()
    {
        const float y = 2.2f, left = -3.9f, span = 2.6f, sag = 0.45f;
        AddBox("PostL", new Vector3(left - 0.15f, y / 2, 0), new Vector3(0.3f, y, 0.3f), Stone, dynamic: false);
        AddBox("PostR", new Vector3(left + span + 0.15f, y / 2, 0), new Vector3(0.3f, y, 0.3f), Stone, dynamic: false);
        Span<Vector3> points = stackalloc Vector3[8];
        for (var i = 0; i <= 7; i++)
            points[i] = new Vector3(left + span * i / 7, y - sag * MathF.Sin(MathF.PI * i / 7), 0);
        for (var i = 0; i < 7; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            var plank = AddBox($"Plank{i}", (a + b) / 2, new Vector3(Vector3.Distance(a, b) * 0.9f, 0.06f, 0.8f), Wood);
            plank.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Atan2(b.Y - a.Y, b.X - a.X));
        }

        for (var i = 0; i <= 7; i++)
            Join(new HingeJoint3D { Name = $"Hinge{i}", UseLimit = true, LowerAngleDegrees = -35, UpperAngleDegrees = 35 }, points[i],
                i == 0 ? "PostL" : $"Plank{i - 1}", i == 7 ? "PostR" : $"Plank{i}");
        _scene.GetNode<RigidBody3D>("Plank3").LinearVelocity = new Vector3(0, 2.5f, 0); // a step on the bridge
    }

    // A box mannequin: the torso hangs from a bar on a cone-twist neck, arms on cone-twists, legs on hinged hips.
    private void AddMannequin()
    {
        const float x = 0.4f;
        AddBox("Bar", new Vector3(x, 3.6f, 0), new Vector3(1.4f, 0.1f, 0.1f), Stone, dynamic: false);
        var torso = AddBox("Torso", new Vector3(x, 2.9f, 0), new Vector3(0.5f, 0.8f, 0.25f), Cloth);
        torso.LinearVelocity = new Vector3(3f, 0, 1.5f);
        var down = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2); // cone axes (X) pointing down
        Join(new ConeTwistJoint3D { Name = "Neck", SwingSpanDegrees = 35, TwistSpanDegrees = 20, Rotation = down }, new Vector3(x, 3.55f, 0), "Bar", "Torso");
        for (var side = -1; side <= 1; side += 2)
        {
            var name = side < 0 ? "L" : "R";
            var arm = AddBox($"Arm{name}", new Vector3(x + side * 0.38f, 2.95f, 0), new Vector3(0.14f, 0.6f, 0.14f), Cloth);
            arm.LinearVelocity = new Vector3(side * 2.5f, 1f, 0);
            Join(new ConeTwistJoint3D { Name = $"Shoulder{name}", SwingSpanDegrees = 60, TwistSpanDegrees = 30, Rotation = down },
                new Vector3(x + side * 0.38f, 3.25f, 0), "Torso", $"Arm{name}");
            var leg = AddBox($"Leg{name}", new Vector3(x + side * 0.14f, 2.15f, 0), new Vector3(0.16f, 0.7f, 0.16f), Cloth);
            leg.LinearVelocity = new Vector3(0, 0, side * 1.5f);
            Join(new HingeJoint3D
            {
                Name = $"Hip{name}",
                UseLimit = true,
                LowerAngleDegrees = -20,
                UpperAngleDegrees = 60,
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2),
            }, new Vector3(x + side * 0.14f, 2.5f, 0), "Torso", $"Leg{name}");
        }
    }

    // A shelf on a wall facing the camera, welded with a low break force; a heavy crate lands on it and breaks the weld.
    // A lamp hangs from a bracket on an unbroken weld.
    private void AddShelf()
    {
        const float x = 2.9f, y = 1.6f, z = -0.6f;
        AddBox("Wall", new Vector3(x, 1.3f, z - 0.1f), new Vector3(1.6f, 2.6f, 0.2f), Stone, dynamic: false);
        AddBox("Shelf", new Vector3(x, y, z + 0.4f), new Vector3(1.2f, 0.08f, 0.8f), Wood);
        Join(new WeldJoint3D { Name = "Weld", BreakForce = 150 }, new Vector3(x, y, z), "Wall", "Shelf");
        AddBox("Bracket", new Vector3(x - 0.45f, y + 0.75f, z + 0.15f), new Vector3(0.3f, 0.1f, 0.3f), Stone, dynamic: false);
        AddBox("Lamp", new Vector3(x - 0.45f, y + 0.45f, z + 0.15f), new Vector3(0.18f), Crate);
        Join(new WeldJoint3D { Name = "LampWeld" }, new Vector3(x - 0.45f, y + 0.6f, z + 0.15f), "Bracket", "Lamp");
        var crate = AddBox("Crate", new Vector3(x + 0.3f, 3.4f, z + 0.5f), new Vector3(0.4f), Crate);
        crate.Mass = 20;
    }

    private RigidBody3D AddBox(string name, Vector3 position, Vector3 size, Color color, bool dynamic = true)
    {
        PhysicsBody3D body = dynamic ? new RigidBody3D { Name = name, Position = position } : new StaticBody3D { Name = name, Position = position };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        body.AddChild(new MeshInstance3D { Name = "Visual", Mesh = new BoxMesh { Size = size }, MaterialOverride = new StandardMaterial3D { AlbedoColor = color } });
        _scene.AddChild(body);
        return body as RigidBody3D ?? null!;
    }

    private void Join(Joint3D joint, Vector3 position, string a, string b)
    {
        joint.Position = position;
        joint.NodeA = "../" + a;
        joint.NodeB = "../" + b;
        _scene.AddChild(joint);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }
}
