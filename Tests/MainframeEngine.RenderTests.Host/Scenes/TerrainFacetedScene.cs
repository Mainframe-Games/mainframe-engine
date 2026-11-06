using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// The Faceted terrain look (G8a.4, <see cref="TerrainMaterial3D"/>): a 64 m terrain at 2 m spacing with a hill and a
/// ramp, drawn with no material (the terrain's default palette): grass, a dirt path across, a sand beach, rock on the
/// hill's crest, a lawn with two-tone bands and a cut-grass tint channel, beside a matte <see cref="StandardMaterial3D"/>
/// box that casts a sun shadow on the facets. Self-checks at frame 5: the chunks draw with a <see cref="TerrainMaterial3D"/>,
/// the painted surfaces read back, and the default palette's surface tag. With <c>--alloc</c> the camera orbits and a
/// surface cell is repainted every frame (the surface map re-uploads).
/// </summary>
public sealed class TerrainFacetedScene(HostOptions host) : RenderTestGame(host)
{
    private const uint CheckFrame = 5;
    private Camera3D _camera = null!;
    private Terrain3D _terrain = null!;
    private bool Orbit => Host.AllocationMeasuredFrames > 0;
    private readonly uint[] _cell = new uint[1];

    internal static float Height(float x, float z)
    {
        var dx = x - 40f;
        var dz = z - 24f;
        var hill = 7f * MathF.Exp(-(dx * dx + dz * dz) / 70f);
        var ramp = x < 18f ? Math.Clamp((18f - x) / 12f, 0f, 1f) * 3f : 0f; // a terrace on the left
        return hill + ramp + 0.4f * MathF.Sin(x * 0.5f) * MathF.Cos(z * 0.45f);
    }

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(TerrainFacetedScene) };
        _camera = new Camera3D { Name = "Camera", Position = new Vector3(6, 34, 50) };
        _camera.LookAt(new Vector3(0, 0, 2));
        scene.AddChild(_camera);
        scene.AddChild(new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });

        var sun = new DirectionalLight3D { Name = "Sun", Color = new Vector3(1f, 0.95f, 0.86f), Energy = 1f };
        sun.LookAt(Vector3.Normalize(new Vector3(0.6f, -0.6f, -0.45f)));
        scene.AddChild(sun);

        var data = TerrainData.Create(TerrainProfile.Faceted, 64, 2f);
        data.SetHeightsFrom(Height);
        // Surfaces (default palette): 0 grass everywhere, 3 sand along the front, 2 dirt path across, 4 rock on the crest,
        // 7 lawn (bands) on the right with channel 0 "cut" over its back half.
        var cells = data.CellsPerSide;
        var surface = new uint[cells * cells];
        var user = new uint[cells * cells];
        for (var v = 0; v < cells; v++)
            for (var u = 0; u < cells; u++)
            {
                float x = (u + 0.5f) * data.CellSize, z = (v + 0.5f) * data.CellSize;
                var id = 0u;
                if (z > 54f)
                    id = 3;
                else if (MathF.Abs(z - 0.35f * x - 18f) < 1.6f)
                    id = 2;
                else if (Height(x, z) > 5.5f)
                    id = 4;
                else if (x > 48f && z < 40f)
                    id = 7;
                surface[v * cells + u] = id;
                user[v * cells + u] = id == 7 && z < 20f ? 255u : 0u;
            }

        data.SetCells(TerrainLayers.Surface, 0, new Rect2I(0, 0, cells, cells), surface);
        data.SetCells(TerrainLayers.User, 0, new Rect2I(0, 0, cells, cells), user);
        _terrain = new Terrain3D { Name = "Terrain", Position = new Vector3(-32, 0, -32), Data = data };
        scene.AddChild(_terrain);
        // The default material's lawn bands are gated by channel 0 (cut grass), at 45°.
        var material = (TerrainMaterial3D)_terrain.RenderMaterial;
        material.TintChannel = 0;
        material.TintStrength = 0.15f;
        material.StripeAngleDegrees = 45f;
        material.StripeWidth = 1.5f;

        var box = new MeshInstance3D
        {
            Name = "Box",
            Mesh = new BoxMesh { Size = new Vector3(2.5f, 2.5f, 2.5f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 200, 196, 186), Specular = 0.05f, Shininess = 8f },
            Position = new Vector3(-8, Height(24f, 34f) + 1.25f, 2),
        };
        scene.AddChild(box);
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (Orbit)
        {
            var angle = gameTime.FrameCount % 120 / 120f * MathF.Tau;
            _camera.Position = new Vector3(44 * MathF.Sin(angle), 22, 44 * MathF.Cos(angle));
            _camera.LookAt(new Vector3(0, 2, 0));
            // A painted cell every frame: the surface map follows edits without allocating.
            _cell[0] = (uint)(gameTime.FrameCount % 8);
            _terrain.SetCells(TerrainLayers.Surface, 0, new Rect2I(1, 1, 1, 1), _cell);
            return;
        }

        if (gameTime.FrameCount != CheckFrame)
            return;
        if (_terrain.RenderMaterial is not TerrainMaterial3D)
            Fail($"a Faceted terrain without a material draws with {_terrain.RenderMaterial.GetType().Name}");
        var o = _terrain.GlobalPosition;
        if (_terrain.SurfaceAt(o.X + 20f, o.Z + 60f) != 3)
            Fail($"the beach reads surface {_terrain.SurfaceAt(o.X + 20f, o.Z + 60f)}");
        if (_terrain.SurfaceTagAt(o.X + 20f, o.Z + 60f) != "sand")
            Fail($"the beach's tag is '{_terrain.SurfaceTagAt(o.X + 20f, o.Z + 60f)}'");
        if (_terrain.SurfaceAt(o.X + 56f, o.Z + 30f) != 7)
            Fail("the lawn is not surface 7");
    }

    protected override void DisposeScene()
    {
    }
}
