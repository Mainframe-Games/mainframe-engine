using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;
using Color = System.Drawing.Color;

namespace Demo;

/// <summary>
/// Paths 3D (G13): <see cref="Path3D"/> rails and moving platforms. A capsule (<see cref="PathRider3D"/>, WASD and Space)
/// starts on a lift that ping-pongs up to a landing; a ferry shuttles between two pads; a turntable turns; a guard
/// (<see cref="PathGuard3D"/>) walks a patrol route by the carrot recipe; and a camera rail circles the yard (the panel
/// rides it). Every platform is an <see cref="AnimatableBody3D"/> on a <see cref="PathFollow3D"/> in the physics
/// callback, or turned by code, and carries what stands on it.
/// </summary>
public static class Paths3DScene
{
    public const float LiftSpeed = 1.6f;
    public const float FerrySpeed = 2.2f;
    public static readonly Vector3 LiftBottom = new(-8f, 0.25f, 0f);
    public static readonly Vector3 LiftTop = new(-8f, 4.75f, 0f);

    private static readonly Color Slate = Color.FromArgb(78, 90, 112);
    private static readonly Color Pad = Color.FromArgb(100, 116, 139);
    private static readonly Color Platform = Color.FromArgb(245, 158, 11);

    public static Node Build()
    {
        var root = new Node3D { Name = "paths_3d" };
        Add(root, root, new Camera3D { Name = "Camera", Current = true, Position = new Vector3(-6, 11, 19), RotationDegrees = new Vector3(-32, -12, 0) });
        Add(root, root, new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });
        Add(root, root, new DirectionalLight3D { Name = "Sun", Energy = 1.2f, CastsShadows = true, RotationDegrees = new Vector3(-55, -30, 0) });
        StaticBox(root, "Floor", new Vector3(0, -0.5f, 0), new Vector3(36, 1, 28), Slate);

        // Platforms come before the rider and the guard in the tree: they move first in each physics step, so riders
        // read where the platform goes this step (exact carrying).
        BuildLift(root);
        BuildFerry(root);
        var table = Add(root, root, new Turntable3D { Name = "Turntable", Position = new Vector3(8, 0.2f, -3), DegreesPerSecond = 30f });
        Add(root, table, new CollisionShape3D { Name = "Shape", Shape = new CylinderShape3D { Radius = 3f, Height = 0.4f } });
        Add(root, table, new MeshInstance3D { Name = "Mesh", Mesh = new CylinderMesh { TopRadius = 3f, BottomRadius = 3f, Height = 0.4f }, MaterialOverride = Material(Platform) });
        Add(root, table, new MeshInstance3D { Name = "Stripe", Position = new Vector3(1.5f, 0.21f, 0), Mesh = new BoxMesh { Size = new Vector3(3f, 0.02f, 0.3f) }, MaterialOverride = Material(Color.FromArgb(30, 41, 59)) });

        BuildPatrol(root);
        BuildRail(root);

