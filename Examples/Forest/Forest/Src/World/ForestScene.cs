using System.Numerics;
using MainframeEngine;
using Color = System.Drawing.Color;

namespace Forest;

/// <summary>
/// The Forest's main scene for now (<c>Content/Scenes/forest.mscene</c>, written by <c>--write-scenes</c>): a flat
/// 256 m ground with a 1.2 m deep trench holding a test <see cref="River3D"/> (ramps at both ends), boxes, slopes and a
/// low beam to walk, climb and crouch under, a shadowed sun, the procedural sky and the
/// <see cref="FirstPersonController"/>. The content wave replaces it with the generated valley.
/// </summary>
public static class ForestScene
{
    public const string Id = "forest";

    /// <summary>The scene file, relative to the project folder.</summary>
    public const string Path = "Content/Scenes/forest.mscene";

    /// <summary>Ground top is y = 0; the trench runs along Z at x = 16 … 22, its floor at y = −1.2, from z = −40 to 40.</summary>
    public const float TrenchMinX = 16f;

    public const float TrenchMaxX = 22f;
    public const float TrenchHalfLength = 40f;
    public const float TrenchDepth = 1.2f;
    public const float RampLength = 6f;

    /// <summary>The test stream's surface (y) at its upstream (south, +Z) and downstream ends.</summary>
    public const float StreamSurfaceStart = -0.15f;

    public const float StreamSurfaceEnd = -0.3f;

    /// <summary>Where the player starts (feet) and the start yaw in degrees (facing the stream, north-east).</summary>
    public static readonly Vector3 Spawn = new(6, 0, 8);

    public const float SpawnYawDegrees = -60f;

    public static Node Build()
    {
        var root = new Node3D { Name = Id };

        // Sun first: the sky's sun disc follows its direction.
        var sun = Add(root, root, new DirectionalLight3D
        {
            Name = "Sun",
            Color = new Vector3(1f, 0.94f, 0.84f),
            Energy = 1.7f,
            CastsShadows = true,
            ShadowCascades = 3,
            RotationDegrees = new Vector3(-38, -35, 0),
        });
        Add(root, root, new WorldEnvironment
        {
            Name = "Environment",
            Sky = new Sky { Mode = SkyEnvironmentType.Procedural, SunDirection = -sun.GlobalForward },
            AmbientColor = new Vector3(0.22f, 0.26f, 0.3f),
        });

        BuildGround(root);
        BuildStream(root);
        BuildCourse(root);
        BuildPlayer(root);
        return root;
    }

