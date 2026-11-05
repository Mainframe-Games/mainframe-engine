using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>
/// Paths 2D (G13): <see cref="Path2D"/> curves with <see cref="PathFollow2D"/> followers. A closed race track whose cars
/// lap at their own speeds (turning with the curve), an open arc whose lift ping-pongs between its ends, and a guard
/// walking a patrol route by the carrot recipe (<see cref="PathGuard2D"/>). Select a path in the editor to edit its
/// curve with the curve tool.
/// </summary>
public static class Paths2DScene
{
    /// <summary>The cars' speeds in pixels per second.</summary>
    public static readonly float[] CarSpeeds = [210f, 180f, 150f, 120f];

    private static readonly Vector4[] CarColors =
    [
        new(1f, 0.42f, 0.38f, 1),
        new(1f, 0.8f, 0.3f, 1),
        new(0.4f, 0.85f, 0.55f, 1),
        new(0.45f, 0.7f, 1f, 1),
    ];

    public const float LiftSpeed = 140f;

    public static Node Build()
    {
        var root = new Node2D { Name = "paths_2d" };
        // The camera sits left of the origin so the paths are drawn right of the left-hand panel.
        Add(root, root, new Camera2D { Name = "Camera", Current = true, Position = new Vector2(-120, 0) });
        Add(root, root, new DemoShape2D
        {
            Name = "Backdrop",
            Kind = DemoShapeKind.Rect,
            Size = new Vector2(1800, 1000),
            Color = new Vector4(0.05f, 0.07f, 0.12f, 1),
            ColorBottom = new Vector4(0.08f, 0.1f, 0.17f, 1),
        });

        BuildTrack(root);
        BuildLift(root);
        BuildPatrol(root);

        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new Paths2DPanel { Name = "Panel" });
        return root;
    }

    // A closed oval: four cars lap it at different speeds, +X along the curve.
    private static void BuildTrack(Node root)
    {
        var curve = new Curve2D();
        Vector2[] points = new Vector2[8];
        for (var i = 0; i < points.Length; i++)
        {
            var a = i * MathF.Tau / points.Length;
            points[i] = new Vector2(MathF.Cos(a) * 320f, MathF.Sin(a) * 135f);
        }

        curve.SetSmoothPoints(points, closed: true);
        var track = Add(root, root, new Path2D { Name = "Track", Position = new Vector2(60, -120), Curve = curve });
        Add(root, track, new PathTrack2D { Name = "Road", Width = 26f });
        for (var i = 0; i < CarSpeeds.Length; i++)
        {
            var car = Add(root, track, new PathFollow2D
            {
                Name = $"Car{i}",
                Speed = CarSpeeds[i],
                Progress = i * 385f, // a quarter lap apart
                VOffset = i % 2 == 0 ? -6f : 6f,
            });
            Add(root, car, new DemoShape2D
            {
                Name = "Body",
                Kind = DemoShapeKind.Polygon,
                Points = [new(16, 0), new(-10, -9), new(-5, 0), new(-10, 9)],
                Color = CarColors[i],
                Outline = new Vector4(0, 0, 0, 0.45f),
                OutlineWidth = 2f,
            });
        }
    }

    // An open arc: a lift ping-pongs between its ends.
    private static void BuildLift(Node root)
    {
        var curve = new Curve2D();
        curve.SetSmoothPoints([new(0, 150), new(120, 0), new(250, 60), new(330, 170)], closed: false);
        var arc = Add(root, root, new Path2D { Name = "Arc", Position = new Vector2(-390, 90), Curve = curve });
        Add(root, arc, new PathTrack2D { Name = "Rail", Width = 6f, Color = new Vector4(0.36f, 0.4f, 0.5f, 1), LineColor = Vector4.Zero });
        var lift = Add(root, arc, new PathFollow2D { Name = "Lift", Speed = LiftSpeed, Loop = false, PingPong = true, Rotates = false });
        Add(root, lift, new DemoShape2D
        {
            Name = "Platform",
            Kind = DemoShapeKind.Rect,
            Size = new Vector2(64, 14),
            Color = new Vector4(0.95f, 0.6f, 0.95f, 1),
            ColorBottom = new Vector4(0.7f, 0.4f, 0.75f, 1),
            Outline = new Vector4(1, 1, 1, 0.5f),
            OutlineWidth = 2f,
        });
    }

    // A rounded route the guard patrols: it starts inside and joins the route at its closest point.
    private static void BuildPatrol(Node root)
    {
        var curve = new Curve2D();
        curve.SetSmoothPoints([new(-140, -90), new(140, -90), new(160, 90), new(-140, 100)], closed: true, tension: 0.35f);
        var route = Add(root, root, new Path2D { Name = "Route", Position = new Vector2(260, 200), Curve = curve });
        Add(root, route, new PathTrack2D { Name = "Trail", Width = 4f, Color = new Vector4(0.4f, 0.75f, 0.55f, 0.5f), LineColor = Vector4.Zero });
        var carrot = Add(root, route, new PathFollow2D { Name = "Carrot", Rotates = false });
        Add(root, carrot, new DemoShape2D { Name = "Marker", Kind = DemoShapeKind.Circle, Radius = 4f, Color = new Vector4(1f, 0.6f, 0.2f, 0.9f) });

        var guard = Add(root, root, new PathGuard2D { Name = "Guard", Route = new NodePath("../Route"), Position = new Vector2(260, 200) });
        Add(root, guard, new DemoShape2D
        {
            Name = "Body",
            Kind = DemoShapeKind.Circle,
            Radius = 12f,
            Color = new Vector4(0.4f, 0.85f, 0.6f, 1),
            Outline = new Vector4(0, 0, 0, 0.45f),
            OutlineWidth = 2f,
        });
        Add(root, guard, new DemoShape2D
        {
            Name = "Facing",
            Kind = DemoShapeKind.Polygon,
            Points = [new(20, 0), new(10, -5), new(10, 5)],
            Color = new Vector4(1, 1, 1, 0.85f),
        });
    }
}
