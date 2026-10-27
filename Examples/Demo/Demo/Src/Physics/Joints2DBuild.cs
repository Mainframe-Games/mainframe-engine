using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>
/// The joints of the Physics 2D scene (G9): a chain of pinned links, a car on <see cref="WheelJoint2D"/>s that drives
/// back and forth (<see cref="Car2D"/>) and a plank bridge on breakable pins under the pegboard.
/// </summary>
public static class Joints2DBuild
{
    private static readonly Vector4 Link = new(0.8f, 0.82f, 0.86f, 1);
    private static readonly Vector4 Plank = new(0.71f, 0.51f, 0.32f, 1);
    private static readonly Vector4 Body = new(0.22f, 0.74f, 0.97f, 1);
    private static readonly Vector4 Tyre = new(0.12f, 0.13f, 0.16f, 1);
    private static readonly Vector4 Outline = new(0, 0, 0, 0.35f);

    /// <summary>Links in the chain.</summary>
    public const int Links = 8;

    /// <summary>Planks in the bridge.</summary>
    public const int Planks = 8;

    /// <summary>Eight links hanging from the world right of the funnel.</summary>
    public static void AddChain(Node root)
    {
        const float x = 480f, top = -330f, step = 24f;
        var chain = Add(root, root, new Node2D { Name = "Chain" });
        for (var i = 0; i < Links; i++)
        {
            var link = Add(root, chain, new RigidBody2D { Name = $"Link{i}", Position = new Vector2(x, top + (i + 0.5f) * step), Mass = 0.5f });
            if (i == Links - 1)
                link.LinearVelocity = new Vector2(-260, 0); // set swinging
            Add(root, link, new CollisionShape2D { Name = "Shape", Shape = new CapsuleShape2D { Radius = 5, Height = step } });
            Add(root, link, new DemoShape2D { Name = "Visual", Kind = DemoShapeKind.Rect, Size = new Vector2(10, step), Color = Link, Outline = Outline, OutlineWidth = 1.5f });
            Add(root, chain, new PinJoint2D
            {
                Name = $"Pin{i}",
                Position = new Vector2(x, top + i * step),
                NodeA = i == 0 ? default(NodePath) : $"../Link{i - 1}",
                NodeB = $"../Link{i}",
            });
        }
    }

    /// <summary>A car on the ground: a chassis and two wheels on sprung, motorised wheel joints.</summary>
    public static void AddCar(Node root)
    {
        const float y = 286f;
        var car = Add(root, root, new Car2D { Name = "Car", Position = new Vector2(-300, 0) });
        var chassis = Add(root, car, new RigidBody2D { Name = "Chassis", Position = new Vector2(0, y - 8), Mass = 3f });
        Add(root, chassis, new CollisionShape2D { Name = "Shape", Shape = new RectangleShape2D { Size = new Vector2(96, 22) } });
        Add(root, chassis, new DemoShape2D { Name = "Visual", Kind = DemoShapeKind.Rect, Size = new Vector2(96, 22), Color = Body, Outline = Outline, OutlineWidth = 2f });
        for (var side = -1; side <= 1; side += 2)
        {
            var name = side < 0 ? "WheelL" : "WheelR";
            var at = new Vector2(side * 34, y + 12);
            var wheel = Add(root, car, new RigidBody2D { Name = name, Position = at });
            Add(root, wheel, new CollisionShape2D { Name = "Shape", Shape = new CircleShape2D { Radius = 15 } });
            Add(root, wheel, new DemoShape2D { Name = "Visual", Kind = DemoShapeKind.Circle, Radius = 15, Color = Tyre, Outline = new Vector4(0.8f, 0.82f, 0.86f, 1), OutlineWidth = 3f });
            // X points up the suspension: the joint is turned −90°.
            Add(root, car, new WheelJoint2D
            {
                Name = $"{name}Joint",
                Position = at,
                Rotation = -MathF.PI / 2,
                NodeA = "../Chassis",
                NodeB = $"../{name}",
                SpringHertz = 4f,
                SpringDampingRatio = 0.7f,
                UseLimit = true,
                LowerTranslation = -8,
                UpperTranslation = 8,
                MotorEnabled = true,
                MotorTargetVelocity = Car2D.WheelSpeed,
                MotorMaxTorque = 600_000f,
            });
        }
    }

