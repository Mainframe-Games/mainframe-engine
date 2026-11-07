using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;
using Color = System.Drawing.Color;

namespace Demo;

/// <summary>
/// Navigation Mesh 3D (G3.12): uneven ground a grid cannot describe — a floor, a terrace 1.5 m up reached by a ramp, a
/// raised deck reached by stairs (0.2 m steps, under the climb height), pillars and crates — on a
/// <see cref="NavigationRegion3D"/> whose <see cref="NavigationMesh"/> is baked with Recast when the scene is built (saved
/// inline; the Demo tests check it against a fresh bake). Capsule chasers walk to the clicked surface, up the ramp and
/// the stairs.
/// </summary>
public static class NavigationMesh3DScene
{
    public const int ChaserCount = 6;

    /// <summary>The level's static boxes: centre, size, rotation (degrees about Z), colour.</summary>
    public static readonly (Vector3 Centre, Vector3 Size, float Tilt, Color Color)[] Blocks =
    [
        (new(0, -0.5f, 0), new(24, 1, 16), 0f, Color.FromArgb(78, 90, 112)),          // floor
        (new(7, 0.75f, -3), new(8, 1.5f, 8), 0f, Color.FromArgb(100, 116, 139)),      // terrace
        (new(0.9f, 0.75f, -5.5f), new(4.6f, 0.2f, 2.4f), 20f, Color.FromArgb(148, 163, 184)), // ramp onto it
        (new(-6, 0.6f, 4), new(6, 1.2f, 5), 0f, Color.FromArgb(100, 116, 139)),       // deck
        (new(-3, 0.6f, -3), new(1.4f, 1.2f, 1.4f), 0f, Color.FromArgb(148, 163, 184)), // crate
        (new(2, 1, 3), new(1, 2, 1), 0f, Color.FromArgb(148, 163, 184)),               // pillar
        (new(8, 2.5f, -1), new(1, 2, 1), 0f, Color.FromArgb(148, 163, 184)),           // pillar on the terrace
    ];

    public static Node Build()
    {
        var root = new Node3D { Name = "navmesh_3d" };
        var camera = Add(root, root, new Camera3D { Name = "Camera", Current = true, Position = new Vector3(-3, 15, 15) });
        camera.LookAt(new Vector3(-3, 0, 1));
        camera.RotationDegrees = camera.RotationDegrees;
        Add(root, root, new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });
        Add(root, root, new DirectionalLight3D { Name = "Sun", Energy = 1.2f, CastsShadows = true, RotationDegrees = new Vector3(-60, -35, 0) });

        var region = Add(root, root, new NavigationRegion3D
        {
            Name = "Level",
            NavigationMesh = new NavigationMesh { AgentRadius = 0.35f, AgentHeight = 1.4f, AgentMaxClimb = 0.3f, CellSize = 0.15f, CellHeight = 0.1f },
        });
        for (var i = 0; i < Blocks.Length; i++)
        {
            var (centre, size, tilt, color) = Blocks[i];
            AddBlock(root, region, $"Block{i}", centre, size, tilt, color);
        }

        // Stairs up the deck's open side (x = -3): six 0.2 m steps, 0.25 m deep, the top one against the deck, floor in front.
        for (var i = 0; i < 6; i++)
        {
            var height = 0.2f * (i + 1);
            AddBlock(root, region, $"Step{i}", new Vector3(-2.875f + (5 - i) * 0.25f, height / 2, 4), new Vector3(0.25f, height, 2), 0f,
                Color.FromArgb(120, 134, 160));
        }

        region.Bake();

        Add(root, root, new MeshInstance3D
        {
            Name = "Target",
            Position = new Vector3(8, 1.55f, -5),
            Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.3f, Height = 0.1f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 115, 217) },
        });

        var chasers = Add(root, root, new Node3D { Name = "Chasers" });
        for (var i = 0; i < ChaserCount; i++)
        {
            var chaser = Add(root, chasers, new NavChaser3D
            {
                Name = $"Chaser{i}",
                Position = new Vector3(-10 + i % 3 * 1.1f, 0.7f, -6 + i / 3 * 1.1f),
                CollisionLayer = 2,
                CollisionMask = 1,
            });
            Add(root, chaser, new CollisionShape3D { Name = "Shape", Shape = new CapsuleShape3D { Radius = 0.3f, Height = 1.4f } });
            var hue = NavChaser.ColorFor(i / (float)ChaserCount);
            Add(root, chaser, new MeshInstance3D
            {
                Name = "Visual",
                Mesh = new CapsuleMesh { Radius = 0.3f, Height = 1.4f },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb((int)(hue.X * 255), (int)(hue.Y * 255), (int)(hue.Z * 255)) },
            });
            Add(root, chaser, new NavigationAgent3D
            {
                Name = "Agent",
                PathHeightOffset = 0.7f,
                PathDesiredDistance = 0.4f,
                TargetDesiredDistance = 0.35f,
                TargetRepathDistance = 0.1f,
            });
        }

        Add(root, root, new NavigationDemo3D { Name = "Controller", KeepHitHeight = true, Ring = 0.8f });
        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new Navigation3DPanel { Name = "Panel", Source = "Assets/Navigation/navmesh_3d_panel.rml" });
        return root;
    }

    private static void AddBlock(Node root, Node parent, string name, Vector3 centre, Vector3 size, float tilt, Color color)
    {
        var body = Add(root, parent, new StaticBody3D { Name = name, Position = centre, RotationDegrees = new Vector3(0, 0, tilt) });
        Add(root, body, new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = size } });
        Add(root, body, new MeshInstance3D
        {
            Name = "Visual",
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.85f },
        });
    }
}
