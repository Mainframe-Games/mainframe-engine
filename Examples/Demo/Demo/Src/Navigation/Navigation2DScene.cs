using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>
/// Navigation 2D (G3a): a walled maze of <see cref="StaticBody2D"/>s on a <see cref="NavigationGrid2D"/> (its cells baked
/// from the walls, saved inline), mud patches that cost three times as much to cross, and a dozen
/// <see cref="NavChaser"/> bodies whose <see cref="NavigationAgent2D"/>s walk to the clicked point. The panel switches the
/// diagonal mode, heuristic, jump point search and the debug draw, and "Click adds walls" turns clicks into wall cells
/// (<see cref="NavigationGrid2D.SetCellSolid"/>: every chaser repaths).
/// </summary>
public static class Navigation2DScene
{
    public const int Columns = 26;
    public const int Rows = 15;
    public const float Cell = 32f;
    public const int ChaserCount = 12;

    /// <summary>The grid's top-left corner: the maze is centred on the origin.</summary>
    public static readonly Vector2 Origin = new(-Columns * Cell / 2, -Rows * Cell / 2);

    private static readonly Vector4 WallColor = new(0.39f, 0.45f, 0.55f, 1);
    private static readonly Vector4 MudColor = new(0.42f, 0.3f, 0.18f, 1);

    /// <summary>The walls, in cells (x, y, width, height).</summary>
    public static readonly Rect2I[] Walls =
    [
        new(5, 0, 1, 9),
        new(5, 11, 1, 4),
        new(10, 4, 1, 9),
        new(11, 4, 4, 1),
        new(15, 0, 1, 7),
        new(15, 9, 5, 1),
        new(20, 3, 1, 9),
        new(21, 11, 3, 1),
        new(1, 6, 3, 1),
    ];

    /// <summary>The mud patches, in cells (weight 3).</summary>
    public static readonly Rect2I[] Mud =
    [
        new(6, 9, 4, 3),
        new(16, 2, 4, 3),
        new(11, 11, 4, 3),
    ];

    public const float MudWeight = 3f;

    public static Node Build()
    {
        var root = new Node2D { Name = "navigation_2d" };
        // The camera sits left of the origin so the maze is drawn right of the left-hand panel.
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
            Name = "Floor",
            Kind = DemoShapeKind.Rect,
            Size = new Vector2(Columns * Cell, Rows * Cell),
            Color = new Vector4(0.13f, 0.16f, 0.22f, 1),
            ColorBottom = new Vector4(0.11f, 0.13f, 0.19f, 1),
            Outline = new Vector4(0.3f, 0.36f, 0.48f, 1),
            OutlineWidth = 3f,
        });

        var mud = Add(root, root, new Node2D { Name = "Mud" });
        for (var i = 0; i < Mud.Length; i++)
        {
            var r = Mud[i];
            Add(root, mud, new DemoShape2D
            {
                Name = $"Mud{i}",
                Kind = DemoShapeKind.Rect,
                Position = Center(r),
                Size = new Vector2(r.Size.X, r.Size.Y) * Cell,
                Color = MudColor,
                ColorBottom = MudColor * new Vector4(0.8f, 0.8f, 0.8f, 1),
            });
        }

        var walls = Add(root, root, new Node2D { Name = "Walls" });
        for (var i = 0; i < Walls.Length; i++)
            AddWall(root, walls, $"Wall{i}", Walls[i]);

        Add(root, root, new NavigationGrid2D
        {
            Name = "Grid",
            Position = Origin,
            Region = new Rect2I(0, 0, Columns, Rows),
            CellSize = new Vector2(Cell),
            Data = BakedData(),
        });

        Add(root, root, new DemoShape2D
        {
            Name = "Target",
            Kind = DemoShapeKind.Circle,
            Radius = 9f,
            Position = Origin + new Vector2(23.5f, 7.5f) * Cell,
            Color = new Vector4(1f, 0.45f, 0.85f, 0.9f),
            Outline = new Vector4(1, 1, 1, 0.8f),
            OutlineWidth = 2f,
        });

        var chasers = Add(root, root, new Node2D { Name = "Chasers" });
        for (var i = 0; i < ChaserCount; i++)
        {
            var cell = new Vector2(1.5f + i % 3, 1.5f + i / 3 * 1.2f);
            var chaser = Add(root, chasers, new NavChaser
            {
                Name = $"Chaser{i}",
                Position = Origin + cell * Cell,
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
            Add(root, chaser, new NavigationAgent2D { Name = "Agent", PathDesiredDistance = 14f, TargetDesiredDistance = 10f });
        }

        Add(root, root, new NavigationDemo2D { Name = "Controller" });
        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new Navigation2DPanel { Name = "Panel" });
        return root;
    }

    /// <summary>The centre of a rectangle of cells, in pixels.</summary>
    public static Vector2 Center(Rect2I cells) =>
        Origin + (new Vector2(cells.Position.X, cells.Position.Y) + new Vector2(cells.Size.X, cells.Size.Y) * 0.5f) * Cell;

    /// <summary>A wall body covering <paramref name="cells"/>, with its visual.</summary>
    public static StaticBody2D AddWall(Node root, Node parent, string name, Rect2I cells)
    {
        var size = new Vector2(cells.Size.X, cells.Size.Y) * Cell;
        var body = Add(root, parent, new StaticBody2D { Name = name, Position = Center(cells) });
        Add(root, body, new CollisionShape2D { Name = "Shape", Shape = new RectangleShape2D { Size = size } });
        Add(root, body, new DemoShape2D
        {
            Name = "Visual",
            Kind = DemoShapeKind.Rect,
            Size = size,
            Color = WallColor,
            ColorBottom = WallColor * new Vector4(0.75f, 0.75f, 0.75f, 1),
        });
        return body;
    }

    /// <summary>
    /// What a physics bake of the maze gives (walls aligned to cells),
    /// written by code so the scene file needs no physics to generate; the mud's weights on top. The Demo tests check it
    /// against a real bake.
    /// </summary>
    public static NavigationGridData BakedData()
    {
        var grid = new AStarGrid2D { Region = new Rect2I(0, 0, Columns, Rows), CellSize = new Vector2(Cell) };
        grid.Update();
        foreach (var wall in Walls)
            grid.FillSolidRegion(wall);
        foreach (var patch in Mud)
            grid.FillWeightScaleRegion(patch, MudWeight);
        return NavigationGridData.FromGrid(grid, NavigationGridSource.Physics);
    }
}
