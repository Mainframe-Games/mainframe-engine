using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// G6.4: mesh levels of detail. A row of spheres recedes from the camera; each has three coarser levels with their own
/// geometry (24, 12 and 6 segments), so farther spheres are visibly faceted, and a sun casts their shadows at the
/// same level. A subdivided plane in the distance has an index-only level (its four corners). A sphere next to the
/// camera with a tiny <see cref="GeometryInstance3D.LodBias"/> draws coarse anyway, and one past its visibility range is
/// not drawn. Checks each instance's level after the first frames, and that the levels grow with distance (at 1× and at
/// moltenvk's 2× pixel scale).
/// </summary>
public sealed class LodScene(HostOptions host) : RenderTestGame(host)
{
    private static readonly float[] Distances = [0f, 11f, 26f, 56f, 96f];
    private static readonly int[] LevelSegments = [24, 12, 6];

    private readonly List<MeshInstance3D> _row = [];
    private MeshInstance3D _biased = null!;
    private MeshInstance3D _field = null!;

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(LodScene) };
        var camera = new Camera3D { Name = "Camera", Position = new Vector3(0, 1.6f, 5f), Far = 200f };
        camera.LookAt(new Vector3(0, 0.8f, -20f));
        scene.AddChild(camera);

        scene.AddChild(new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });
        var sun = new DirectionalLight3D { Name = "Sun", Position = new Vector3(0, 10, 0), Energy = 1.1f };
        sun.LookAt(sun.Position + Vector3.Normalize(new Vector3(0.6f, -0.7f, 0.35f)));
        scene.AddChild(sun);

        scene.AddChild(new MeshInstance3D
        {
            Name = "Floor",
            Position = new Vector3(0, 0, -50),
            Mesh = new PlaneMesh { Size = new Vector2(40, 130) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 120, 125, 110) },
        });

        var sphere = LodSphere();
        var stone = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 220, 150, 90) };
        for (var i = 0; i < Distances.Length; i++)
        {
            var node = new MeshInstance3D
            {
                Name = $"Sphere{i}",
                Position = new Vector3(i % 2 == 0 ? -1.2f : 1.2f, 1f, -Distances[i]),
                Mesh = sphere,
                MaterialOverride = stone,
            };
            _row.Add(node);
            scene.AddChild(node);
        }

        _biased = new MeshInstance3D
        {
            Name = "Biased",
            Position = new Vector3(2.6f, 0.6f, 0.5f),
            Scale = new Vector3(0.6f),
            Mesh = sphere,
            LodBias = 0.02f,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 110, 170, 230) },
        };
        scene.AddChild(_biased);

        scene.AddChild(new MeshInstance3D
        {
            Name = "OutOfRange",
            Position = new Vector3(-2.6f, 0.6f, 0.5f),
            Mesh = sphere,
            VisibilityRangeEnd = 2f,
        });

        _field = new MeshInstance3D
        {
            Name = "Field",
            Position = new Vector3(6f, 0.05f, -50f),
            Mesh = FieldMesh(),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 90, 160, 90) },
        };
        scene.AddChild(_field);

        Tree.ChangeScene(scene);
    }

    // A unit sphere with three coarser levels; errors are each level's chord error (r · (1 − cos(π / segments))).
    private static ArrayMesh LodSphere()
    {
        static MeshSurface Sphere(int segments) =>
            new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = segments, Rings = segments / 2 }.GetSurface(0);

        static float ChordError(int segments) => 1f - MathF.Cos(MathF.PI / segments);

        var surface = Sphere(64);
        surface.Lods = [.. LevelSegments.Select(n => new MeshLod(ChordError(n), Sphere(n)))];
        var mesh = new ArrayMesh { ResourceName = "lod_sphere" };
        mesh.AddSurface(surface);
        return mesh;
    }

    // A 6 × 6 plane subdivided 8 × 8, whose one level indexes only its four corner vertices (flat: no visible change).
    private static ArrayMesh FieldMesh()
    {
        var surface = new PlaneMesh { Size = new Vector2(6, 6), SubdivideWidth = 8, SubdivideDepth = 8 }.GetSurface(0);
        var positions = surface.Positions;
        int Corner(float x, float z)
        {
            for (var i = 0; i < positions.Length; i++)
                if (MathF.Abs(positions[i].X - x) < 1e-4f && MathF.Abs(positions[i].Z - z) < 1e-4f)
                    return i;
            throw new InvalidOperationException("corner not found");
        }

        int a = Corner(-3, -3), b = Corner(3, -3), c = Corner(3, 3), d = Corner(-3, 3);
        // Same winding as the plane's own triangles (counter-clockwise seen from +Y).
        surface.Lods = [new MeshLod(0.001f, [a, d, c, a, c, b])];
        var mesh = new ArrayMesh { ResourceName = "lod_field" };
        mesh.AddSurface(surface);
        return mesh;
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (gameTime.FrameCount != 4)
            return;
        var levels = _row.Select(n => n.RenderLod).ToArray();
        // The capture's pixel scale differs by driver (moltenvk renders at 2×), so the nearest sphere may draw 0 or 1.
        if (levels[0] > 1)
            Fail($"the nearest sphere drew level {levels[0]}");
        if (levels[^1] != 3)
            Fail($"the farthest sphere drew level {levels[^1]}, not 3");
        for (var i = 1; i < levels.Length; i++)
            if (levels[i] < levels[i - 1])
                Fail($"levels do not grow with distance: {string.Join(", ", levels)}");
        if (levels.Distinct().Count() < 3)
            Fail($"the row uses too few levels: {string.Join(", ", levels)}");
        if (_biased.RenderLod < 2)
            Fail($"the sphere with LodBias 0.02 drew level {_biased.RenderLod}");
        if (_field.RenderLod != 1)
            Fail($"the distant field drew level {_field.RenderLod}, not its index-only level 1");
        var stats = Servers.Render!.MeshStats;
        if (stats.LodSurfaceInstances < 4)
            Fail($"{stats.LodSurfaceInstances} surface instances drew a coarser level");
        if (stats.OutOfRange < 1)
            Fail("the sphere past its visibility range was drawn");
    }

    protected override void DisposeScene()
    {
    }
}
