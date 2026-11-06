using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>
/// Navigation Mesh 2D (G3.9): a hall of slanted walls and round pillars on a <see cref="NavigationRegion2D"/> whose
/// <see cref="NavigationPolygon"/> is baked from them (a baking rectangle minus the static bodies, 12 px of agent radius),
/// a pond carved by a <see cref="NavigationObstacle2D"/> with <see cref="NavigationObstacle2D.AffectNavigationMesh"/>, and
/// a river the chasers cross by two ferries — <see cref="NavigationLink2D"/>s to a second region on the far bank. The
/// chasers follow funnel paths to the clicked point, avoid each other (G3.7) and hop across a link when their agents raise
/// <see cref="NavigationAgent2D.LinkReached"/>. The panel re-bakes, carves the pond or not, and switches the debug draw
/// and avoidance.
/// </summary>
public static class NavigationMesh2DScene
{
    public const int ChaserCount = 10;
    public const float AgentRadius = 12f;

    /// <summary>The floor (pixels): the whole level, centred on the origin.</summary>
    public static readonly Rect2 Floor = new(-416, -240, 832, 480);

    /// <summary>The river between the hall and the bank.</summary>
    public static readonly Rect2 River = new(250, -240, 60, 480);

    /// <summary>The walls: centre, size, rotation (radians).</summary>
    public static readonly (Vector2 Centre, Vector2 Size, float Rotation)[] Walls =
    [
        (new(-250, -70), new(24, 300), 0.35f),
        (new(-60, 110), new(280, 24), -0.25f),
        (new(120, -90), new(24, 260), -0.45f),
    ];

    /// <summary>The pillars: centre, radius.</summary>
    public static readonly (Vector2 Centre, float Radius)[] Pillars =
    [
        (new(-110, -130), 34f),
        (new(180, 150), 30f),
        (new(363, 0), 20f),
    ];

    /// <summary>The ferries across the river: from the hall's mesh edge to the bank's (the agent radius in from each shore).</summary>
    public static readonly (Vector2 Start, Vector2 End)[] Ferries =
    [
        (new(250 - AgentRadius, -170), new(310 + AgentRadius, -170)),
        (new(250 - AgentRadius, 170), new(310 + AgentRadius, 170)),
    ];

    private static readonly Vector4 WallColor = new(0.39f, 0.45f, 0.55f, 1);
    private static readonly Vector4 WaterColor = new(0.12f, 0.32f, 0.55f, 1);