    /// <summary>Eight planks pinned edge to edge between two blocks under the pegboard's right side; the pins break under a pile.</summary>
    public static void AddBridge(Node root)
    {
        var bridge = Add(root, root, new Node2D { Name = "Bridge" });
        Block(root, bridge, "BlockL", BridgeLeft - 12);
        Block(root, bridge, "BlockR", BridgeLeft + BridgeSpan + 12);
        for (var i = 0; i < Planks; i++)
        {
            var (a, b) = (BridgePoint(i), BridgePoint(i + 1));
            var size = new Vector2(Vector2.Distance(a, b) * 0.94f, 8);
            var plank = Add(root, bridge, new RigidBody2D { Name = $"Plank{i}", Position = PlankCentre(i), RotationDegrees = PlankDegrees(i), Mass = 0.5f });
            Add(root, plank, new CollisionShape2D { Name = "Shape", Shape = new RectangleShape2D { Size = size } });
            Add(root, plank, new DemoShape2D { Name = "Visual", Kind = DemoShapeKind.Rect, Size = size, Color = Plank, Outline = Outline, OutlineWidth = 1.5f });
        }

        for (var i = 0; i <= Planks; i++)
        {
            Add(root, bridge, new PinJoint2D
            {
                Name = $"Pin{i}",
                Position = BridgePoint(i),
                NodeA = i == 0 ? "../BlockL" : $"../Plank{i - 1}",
                NodeB = i == Planks ? "../BlockR" : $"../Plank{i}",
                BreakForce = 60000f,
            });
        }
    }

    private const float BridgeY = 205f, BridgeLeft = 170f, BridgeSpan = 330f, BridgeSag = 40f;

    // The bridge is authored sagging: pin i on a sine between the blocks (a chain between fixed ends needs slack). The
    // pins and plank angles are rounded: MathF's trig differs in the last bit between platforms, and the committed scene
    // file must match the builder on every platform.
    private static Vector2 BridgePoint(int i) =>
        new(BridgeLeft + BridgeSpan * i / Planks, MathF.Round(BridgeY + BridgeSag * MathF.Sin(MathF.PI * i / Planks), 3));

    private static Vector2 PlankCentre(int i) => (BridgePoint(i) + BridgePoint(i + 1)) / 2;

    private static float PlankDegrees(int i)
    {
        var (a, b) = (BridgePoint(i), BridgePoint(i + 1));
        return MathF.Round(float.RadiansToDegrees(MathF.Atan2(b.Y - a.Y, b.X - a.X)), 3);
    }

    /// <summary>Plank <paramref name="i"/>'s authored pose (the Reset button puts it back there).</summary>
    public static Transform2D PlankPose(int i) =>
        Transform2D.FromTrs(PlankCentre(i), float.DegreesToRadians(PlankDegrees(i)), Vector2.One);

    // A pillar from the ground (its top at y = 315) up to the bridge's end.
    private static void Block(Node root, Node parent, string name, float x)
    {
        const float ground = 315f;
        var size = new Vector2(24, ground - BridgeY);
        var body = Add(root, parent, new StaticBody2D { Name = name, Position = new Vector2(x, (ground + BridgeY) / 2) });
        Add(root, body, new CollisionShape2D { Name = "Shape", Shape = new RectangleShape2D { Size = size } });
        Add(root, body, new DemoShape2D { Name = "Visual", Kind = DemoShapeKind.Rect, Size = size, Color = new Vector4(0.39f, 0.45f, 0.55f, 1) });
    }
}

/// <summary>Drives its wheel joints back and forth between two turning points along the ground.</summary>
public sealed class Car2D : Node2D
{
    /// <summary>The wheels' motor speed in radians per second.</summary>
    public const float WheelSpeed = 8f;

    /// <summary>Where the car turns back (pixels, the chassis's global X).</summary>
    [Export] public Vector2 Range { get; set; } = new(-450, 60);

    private float _direction = 1f;
    private int _lastEnd;

    /// <summary>How many times the car turned back.</summary>
    public int Turns { get; private set; }

    protected override void OnPhysicsProcess(float delta)
    {
        if (GetNodeOrNull<RigidBody2D>("Chassis") is not { } chassis)
            return;
        var x = chassis.GlobalPosition.X;
        var velocity = chassis.LinearVelocity.X;
        // Turn once at each end (the motor's sign that drives right is learnt from the motion, not assumed).
        var end = x > Range.Y && velocity > 0 ? 1 : x < Range.X && velocity < 0 ? -1 : 0;
        if (end != 0 && end != _lastEnd)
        {
            _lastEnd = end;
            _direction = -_direction;
            Turns++;
        }

        for (var i = 0; i < ChildCount; i++)
            if (GetChild(i) is WheelJoint2D joint)
                joint.MotorTargetVelocity = WheelSpeed * _direction;
    }
}