        var rider = Add(root, root, new PathRider3D { Name = "Rider", Position = LiftBottom + new Vector3(0, 1.15f, 0) });
        Add(root, rider, new CollisionShape3D { Name = "Shape", Shape = new CapsuleShape3D { Radius = 0.35f, Height = 1.6f } });
        Add(root, rider, new MeshInstance3D { Name = "Mesh", Mesh = new CapsuleMesh { Radius = 0.35f, Height = 1.6f }, MaterialOverride = Material(Color.FromArgb(226, 232, 240)) });

        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new Paths3DPanel { Name = "Panel" });
        return root;
    }

    // A lift between the floor and a landing, ping-ponging.
    private static void BuildLift(Node root)
    {
        var curve = new Curve3D { UpVectorEnabled = true };
        curve.AddPoint(LiftBottom);
        curve.AddPoint(LiftTop);
        var path = Add(root, root, new Path3D { Name = "LiftPath", Curve = curve });
        Add(root, path, new PathMarkers3D { Name = "Markers", Spacing = 0.5f });
        var follow = Add(root, path, new PathFollow3D
        {
            Name = "Follow",
            Speed = LiftSpeed,
            Loop = false,
            PingPong = true,
            RotationMode = PathRotationMode.None,
            ProcessCallback = PathProcessCallback.Physics,
        });
        PlatformBody(root, follow, "Lift", new Vector3(3, 0.5f, 3));
        StaticBox(root, "Landing", new Vector3(-12f, 2.25f, 0), new Vector3(5, 5, 5), Pad);
    }

    // A ferry between two raised pads.
    private static void BuildFerry(Node root)
    {
        StaticBox(root, "PadWest", new Vector3(-4f, 1f, 7f), new Vector3(3, 2, 3), Pad);
        StaticBox(root, "PadEast", new Vector3(10f, 1f, 7f), new Vector3(3, 2, 3), Pad);
        var curve = new Curve3D { UpVectorEnabled = true };
        curve.SetSmoothPoints([new(-1f, 1.75f, 7f), new(3f, 2.75f, 9f), new(7f, 1.75f, 7f)], closed: false);
        var path = Add(root, root, new Path3D { Name = "FerryPath", Curve = curve });
        Add(root, path, new PathMarkers3D { Name = "Markers" });
        var follow = Add(root, path, new PathFollow3D
        {
            Name = "Follow",
            Speed = FerrySpeed,
            Loop = false,
            PingPong = true,
            RotationMode = PathRotationMode.Y,
            ProcessCallback = PathProcessCallback.Physics,
        });
        PlatformBody(root, follow, "Ferry", new Vector3(2.5f, 0.5f, 2.5f));
    }

    // A rounded route the guard walks, starting off it.
    private static void BuildPatrol(Node root)
    {
        var curve = new Curve3D { UpVectorEnabled = true };
        curve.SetSmoothPoints([new(-6, 0, -11), new(2, 0, -12), new(4, 0, -8), new(-2, 0, -6), new(-8, 0, -7)], closed: true);
        var route = Add(root, root, new Path3D { Name = "Route", Curve = curve });
        Add(root, route, new PathMarkers3D { Name = "Markers", Color = Color.FromArgb(110, 220, 150) });
        Add(root, route, new PathFollow3D { Name = "Carrot", RotationMode = PathRotationMode.None });
        var guard = Add(root, root, new PathGuard3D { Name = "Guard", Route = new NodePath("../Route"), Position = new Vector3(-1, 1, -9) });
        Add(root, guard, new CollisionShape3D { Name = "Shape", Shape = new CapsuleShape3D { Radius = 0.4f, Height = 1.8f } });
        Add(root, guard, new MeshInstance3D { Name = "Mesh", Mesh = new CapsuleMesh { Radius = 0.4f, Height = 1.8f }, MaterialOverride = Material(Color.FromArgb(110, 220, 150)) });
        Add(root, guard, new MeshInstance3D { Name = "Visor", Position = new Vector3(0, 0.45f, -0.35f), Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.15f, 0.15f) }, MaterialOverride = Material(Color.FromArgb(30, 41, 59)) });
    }

    // A closed camera rail circling the yard, rising and dipping; its camera looks at the yard's centre.
    private static void BuildRail(Node root)
    {
        var points = new Vector3[8];
        for (var i = 0; i < points.Length; i++)
        {
            var a = i * MathF.Tau / points.Length;
            points[i] = new Vector3(MathF.Cos(a) * 20f, 9f + 3f * MathF.Sin(2 * a), MathF.Sin(a) * 16f);
        }

        var curve = new Curve3D { UpVectorEnabled = true };
        curve.SetSmoothPoints(points, closed: true);
        var rail = Add(root, root, new Path3D { Name = "Rail", Curve = curve });
        var follow = Add(root, rail, new PathFollow3D { Name = "Follow", Speed = 4f, RotationMode = PathRotationMode.None });
        Add(root, follow, new RailCamera3D { Name = "Camera", Fov = 60f, Target = new Vector3(0, 1, 0) });
    }

    private static void PlatformBody(Node root, Node parent, string name, Vector3 size)
    {
        var body = Add(root, parent, new AnimatableBody3D { Name = name });
        Add(root, body, new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = size } });
        Add(root, body, new MeshInstance3D { Name = "Mesh", Mesh = new BoxMesh { Size = size }, MaterialOverride = Material(Platform) });
    }

    private static void StaticBox(Node root, string name, Vector3 position, Vector3 size, Color color)
    {
        var body = Add(root, root, new StaticBody3D { Name = name, Position = position });
        Add(root, body, new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = size } });
        Add(root, body, new MeshInstance3D { Name = "Mesh", Mesh = new BoxMesh { Size = size }, MaterialOverride = Material(color) });
    }

    private static StandardMaterial3D Material(Color color) => new() { AlbedoColor = color };
}

/// <summary>A camera that keeps looking at <see cref="Target"/> as its rail carries it.</summary>
public sealed class RailCamera3D : Camera3D
{
    [Export] public Vector3 Target { get; set; }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (Vector3.DistanceSquared(GlobalPosition, Target) > 1e-4f)
            LookAt(Target);
    }
}
