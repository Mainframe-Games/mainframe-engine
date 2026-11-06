using Color = System.Drawing.Color;
using System.Drawing;
using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>
/// The joints of the Physics 3D scene (G9): a rope bridge of hinged planks, a shop sign on a breakable weld, a capsule
/// mannequin (<see cref="Mannequin3D"/>, a <see cref="Ragdoll3D"/>) that falls when a crate hits it, and two
/// <see cref="Generic6DofJoint3D"/>s (G9.10b): a gimbal that tilts and springs back, and a drawer that slides out.
/// </summary>
public static class Joints3DBuild
{
    private static readonly Color Wood = Color.FromArgb(180, 130, 82);
    private static readonly Color Post = Color.FromArgb(100, 116, 139);
    private static readonly Color Skin = Color.FromArgb(226, 232, 240);
    private static readonly Color Shirt = Color.FromArgb(56, 189, 248);

    /// <summary>Planks in the bridge.</summary>
    public const int Planks = 9;

    /// <summary>Nine planks hinged edge to edge between two posts at the back, authored sagging (a chain between fixed ends needs slack).</summary>
    public static void AddBridge(Node root)
    {
        const float y = 2.6f, left = -6.4f, span = 4.2f, sag = 0.5f, z = -5f;
        var bridge = Add(root, root, new Node3D { Name = "Bridge" });
        StaticBox(root, bridge, "PostL", new Vector3(left - 0.2f, y / 2, z), new Vector3(0.4f, y, 0.4f), Post);
        StaticBox(root, bridge, "PostR", new Vector3(left + span + 0.2f, y / 2, z), new Vector3(0.4f, y, 0.4f), Post);
        var points = new Vector3[Planks + 1];
        for (var i = 0; i <= Planks; i++)
            points[i] = new Vector3(left + span * i / Planks, MathF.Round(y - sag * MathF.Sin(MathF.PI * i / Planks), 3), z);
        for (var i = 0; i < Planks; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            var size = new Vector3(Vector3.Distance(a, b) * 0.92f, 0.08f, 1.4f);
            var plank = Add(root, bridge, new RigidBody3D
            {
                Name = $"Plank{i}",
                Position = (a + b) / 2,
                Mass = 2f,
                // Rounded like the pins: MathF's trig differs in the last bit between platforms, and the committed
                // scene file must match the builder on every platform.
                RotationDegrees = new Vector3(0, 0, MathF.Round(float.RadiansToDegrees(MathF.Atan2(b.Y - a.Y, b.X - a.X)), 3)),
            });
            Add(root, plank, new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = size } });
            Add(root, plank, new MeshInstance3D { Name = "Visual", Mesh = new BoxMesh { Size = size }, MaterialOverride = new StandardMaterial3D { ShadingMode = ShadingMode.BlinnPhong, AlbedoColor = Wood } });
        }

        for (var i = 0; i <= Planks; i++)
        {
            Add(root, bridge, new HingeJoint3D
            {
                Name = $"Hinge{i}",
                Position = points[i],
                UseLimit = true,
                LowerAngleDegrees = -40,
                UpperAngleDegrees = 40,
                NodeA = i == 0 ? "../PostL" : $"../Plank{i - 1}",
                NodeB = i == Planks ? "../PostR" : $"../Plank{i}",
            });
        }
    }

    /// <summary>A shop sign hanging from a post on a breakable weld: drop crates on it (click near it) to break it.</summary>
    public static void AddSign(Node root)
    {
        const float x = 9f, z = -3f;
        var sign = Add(root, root, new Node3D { Name = "SignPost" });
        StaticBox(root, sign, "Post", new Vector3(x, 1.75f, z), new Vector3(0.25f, 3.5f, 0.25f), Post);
        StaticBox(root, sign, "Arm", new Vector3(x - 0.7f, 3.4f, z), new Vector3(1.4f, 0.15f, 0.15f), Post);
        var board = Add(root, sign, new RigidBody3D { Name = "Board", Position = new Vector3(x - 0.8f, 2.8f, z), Mass = 3f });
        var size = new Vector3(1.1f, 0.8f, 0.08f);
        Add(root, board, new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = size } });
        Add(root, board, new MeshInstance3D { Name = "Visual", Mesh = new BoxMesh { Size = size }, MaterialOverride = new StandardMaterial3D { ShadingMode = ShadingMode.BlinnPhong, AlbedoColor = Color.FromArgb(250, 204, 21) } });
        Add(root, sign, new WeldJoint3D { Name = "Weld", Position = new Vector3(x - 0.8f, 3.3f, z), NodeA = "../Arm", NodeB = "../Board", BreakForce = 90 });
    }

    /// <summary>A capsule mannequin next to the crate stack: kinematic and standing until a crate hits it.</summary>
    public static void AddMannequin(Node root)
    {
        var mannequin = Add(root, root, new Mannequin3D { Name = "Mannequin", Position = new Vector3(3f, 0, 1.6f) });
        var down = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2); // cone-twist axes (X) pointing down the limbs
        Part(root, mannequin, "Torso", new Vector3(0, 1.3f, 0), 0.2f, 0.75f, Shirt, 8f);
        Part(root, mannequin, "Head", new Vector3(0, 1.88f, 0), 0.15f, 0.3f, Skin, 2f);
        Add(root, mannequin, new ConeTwistJoint3D { Name = "Neck", Position = new Vector3(0, 1.7f, 0), Rotation = down, SwingSpanDegrees = 35, TwistSpanDegrees = 40, NodeA = "../Torso", NodeB = "../Head" });
        for (var side = -1; side <= 1; side += 2)
        {
            var s = side < 0 ? "L" : "R";
            Part(root, mannequin, $"Arm{s}", new Vector3(side * 0.33f, 1.32f, 0), 0.07f, 0.62f, Skin, 1.5f);
            Add(root, mannequin, new ConeTwistJoint3D
            {
                Name = $"Shoulder{s}",
                Position = new Vector3(side * 0.33f, 1.6f, 0),
                Rotation = down,
                SwingSpanDegrees = 80,
                TwistSpanDegrees = 45,
                NodeA = "../Torso",
                NodeB = $"../Arm{s}",
            });
            Part(root, mannequin, $"Leg{s}", new Vector3(side * 0.11f, 0.45f, 0), 0.09f, 0.88f, Shirt, 3f);
            Add(root, mannequin, new HingeJoint3D
            {
                Name = $"Hip{s}",
                Position = new Vector3(side * 0.11f, 0.9f, 0),
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2),
                UseLimit = true,
                LowerAngleDegrees = -30,
                UpperAngleDegrees = 90,
                NodeA = "../Torso",
                NodeB = $"../Leg{s}",
            });
        }
    }

    /// <summary>Where the gimbal's dish is joined to its pedestal.</summary>
    public static readonly Vector3 GimbalAt = new(-2.5f, 1.25f, 3.5f);

    /// <summary>Where the drawer's joint is (its X axis turned to point out of the cabinet, towards +Z).</summary>
    public static readonly Vector3 DrawerAt = new(2f, 0.6f, 4.5f);

    /// <summary>How far the drawer slides out (m).</summary>
    public const float DrawerTravel = 0.4f;

    /// <summary>
    /// A dish on a pedestal that tilts up to 40° about X and Z and springs back level, and a drawer that slides 0.4 m out
    /// of its cabinet, wobbling a few degrees, and softly closes: two 6-DOF joints, every other axis locked.
    /// </summary>
    public static void AddSixDof(Node root)
    {
        var props = Add(root, root, new Node3D { Name = "SixDof" });
        StaticBox(root, props, "Pedestal", new Vector3(GimbalAt.X, 0.6f, GimbalAt.Z), new Vector3(0.3f, 1.2f, 0.3f), Post);
        var dishSize = new Vector3(1.1f, 0.08f, 1.1f);
        var dish = Add(root, props, new RigidBody3D { Name = "Dish", Position = GimbalAt + new Vector3(0, 0.05f, 0), Mass = 2f });
        Add(root, dish, new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = dishSize } });
        Add(root, dish, new MeshInstance3D { Name = "Visual", Mesh = new BoxMesh { Size = dishSize }, MaterialOverride = new StandardMaterial3D { ShadingMode = ShadingMode.BlinnPhong, AlbedoColor = Shirt } });
        Add(root, props, new Generic6DofJoint3D
        {
            Name = "Gimbal",
            Position = GimbalAt,
            NodeA = "../Pedestal",
            NodeB = "../Dish",
            AngularXMode = JointAxisMode.Limited,
            AngularXLowerDegrees = -40,
            AngularXUpperDegrees = 40,
            AngularXSpringEnabled = true,
            AngularXSpringFrequency = 1.5f,
            AngularXSpringDampingRatio = 0.3f,
            AngularZMode = JointAxisMode.Limited,
            AngularZLowerDegrees = -40,
            AngularZUpperDegrees = 40,
            AngularZSpringEnabled = true,
            AngularZSpringFrequency = 1.5f,
            AngularZSpringDampingRatio = 0.3f,
        });

        StaticBox(root, props, "Cabinet", new Vector3(DrawerAt.X, 0.5f, DrawerAt.Z - 0.2f), new Vector3(1f, 1f, 0.8f), Wood);
        var drawerSize = new Vector3(0.8f, 0.3f, 0.7f);
        var drawer = Add(root, props, new RigidBody3D { Name = "Drawer", Position = DrawerAt, Mass = 1f });
        Add(root, drawer, new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = drawerSize } });
        Add(root, drawer, new MeshInstance3D { Name = "Visual", Mesh = new BoxMesh { Size = drawerSize }, MaterialOverride = new StandardMaterial3D { ShadingMode = ShadingMode.BlinnPhong, AlbedoColor = Skin } });
        Add(root, props, new Generic6DofJoint3D
        {
            Name = "Slide",
            Position = DrawerAt,
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2), // X out of the cabinet's front (+Z)
            NodeA = "../Cabinet",
            NodeB = "../Drawer",
            LinearXMode = JointAxisMode.Limited,
            LinearXLower = 0f,
            LinearXUpper = DrawerTravel,
            LinearXSpringEnabled = true, // soft-close
            LinearXSpringFrequency = 0.6f,
            LinearXSpringDampingRatio = 1f,
            AngularYMode = JointAxisMode.Limited,
            AngularYLowerDegrees = -5,
            AngularYUpperDegrees = 5,
        });
    }

    private static void Part(Node root, Node parent, string name, Vector3 at, float radius, float height, Color color, float mass)
    {
        var part = Add(root, parent, new RigidBody3D { Name = name, Position = at, Mass = mass, ContactMonitor = true });
        Add(root, part, new CollisionShape3D { Name = "Shape", Shape = new CapsuleShape3D { Radius = radius, Height = height } });
        Add(root, part, new MeshInstance3D { Name = "Visual", Mesh = new CapsuleMesh { Radius = radius, Height = height }, MaterialOverride = new StandardMaterial3D { ShadingMode = ShadingMode.BlinnPhong, AlbedoColor = color } });
    }

    private static void StaticBox(Node root, Node parent, string name, Vector3 at, Vector3 size, Color color)
    {
        var body = Add(root, parent, new StaticBody3D { Name = name, Position = at });
        Add(root, body, new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = size } });
        Add(root, body, new MeshInstance3D { Name = "Visual", Mesh = new BoxMesh { Size = size }, MaterialOverride = new StandardMaterial3D { ShadingMode = ShadingMode.BlinnPhong, AlbedoColor = color } });
    }
}
