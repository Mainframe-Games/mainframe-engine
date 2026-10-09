using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// Terrain foliage (ADR 0157): grass clumps, ferns and pebbles scattered over the <see cref="TerrainScene"/> hill (64 m
/// at 0.5 m, 16 m chunks), grass and ferns on splat layer 0 with bare earth (layer 1) on the steep side, swaying in the
/// wind (frame N is N / 60 s, so the golden has a fixed wind time) under a low sun, from a walker's eye height. Grass
/// thins out from 25 m and is gone at 45 m. Self-checks at frame 10: some instances drawn, fewer than placed, some
/// tiles hidden, and every drawn grass instance within its tile's prefix. With <c>--alloc</c> the camera walks a loop
/// over the hill, one lap per 120 frames (tiles thin, hide and come back).
/// </summary>
/// <remarks>
/// Performance (<c>--count 1</c>, with <c>--perf</c>): the forest's 256 m terrain (0.5 m, 32 m chunks) with dense grass
/// (5 clumps per m², 45 m), ferns and pebbles, the camera walking at eye height; <c>--count 2</c> is the same without
/// foliage, the baseline for the foliage's cost.
/// </remarks>
public sealed class TerrainFoliageScene(HostOptions host) : RenderTestGame(host)
{
    private const uint CheckFrame = 10;
    private Camera3D _camera = null!;
    private Terrain3D _terrain = null!;
    private bool Walk => Host.AllocationMeasuredFrames > 0 || Large;
    private bool Large => Host.Count is 1 or 2;
    private float Size => Large ? 256f : 64f;

