using System.Drawing;
using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;
using Color = System.Drawing.Color;

namespace Demo;

/// <summary>
/// Navigation 3D (G3.11): a courtyard of walls on a 24 × 16 m floor, a <see cref="NavigationGrid3D"/> of half-metre cells
/// on the floor (its cells saved inline, written by code as a physics bake of the walls gives them; the Demo tests check
/// it against a real bake), and eight <see cref="NavChaser3D"/> capsules whose <see cref="NavigationAgent3D"/>s walk to the
/// clicked point (<see cref="NavigationDemo3D"/>). The panel switches the debug draw and shows arrivals.
/// </summary>
public static class Navigation3DScene
{
    public const int ChaserCount = 8;
    public const float Cell = 0.5f;
    public const int Columns = 48;
    public const int Rows = 32;
    public const float AgentRadius = 0.35f;

    /// <summary>The grid's corner: the floor is centred on the origin.</summary>
    public static readonly Vector3 Origin = new(-Columns * Cell / 2, 0, -Rows * Cell / 2);

    /// <summary>The walls: centre and size (metres; all 1.5 m tall, axis-aligned).</summary>
    public static readonly (Vector3 Centre, Vector3 Size)[] Walls =
    [
        (new(-6, 0.75f, -2.5f), new(0.6f, 1.5f, 9)),
        (new(-1, 0.75f, 3.5f), new(9, 1.5f, 0.6f)),
        (new(3.5f, 0.75f, -3.5f), new(0.6f, 1.5f, 7)),
        (new(7.5f, 0.75f, 1.5f), new(5, 1.5f, 0.6f)),
        (new(-9.5f, 0.75f, 5), new(3, 1.5f, 0.6f)),
        (new(0, 0.75f, -6), new(2, 1.5f, 2)),
    ];

    public static Node Build()
    {
        var root = new Node3D { Name = "navigation_3d" };
        var camera = Add(root, root, new Camera3D { Name = "Camera", Current = true, Position = new Vector3(-4.5f, 16, 15) });
        camera.LookAt(new Vector3(-4.5f, 0, 1.5f)); // the courtyard right of the panel
        camera.RotationDegrees = camera.RotationDegrees; // pin the Euler angles the saver writes
        Add(root, root, new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });
        Add(root, root, new DirectionalLight3D { Name = "Sun", Energy = 1.2f, CastsShadows = true, RotationDegrees = new Vector3(-60, -35, 0) });

        var size = new Vector3(Columns * Cell, 1, Rows * Cell);
        var floor = Add(root, root, new StaticBody3D { Name = "Floor", Position = new Vector3(0, -0.5f, 0) });
        Add(root, floor, new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = size } });
        Add(root, floor, new MeshInstance3D
        {
            Name = "Visual",
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(78, 90, 112), Roughness = 0.9f },
        });

        var walls = Add(root, root, new Node3D { Name = "Walls" });
        for (var i = 0; i < Walls.Length; i++)
        {
            var (centre, wallSize) = Walls[i];
            var wall = Add(root, walls, new StaticBody3D { Name = $"Wall{i}", Position = centre });
            Add(root, wall, new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = wallSize } });
            Add(root, wall, new MeshInstance3D
            {
                Name = "Visual",
                Mesh = new BoxMesh { Size = wallSize },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(148, 163, 184), Roughness = 0.8f },
            });
        }

        Add(root, root, new NavigationGrid3D
        {
            Name = "Grid",
            Position = Origin,
            Region = new Rect2I(0, 0, Columns, Rows),
            CellSize = new Vector2(Cell),
            AgentRadius = AgentRadius,
            Data = BakedData(),
        });

        Add(root, root, new MeshInstance3D
        {
            Name = "Target",
            Position = new Vector3(8, 0.05f, -5),
            Mesh = new CylinderMesh { TopRadius = 0.35f, BottomRadius = 0.35f, Height = 0.1f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 115, 217) },
        });

        var chasers = Add(root, root, new Node3D { Name = "Chasers" });
        for (var i = 0; i < ChaserCount; i++)
        {
            var chaser = Add(root, chasers, new NavChaser3D
            {
                Name = $"Chaser{i}",
                Position = new Vector3(-11f + i % 4 * 1.2f, 0.7f, 6.2f + i / 4 * 1.2f),
                CollisionLayer = 2,
                CollisionMask = 1,
            });
            Add(root, chaser, new CollisionShape3D { Name = "Shape", Shape = new CapsuleShape3D { Radius = 0.3f, Height = 1.4f } });
            var hue = NavChaser.ColorFor(i / (float)ChaserCount);
            Add(root, chaser, new MeshInstance3D
            {
                Name = "Visual",
                Mesh = new CapsuleMesh { Radius = 0.3f, Height = 1.4f },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = Color.FromArgb((int)(hue.X * 255), (int)(hue.Y * 255), (int)(hue.Z * 255)),
                    Roughness = 0.5f,
                },
            });
            Add(root, chaser, new NavigationAgent3D
            {
                Name = "Agent",
                PathHeightOffset = 0.7f,
                PathDesiredDistance = 0.4f,
                TargetDesiredDistance = 0.3f,
                TargetRepathDistance = 0.1f,
            });
        }

        Add(root, root, new NavigationDemo3D { Name = "Controller" });
        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new Navigation3DPanel { Name = "Panel" });
        return root;
    }

    /// <summary>
    /// What a physics bake of the walls gives (a cell is solid when it, grown by the agent radius less 1 mm, overlaps a
    /// wall's footprint), written by code so the scene file needs no physics to generate.
    /// </summary>
    public static NavigationGridData BakedData()
    {
        var grid = new AStarGrid2D { Region = new Rect2I(0, 0, Columns, Rows), CellSize = new Vector2(Cell) };
        grid.Update();
        var grow = AgentRadius - 0.001f;
        for (var z = 0; z < Rows; z++)
        {
            for (var x = 0; x < Columns; x++)
            {
                var minX = Origin.X + x * Cell - grow;
                var maxX = Origin.X + (x + 1) * Cell + grow;
                var minZ = Origin.Z + z * Cell - grow;
                var maxZ = Origin.Z + (z + 1) * Cell + grow;
                foreach (var (centre, size) in Walls)
                {
                    if (maxX > centre.X - size.X / 2 && minX < centre.X + size.X / 2 && maxZ > centre.Z - size.Z / 2 && minZ < centre.Z + size.Z / 2)
                    {
                        grid.SetPointSolid(new Vector2I(x, z), true);
                        break;
                    }
                }
            }
        }

        return NavigationGridData.FromGrid(grid, NavigationGridSource.Physics);
    }
}