    private static void BuildGround(Node root)
    {
        var ground = Add(root, root, new Node3D { Name = "Ground" });
        var moss = Lit(Color.FromArgb(84, 104, 58), shininess: 6f);
        var bed = Lit(Color.FromArgb(92, 80, 62), shininess: 6f);
        const float half = 128f;
        // Slabs around the trench (top at y = 0).
        Slab(root, ground, "West", new Vector3(-half, -1, -half), new Vector3(TrenchMinX, 0, half), moss, "moss");
        Slab(root, ground, "East", new Vector3(TrenchMaxX, -1, -half), new Vector3(half, 0, half), moss, "moss");
        Slab(root, ground, "North", new Vector3(TrenchMinX, -1, -half), new Vector3(TrenchMaxX, 0, -TrenchHalfLength), moss, "moss");
        Slab(root, ground, "South", new Vector3(TrenchMinX, -1, TrenchHalfLength), new Vector3(TrenchMaxX, 0, half), moss, "moss");
        Slab(root, ground, "TrenchFloor", new Vector3(TrenchMinX, -TrenchDepth - 1, -TrenchHalfLength),
            new Vector3(TrenchMaxX, -TrenchDepth, TrenchHalfLength), bed, "gravel");

        // Ramps out of the trench at both ends (floor to ground over RampLength metres).
        var angle = MathF.Atan2(TrenchDepth, RampLength);
        var length = MathF.Sqrt(RampLength * RampLength + TrenchDepth * TrenchDepth);
        foreach (var (name, sign) in new[] { ("RampSouth", 1f), ("RampNorth", -1f) })
        {
            // The ramp's top centre is halfway up; the box hangs 0.2 m below its top face.
            var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -sign * angle);
            var topCentre = new Vector3((TrenchMinX + TrenchMaxX) / 2, -TrenchDepth / 2, sign * (TrenchHalfLength - RampLength / 2));
            var centre = topCentre - Vector3.Transform(Vector3.UnitY, tilt) * 0.2f;
            Box(root, ground, name, centre, new Vector3(TrenchMaxX - TrenchMinX, 0.4f, length), bed, "gravel",
                rotationDegrees: new Vector3(-sign * float.RadiansToDegrees(angle), 0, 0));
        }
    }

    private static void BuildStream(Node root)
    {
        var curve = new Curve3D { ResourceName = "Test stream", BakeInterval = 0.25f };
        var x = (TrenchMinX + TrenchMaxX) / 2;
        var start = new Vector3(x, StreamSurfaceStart, TrenchHalfLength - RampLength - 1f);
        var end = new Vector3(x, StreamSurfaceEnd, -(TrenchHalfLength - RampLength - 1f));
        var span = end - start;
        curve.AddPoint(start, @out: span / 3);
        curve.AddPoint(end, @in: -span / 3);
        for (var i = 0; i < curve.PointCount; i++)
        {
            curve.SetPointWidth(i, TrenchMaxX - TrenchMinX);
            curve.SetPointDepth(i, TrenchDepth - (i == 0 ? -StreamSurfaceStart : -StreamSurfaceEnd));
        }

        Add(root, root, new River3D { Name = "Stream", Curve = curve });
    }

    private static void BuildCourse(Node root)
    {
        var course = Add(root, root, new Node3D { Name = "Course" });
        var rock = Lit(Color.FromArgb(128, 128, 122), shininess: 12f);
        var wood = Lit(Color.FromArgb(122, 86, 52), shininess: 10f);

        // Crates to bump into and a stack to jump onto (≈ 0.9 m jump: the 0.8 m crate is climbable).
        Box(root, course, "Crate1", new Vector3(2, 0.4f, -4), new Vector3(0.8f), wood, "wood");
        Box(root, course, "Crate2", new Vector3(3.1f, 0.4f, -4.3f), new Vector3(0.8f), wood, "wood", rotationDegrees: new Vector3(0, 20, 0));
        Box(root, course, "Crate3", new Vector3(2.5f, 1.2f, -4.1f), new Vector3(0.8f), wood, "wood", rotationDegrees: new Vector3(0, 8, 0));

        // Slopes: 15° and 35° walk up (FloorMaxAngle 45°), 55° does not.
        Slope(root, course, "Slope15", new Vector3(-4, 0, -10), 15f, rock);
        Slope(root, course, "Slope35", new Vector3(0, 0, -10), 35f, rock);
        Slope(root, course, "Slope55", new Vector3(4, 0, -10), 55f, rock);

        // A low beam on two posts: crouch (1.1 m) to pass under it (1.3 m clearance).
        Box(root, course, "BeamPostL", new Vector3(8, 0.75f, -4), new Vector3(0.3f, 1.5f, 0.3f), wood, "wood");
        Box(root, course, "BeamPostR", new Vector3(11, 0.75f, -4), new Vector3(0.3f, 1.5f, 0.3f), wood, "wood");
        Box(root, course, "Beam", new Vector3(9.5f, 1.4f, -4), new Vector3(3.3f, 0.2f, 1.2f), wood, "wood");

        // A rock outcrop by the stream.
        Box(root, course, "Boulder", new Vector3(12.5f, 0.6f, 2), new Vector3(2.2f, 1.2f, 1.8f), rock, "rock", rotationDegrees: new Vector3(0, 30, 6));
    }

    private static void BuildPlayer(Node root)
    {
        var player = Add(root, root, new FirstPersonController { Name = "Player", Position = Spawn, FloorSnapLength = 0.3f });
        Add(root, player, new CollisionShape3D
        {
            Name = "Shape",
            Shape = new CapsuleShape3D { Radius = player.Radius, Height = player.StandingHeight },
            Position = new Vector3(0, player.StandingHeight / 2, 0),
        });
        var head = Add(root, player, new Node3D
        {
            Name = "Head",
            Position = new Vector3(0, player.StandingHeight - FirstPersonController.EyeBelowTop, 0),
            RotationDegrees = new Vector3(0, SpawnYawDegrees, 0),
        });
        Add(root, head, new Camera3D { Name = "Camera", Current = true, Near = 0.05f, Far = 2000f, Fov = FirstPersonController.VerticalFov(90f, 16f / 9f) });
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>Adds <paramref name="child"/> under <paramref name="parent"/>, owned by the scene root (so it is saved).</summary>
    public static T Add<T>(Node root, Node parent, T child) where T : Node
    {
        parent.AddChild(child);
        child.Owner = root;
        return child;
    }

    private static StandardMaterial3D Lit(Color color, float shininess = 32f) => new() { AlbedoColor = color, Shininess = shininess };

    private static void Slab(Node root, Node parent, string name, Vector3 min, Vector3 max, Material material, string surface) =>
        Box(root, parent, name, (min + max) / 2, max - min, material, surface);

    private static void Box(Node root, Node parent, string name, Vector3 centre, Vector3 size, Material material, string surface,
        Vector3 rotationDegrees = default)
    {
        var body = Add(root, parent, new SurfaceBody3D { Name = name, Surface = surface, Position = centre, RotationDegrees = rotationDegrees });
        Add(root, body, new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = size } });
        Add(root, body, new MeshInstance3D { Name = "Visual", Mesh = new BoxMesh { Size = size }, MaterialOverride = material });
    }

    /// <summary>A 4 m wide, 6 m long ramp rising at <paramref name="degrees"/> towards −Z from <paramref name="foot"/>.</summary>
    private static void Slope(Node root, Node parent, string name, Vector3 foot, float degrees, Material material)
    {
        var angle = float.DegreesToRadians(degrees);
        const float length = 6f;
        const float thickness = 0.4f;
        var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, angle);
        // Top face centre halfway up the ramp; the box hangs below it.
        var topCentre = foot + new Vector3(0, MathF.Sin(angle) * length / 2, -MathF.Cos(angle) * length / 2);
        var centre = topCentre - Vector3.Transform(Vector3.UnitY, tilt) * (thickness / 2);
        Box(root, parent, name, centre, new Vector3(4, thickness, length), material, "rock", rotationDegrees: new Vector3(degrees, 0, 0));
    }
}
