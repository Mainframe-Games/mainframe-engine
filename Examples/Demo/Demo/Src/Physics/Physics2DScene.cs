using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>Box2D bodies: a funnel and pegboard over a ground, shapes raining in, and click-to-spawn.</summary>
public static class Physics2DScene
{
    private static readonly Vector4 Wall = new(0.39f, 0.45f, 0.55f, 1);

    public static Node Build()
    {
        var root = new Node2D { Name = "physics_2d" };
        // The camera sits left of the origin so the world is drawn right of the left-hand panel.
        Add(root, root, new Camera2D { Name = "Camera", Current = true, Position = new Vector2(-60, 0) });
        Add(root, root, new DemoShape2D
        {
            Name = "Backdrop", Kind = DemoShapeKind.Rect, Size = new Vector2(1600, 900),
            Color = new Vector4(0.05f, 0.07f, 0.12f, 1), ColorBottom = new Vector4(0.09f, 0.11f, 0.2f, 1),
        });

        StaticRect(root, "Ground", new Vector2(0, 330), new Vector2(1100, 30), 0f);
        StaticRect(root, "LeftFunnel", new Vector2(-225, -170), new Vector2(300, 20), 30f);
        StaticRect(root, "RightFunnel", new Vector2(225, -170), new Vector2(300, 20), -30f);
        StaticRect(root, "LeftWall", new Vector2(-540, 120), new Vector2(20, 440), 0f);
        StaticRect(root, "RightWall", new Vector2(540, 120), new Vector2(20, 440), 0f);
        for (var row = 0; row < 4; row++)
            for (var col = 0; col < 7 - row % 2; col++)
                StaticCircle(root, $"Peg{row}_{col}", new Vector2(-300 + col * 100 + row % 2 * 50, 20 + row * 70), 9f);

        Add(root, root, new Spawner2D { Name = "Bodies" });
        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new Physics2DPanel { Name = "Panel" });
        return root;
    }

    private static void StaticRect(Node root, string name, Vector2 at, Vector2 size, float degrees)
    {
        var body = Add(root, root, new StaticBody2D { Name = name, Position = at, RotationDegrees = degrees });
        Add(root, body, new CollisionShape2D { Name = "Shape", Shape = new RectangleShape2D { Size = size } });
        Add(root, body, new DemoShape2D
        {
            Name = "Visual", Kind = DemoShapeKind.Rect, Size = size, Color = Wall,
            ColorBottom = Wall * new Vector4(0.75f, 0.75f, 0.75f, 1),
        });
    }

    private static void StaticCircle(Node root, string name, Vector2 at, float radius)
    {
        var body = Add(root, root, new StaticBody2D { Name = name, Position = at });
        Add(root, body, new CollisionShape2D { Name = "Shape", Shape = new CircleShape2D { Radius = radius } });
        Add(root, body, new DemoShape2D { Name = "Visual", Kind = DemoShapeKind.Circle, Radius = radius, Color = new Vector4(0.89f, 0.91f, 0.94f, 1) });
    }
}