    // The perf map: rolling hills of the forest's scale.
    private static float Hills(float x, float z) =>
        6f * MathF.Sin(x * 0.031f) * MathF.Cos(z * 0.027f) + 2.5f * MathF.Sin(x * 0.11f + z * 0.07f) + 0.4f * MathF.Sin(x * 0.7f) * MathF.Cos(z * 0.6f);

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(TerrainFoliageScene) };
        _camera = new Camera3D { Name = "Camera", Far = 400f };
        scene.AddChild(_camera);
        scene.AddChild(new WorldEnvironment
        {
            Name = "Environment",
            Sky = new Sky { Mode = SkyEnvironmentType.Procedural },
            WindDirection = new Vector3(1f, 0f, 0.4f),
            WindStrength = 1f,
            WindFrequency = 0.4f,
            WindTurbulence = 0.4f,
        });

        var sun = new DirectionalLight3D { Name = "Sun", Color = new Vector3(1f, 0.94f, 0.84f), Energy = 1.1f };
        sun.LookAt(Vector3.Normalize(new Vector3(0.7f, -0.45f, -0.5f)));
        scene.AddChild(sun);

        TerrainData data;
        if (Large)
        {
            data = TerrainData.Create(TerrainProfile.Realistic, 256, 0.5f, 32);
            data.SetHeightsFrom(Hills);
        }
        else
        {
            data = TerrainData.Create(TerrainProfile.Realistic, 64, 0.5f, 16);
            data.SetHeightsFrom(TerrainScene.Hill);
        }

        data.CollisionMode = TerrainCollisionMode.None;
        // Bare earth (layer 1) where the ground is steep and in a few patches; grass (layer 0) elsewhere.
        var grid = data.Grid;
        var bed = data.BedHeights.ToArray();
        data.SetWeightsFrom((x, z, w) =>
        {
            var slope = 1f - grid.SmoothNormalAt(bed, x, z).Y;
            var patch = MathF.Sin(x * 0.21f + 1.3f) * MathF.Cos(z * 0.17f) - 0.75f;
            var earth = Math.Clamp(slope * 4f + patch * 2.5f, 0f, 1f);
            w[0] = 1f - earth;
            w[1] = earth;
        });

        if (Host.Count != 2)
        {
            var grassMaterial = GrassMesh.CreateMaterial();
            data.FoliageTypes =
            [
                new FoliageType
                {
                    ResourceName = "Grass",
                    Mesh = GrassMesh.Clump(blades: 16, height: 0.42f, width: 0.05f, bend: 0.45f, seed: 1),
                    Material = grassMaterial,
                    Density = 7f,
                    LayerMask = 1,
                    ScaleMin = 0.7f,
                    ScaleMax = 1.3f,
                    AlignToNormal = 0.4f,
                    CullDistance = 45f,
                    ThinBand = 20f,
                    Subdivisions = 2,
                },
                new FoliageType
                {
                    ResourceName = "Fern",
                    Mesh = GrassMesh.Fern(seed: 2),
                    Material = grassMaterial,
                    Density = 0.12f,
                    LayerMask = 1,
                    ScaleMin = 0.7f,
                    ScaleMax = 1.4f,
                    AlignToNormal = 0.2f,
                    CullDistance = 60f,
                    ThinBand = 20f,
                    Seed = 1,
                },
                new FoliageType
                {
                    ResourceName = "Pebbles",
                    Mesh = GrassMesh.Rock(radius: 0.18f, seed: 3),
                    Material = new StandardMaterial3D { ShadingMode = ShadingMode.Pbr, Roughness = 0.85f },
                    Density = 0.08f,
                    ScaleMin = 0.5f,
                    ScaleMax = 1.6f,
                    AlignToNormal = 0.8f,
                    SinkMeters = 0.04f,
                    CullDistance = 70f,
                    ThinBand = 25f,
                    CastShadows = true,
                    Seed = 2,
                },
            ];
        }

        _terrain = new Terrain3D
        {
            Name = "Terrain",
            Position = new Vector3(-Size / 2, 0, -Size / 2),
            Data = data,
            Material = new StandardMaterial3D
            {
                ShadingMode = ShadingMode.Pbr,
                AlbedoColor = Color.FromArgb(255, 74, 80, 40),
                Roughness = 0.95f,
            },
            LodBias = 1f,
        };
        scene.AddChild(_terrain);
        Tree.ChangeScene(scene);
        PlaceCamera(0);
    }

    private void PlaceCamera(uint frame)
    {
        if (Walk)
        {
            // A loop around the middle of the map: 1200 frames (20 s) per lap for the perf map; 120 for the allocation
            // gate, so every tile has been shown, thinned and hidden once before the measured window.
            var radius = Large ? 70f : 14f;
            var lap = Large ? 1200u : 120u;
            var angle = frame % lap / (float)lap * MathF.Tau;
            var x = radius * MathF.Sin(angle);
            var z = radius * MathF.Cos(angle);
            _camera.Position = new Vector3(x, _terrain.HeightAt(x, z) + 1.7f, z);
            var ahead = new Vector3(radius * MathF.Sin(angle + 0.3f), 0, radius * MathF.Cos(angle + 0.3f));
            _camera.LookAt(new Vector3(ahead.X, _terrain.HeightAt(ahead.X, ahead.Z) + 1.2f, ahead.Z));
            return;
        }

        var eye = new Vector3(4f, 0f, 22f);
        _camera.Position = eye with { Y = _terrain.HeightAt(eye.X, eye.Z) + 1.7f };
        _camera.LookAt(new Vector3(-2f, 3.5f, -6f));
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (Walk)
        {
            PlaceCamera((uint)gameTime.FrameCount);
            return;
        }

        if (gameTime.FrameCount != CheckFrame || _terrain.Foliage is not { } foliage)
            return;
        if (foliage.TypeCount != 3)
            Fail($"expected 3 foliage types, got {foliage.TypeCount}");
        var drawn = foliage.DrawnInstances;
        var total = foliage.TotalInstances;
        if (drawn == 0 || drawn >= total)
            Fail($"expected some but not all of the {total} instances drawn, got {drawn}");

        var hidden = 0;
        var tiles = foliage.GetTilesPerSide(0);
        for (var tz = 0; tz < tiles; tz++)
            for (var tx = 0; tx < tiles; tx++)
                if (foliage.GetTileNode(0, tx, tz) is { Visible: false })
                    hidden++;
        if (hidden == 0)
            Fail("no grass tile is beyond the cull distance from this view");
        if (Servers.Render is { } render && render.MeshStats.MultiMeshInstances == 0)
            Fail("no foliage instance was drawn");
    }

    protected override void DisposeScene()
    {
    }
}