    public static Node Build()
    {
        var root = new Node2D { Name = "navmesh_2d" };
        Add(root, root, new Camera2D { Name = "Camera", Current = true, Position = new Vector2(-120, 0) });
        Add(root, root, new DemoShape2D
        {
            Name = "Backdrop",
            Kind = DemoShapeKind.Rect,
            Size = new Vector2(1800, 1000),
            Color = new Vector4(0.05f, 0.07f, 0.12f, 1),
            ColorBottom = new Vector4(0.08f, 0.1f, 0.17f, 1),
        });
        Add(root, root, new DemoShape2D
        {
            Name = "FloorShape",
            Kind = DemoShapeKind.Rect,
            Size = Floor.Size,
            Color = new Vector4(0.13f, 0.16f, 0.22f, 1),
            ColorBottom = new Vector4(0.11f, 0.13f, 0.19f, 1),
            Outline = new Vector4(0.3f, 0.36f, 0.48f, 1),
            OutlineWidth = 3f,
        });
        // The level's outer walls (outside the baking rectangles: nothing to carve) keep pushed chasers in.
        var bounds = Add(root, root, new Node2D { Name = "Bounds" });
        (Vector2 Centre, Vector2 Size)[] sides =
        [
            (new(0, Floor.Position.Y - 20), new(Floor.Size.X + 80, 40)),
            (new(0, Floor.End.Y + 20), new(Floor.Size.X + 80, 40)),
            (new(Floor.Position.X - 20, 0), new(40, Floor.Size.Y)),
            (new(Floor.End.X + 20, 0), new(40, Floor.Size.Y)),
        ];
        for (var i = 0; i < sides.Length; i++)
        {
            var side = Add(root, bounds, new StaticBody2D { Name = $"Side{i}", Position = sides[i].Centre });
            Add(root, side, new CollisionShape2D { Name = "Shape", Shape = new RectangleShape2D { Size = sides[i].Size } });
        }

        // The river blocks walking (a static body; no region parses it) and avoidance keeps the crowds at the landings off it.
        var river = Add(root, root, new StaticBody2D { Name = "River", Position = River.Position + River.Size * 0.5f });
        Add(root, river, new CollisionShape2D { Name = "Shape", Shape = new RectangleShape2D { Size = River.Size } });
        var half = River.Size * 0.5f;
        Add(root, river, new NavigationObstacle2D
        {
            Name = "Obstacle",
            Vertices = [new(-half.X, -half.Y), new(half.X, -half.Y), new(half.X, half.Y), new(-half.X, half.Y)],
        });
        Add(root, river, new DemoShape2D
        {
            Name = "Visual",
            Kind = DemoShapeKind.Rect,
            Size = River.Size,
            Color = WaterColor,
            ColorBottom = WaterColor * new Vector4(0.7f, 0.7f, 0.8f, 1),
        });

        // The hall: everything left of the river, its walls, pillars and pond as children (what the bake parses).
        var hall = Add(root, root, new NavigationRegion2D
        {
            Name = "Hall",
            NavigationPolygon = new NavigationPolygon
            {
                BakingRect = new Rect2(Floor.Position, new Vector2(River.Position.X - Floor.Position.X, Floor.Size.Y)),
                AgentRadius = AgentRadius,
            },
        });
        for (var i = 0; i < Walls.Length; i++)
        {
            var (centre, size, rotation) = Walls[i];
            var body = Add(root, hall, new StaticBody2D { Name = $"Wall{i}", Position = centre, Rotation = rotation });
            Add(root, body, new CollisionShape2D { Name = "Shape", Shape = new RectangleShape2D { Size = size } });
            Add(root, body, new DemoShape2D
            {
                Name = "Visual",
                Kind = DemoShapeKind.Rect,
                Size = size,
                Color = WallColor,
                ColorBottom = WallColor * new Vector4(0.75f, 0.75f, 0.75f, 1),
            });
        }

        AddPillar(root, hall, "Pillar0", Pillars[0]);
        AddPillar(root, hall, "Pillar1", Pillars[1]);

        var pond = Add(root, hall, new NavigationObstacle2D
        {
            Name = "Pond",
            Position = new Vector2(-330, 150),
            AffectNavigationMesh = true,
            AvoidanceEnabled = false,
            Vertices = [new(-50, -10), new(-20, -42), new(30, -36), new(52, 0), new(28, 38), new(-30, 34)],
        });
        Add(root, pond, new DemoShape2D { Name = "Visual", Kind = DemoShapeKind.Polygon, Points = pond.Vertices, Color = WaterColor });

        // The far bank: its own region, joined to the hall only by the ferries.
        var bank = Add(root, root, new NavigationRegion2D
        {
            Name = "Bank",
            NavigationPolygon = new NavigationPolygon
            {
                BakingRect = new Rect2(River.End.X, Floor.Position.Y, Floor.End.X - River.End.X, Floor.Size.Y),
                AgentRadius = AgentRadius,
            },
        });
        AddPillar(root, bank, "Rock", Pillars[2]);

        for (var i = 0; i < Ferries.Length; i++)
        {
            var (start, end) = Ferries[i];
            Add(root, root, new NavigationLink2D { Name = $"Ferry{i}", StartPosition = start, EndPosition = end });
            Add(root, root, new DemoShape2D
            {
                Name = $"FerryDeck{i}",
                Kind = DemoShapeKind.Rect,
                Position = (start + end) * 0.5f,
                Size = new Vector2(end.X - start.X, 18),
                Color = new Vector4(0.55f, 0.42f, 0.28f, 1),
                Outline = new Vector4(0, 0, 0, 0.35f),
                OutlineWidth = 2f,
            });
        }

        hall.Bake();
        bank.Bake();

        Add(root, root, new DemoShape2D
        {
            Name = "Target",
            Kind = DemoShapeKind.Circle,
            Radius = 9f,
            Position = new Vector2(370, 120),
            Color = new Vector4(1f, 0.45f, 0.85f, 0.9f),
            Outline = new Vector4(1, 1, 1, 0.8f),
            OutlineWidth = 2f,
        });

        var chasers = Add(root, root, new Node2D { Name = "Chasers" });
        for (var i = 0; i < ChaserCount; i++)
        {
            var chaser = Add(root, chasers, new NavChaser
            {
                Name = $"Chaser{i}",
                Position = new Vector2(-385 + i % 2 * 36, -200 + i / 2 * 40),
                Hue = i / (float)ChaserCount,
                CollisionLayer = 2,
                CollisionMask = 1,
            });
            Add(root, chaser, new CollisionShape2D { Name = "Shape", Shape = new CircleShape2D { Radius = 9f } });
            Add(root, chaser, new DemoShape2D
            {
                Name = "Visual",
                Kind = DemoShapeKind.Circle,
                Radius = 9f,
                Color = NavChaser.ColorFor(i / (float)ChaserCount),
                Outline = new Vector4(0, 0, 0, 0.4f),
                OutlineWidth = 2f,
            });
            Add(root, chaser, new NavigationAgent2D
            {
                Name = "Agent",
                PathDesiredDistance = 14f,
                TargetDesiredDistance = 10f,
                AvoidanceEnabled = true,
                Radius = 10f,
                MaxSpeed = 150f,
                NeighborDistance = 96f,
                MaxNeighbors = 8,
            });
        }

        Add(root, root, new NavigationMeshDemo2D { Name = "Controller" });
        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new NavigationMesh2DPanel { Name = "Panel" });
        return root;
    }

    private static void AddPillar(Node root, Node parent, string name, (Vector2 Centre, float Radius) pillar)
    {
        var body = Add(root, parent, new StaticBody2D { Name = name, Position = pillar.Centre });
        Add(root, body, new CollisionShape2D { Name = "Shape", Shape = new CircleShape2D { Radius = pillar.Radius } });
        Add(root, body, new DemoShape2D
        {
            Name = "Visual",
            Kind = DemoShapeKind.Circle,
            Radius = pillar.Radius,
            Color = WallColor,
            Outline = new Vector4(0, 0, 0, 0.35f),
            OutlineWidth = 2f,
        });
    }
}
