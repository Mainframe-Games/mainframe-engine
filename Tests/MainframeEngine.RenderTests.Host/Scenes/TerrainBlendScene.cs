using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// ADR 0175 (RVT-lite): the terrain splat test's ground (<see cref="TerrainSplatScene"/>) with its macro texture baked
/// (<see cref="Terrain3D.MacroTextureEnabled"/>), and two rows of grey "boulders" half sunk into it, across grass, moss and
/// the dirt path: the near row with <see cref="StandardMaterial3D.TerrainBlend"/> 1 (their feet take on the ground's
/// colour, normal and roughness over 0.4 m, with a noisy edge), the far row without (a hard seam). The macro texture is
/// baked before the scene starts (<see cref="Terrain3D.RebakeMacroTexture"/>), so frame 1 already blends. Self-checks at frame 5:
/// the world's macro terrain, the macro heights against the terrain's, and the path's macro colour against the meadow's.
/// With <c>--alloc</c> the camera orbits.
/// </summary>
public sealed class TerrainBlendScene(HostOptions host) : RenderTestGame(host)
{
    private const uint CheckFrame = 5;
    private const int TextureSize = 128;
    private Camera3D _camera = null!;
    private Terrain3D _terrain = null!;
    private bool Orbit => Host.AllocationMeasuredFrames > 0;

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(TerrainBlendScene) };
        _camera = new Camera3D { Name = "Camera", Position = new Vector3(-6f, 4.5f, 18f), Fov = 50f };
        _camera.LookAt(new Vector3(2f, 1f, 6f));
        scene.AddChild(_camera);
        scene.AddChild(new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });

        var sun = new DirectionalLight3D { Name = "Sun", Color = new Vector3(1f, 0.95f, 0.86f), Energy = 1.1f, CastsShadows = true };
        sun.LookAt(Vector3.Normalize(new Vector3(0.6f, -0.6f, -0.5f)));
        scene.AddChild(sun);

        var data = TerrainData.Create(TerrainProfile.Realistic, 64, 0.5f, 16);
        data.SetHeightsFrom(TerrainSplatScene.Height);
        data.SetWeightsFrom(TerrainSplatScene.Weights);
        _terrain = new Terrain3D
        {
            Name = "Terrain",
            Position = new Vector3(-32, 0, -32),
            Data = data,
            Material = new TerrainSplatMaterial3D
            {
                LayerTextureSize = TextureSize,
                Layers =
                [
                    Layer("grass", LayerTextures.Kind.Grass, 3f, 0.25f),
                    Layer("dirt", LayerTextures.Kind.Dirt, 2.5f, 0.15f),
                    Layer("rock", LayerTextures.Kind.Rock, 6f, 0.2f),
                    Layer("moss", LayerTextures.Kind.Moss, 3.5f, 0.3f),
                ],
            },
            MacroTextureEnabled = true,
            MacroTextureResolution = 512, // 12.5 cm over 64 m, as the Forest's 2048² over 256 m
        };
        scene.AddChild(_terrain);
        _terrain.RebakeMacroTexture(); // now, not on the worker thread: the captured frames are deterministic

        // Two rows of flattened spheres sunk 0.35 m: blended (near, z = 10) and plain (far, z = 2).
        var blended = Rock(1f);
        var plain = Rock(0f);
        for (var i = 0; i < 5; i++)
        {
            var x = -6f + i * 3.2f;
            scene.AddChild(Boulder($"Blended{i}", x, 10f, blended));
            scene.AddChild(Boulder($"Plain{i}", x, 2f, plain));
        }

        Tree.ChangeScene(scene);
    }

    private static StandardMaterial3D Rock(float blend) => new()
    {
        ShadingMode = ShadingMode.Pbr,
        AlbedoColor = Color.FromArgb(255, 150, 148, 142),
        Roughness = 0.55f,
        TerrainBlend = blend,
        TerrainBlendHeight = 0.4f,
    };

    private static MeshInstance3D Boulder(string name, float x, float z, StandardMaterial3D material)
    {
        var ground = TerrainSplatScene.Height(x + 32f, z + 32f);
        return new MeshInstance3D
        {
            Name = name,
            Mesh = new SphereMesh { Radius = 1f, Height = 2f },
            MaterialOverride = material,
            Position = new Vector3(x, ground + 0.4f, z),
            Scale = new Vector3(1.1f, 0.75f, 0.9f),
        };
    }

    private static TerrainLayer Layer(string tag, LayerTextures.Kind kind, float tiling, float contrast)
    {
        var (albedo, normal, orm) = LayerTextures.Generate(kind, TextureSize);
        return new TerrainLayer { Name = tag, Tag = tag, Albedo = albedo, Normal = normal, Orm = orm, TilingMeters = tiling, HeightBlendContrast = contrast };
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (Orbit)
        {
            var angle = gameTime.FrameCount % 120 / 120f * MathF.Tau;
            _camera.Position = new Vector3(18 * MathF.Sin(angle), 6, 6 + 18 * MathF.Cos(angle));
            _camera.LookAt(new Vector3(0, 1, 6));
            return;
        }

        if (gameTime.FrameCount != CheckFrame)
            return;
        if (_terrain.MacroTexture is not { } macro)
        {
            Fail("the terrain baked no macro texture");
            return;
        }

        if (!ReferenceEquals(_terrain.GetWorld3D()?.MacroTerrain, _terrain))
            Fail("the world's macro terrain is not the terrain");
        foreach (var (x, z) in new[] { (10f, 12f), (31.3f, 40.2f), (50f, 20f) })
        {
            var expected = _terrain.HeightAt(x - 32f, z - 32f);
            var actual = macro.HeightAt(x, z);
            if (MathF.Abs(actual - expected) > 0.05f)
                Fail($"macro height at ({x}, {z}) is {actual:0.000} m, the terrain's {expected:0.000} m");
        }

        // The dirt path (brown) and the meadow (green) differ in the macro texture as they do on the ground.
        var path = macro.AlbedoAt(20f, 42f + 5f * MathF.Sin(20f * 0.12f));
        var meadow = macro.AlbedoAt(8f, 8f);
        if (!(path.X > path.Y && meadow.Y > meadow.X))
            Fail($"macro colours: path {path}, meadow {meadow}");
    }

    protected override void DisposeScene()
    {
    }
}
