using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// ADR 0159: a stream carved into a Realistic <see cref="Terrain3D"/> (64 m at 0.5 m), drawn with
/// <see cref="WaterMaterial3D"/> under the procedural sky (ambient and reflections from the sky), with a pond from the
/// terrain's water layer and a pillar whose shadow falls across the stream. The sun is in front of the camera, so the
/// ripples glint. The curve is fitted to the valley (<see cref="River3D.FitToTerrain"/>), then carved. Self-checks at
/// frame 5: the channel is carved, the stream and the pond answer water queries, the pond has its surface. With
/// <c>--alloc</c> the camera orbits.
/// </summary>
public sealed class WaterScene(HostOptions host) : RenderTestGame(host)
{
    private const uint CheckFrame = 5;
    private static readonly Vector2 PondCentre = new(15f, 42f); // terrain-local
    private const float PondRadius = 4f;
    private const float PondLevel = 4.2f;

    private Camera3D _camera = null!;
    private Terrain3D _terrain = null!;
    private River3D _river = null!;
    private bool Orbit => Host.AllocationMeasuredFrames > 0;

    // The valley's centreline (terrain-local x at z).
    private static float ValleyX(float z) => 32f + 6f * MathF.Sin(z * 0.08f);

    private static float Ground(float x, float z)
    {
        var across = x - ValleyX(z);
        var h = 2.2f + 0.04f * (64f - z)                       // falls towards +z (the camera)
                - 1.4f * MathF.Exp(-across * across / 22f)       // the valley floor
                + 0.012f * across * across                       // the sides
                + 0.25f * MathF.Sin(x * 0.9f) * MathF.Cos(z * 0.7f);
        var d = Vector2.Distance(new Vector2(x, z), PondCentre);
        if (d < PondRadius)
            return PondLevel;
        if (d < PondRadius + 3f)
        {
            var t = (d - PondRadius) / 3f;
            var s = t * t * (3f - 2f * t);
            return PondLevel + 0.25f + (h - PondLevel - 0.25f) * s; // a rim 0.25 m above the water, easing to the ground
        }

        return h;
    }

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(WaterScene) };
        _camera = new Camera3D { Name = "Camera", Position = new Vector3(1f, 6f, 27f) };
        _camera.LookAt(new Vector3(-6f, 1.5f, 10f));
        scene.AddChild(_camera);

        var lightDirection = Vector3.Normalize(new Vector3(0.3f, -0.55f, 0.78f)); // from the far end: glints
        var sky = new Sky { Mode = SkyEnvironmentType.Procedural, SunDirection = -lightDirection };
        scene.AddChild(new WorldEnvironment
        {
            Name = "Environment",
            Sky = sky,
            AmbientSource = AmbientSource.Sky,
            ReflectedLightSource = ReflectedLightSource.Sky,
        });
        var sun = new DirectionalLight3D { Name = "Sun", Color = new Vector3(1f, 0.93f, 0.82f), Energy = 1.1f };
        sun.LookAt(lightDirection);
        scene.AddChild(sun);

        var data = TerrainData.Create(TerrainProfile.Realistic, 64, 0.5f, 16);
        data.CollisionMode = TerrainCollisionMode.None;
        data.SetHeightsFrom(Ground);
        data.SetWaterDepthFrom((x, z) =>
        {
            var d = Vector2.Distance(new Vector2(x, z), PondCentre);
            return d < PondRadius ? 0.05f + 0.9f * (1f - d * d / (PondRadius * PondRadius)) : 0f;
        });
        _terrain = new Terrain3D
        {
            Name = "Terrain",
            Position = new Vector3(-32, 0, -32),
            Data = data,
            Material = new StandardMaterial3D
            {
                ShadingMode = ShadingMode.Pbr,
                AlbedoColor = Color.FromArgb(255, 132, 124, 92),
                Roughness = 0.9f,
            },
        };
        scene.AddChild(_terrain);

        // The stream follows the valley from the far end (high) towards the camera (low).
        var curve = new Curve3D();
        float[] zs = [1f, 13f, 25f, 37f, 49f, 63f];
        for (var i = 0; i < zs.Length; i++)
        {
            var p = Local(zs[i]);
            var tangent = (Local(zs[Math.Min(i + 1, zs.Length - 1)]) - Local(zs[Math.Max(i - 1, 0)])) / (i is 0 or 5 ? 3f : 6f);
            curve.AddPoint(p, -tangent, tangent);
            curve.SetPointWidth(i, 2.6f + 0.4f * MathF.Sin(i * 1.3f));
            curve.SetPointDepth(i, 0.45f);
        }

        _river = new River3D { Name = "Stream", Curve = curve };
        scene.AddChild(_river);

        // A pillar on the left bank: its shadow falls across the stream towards the camera.
        var pillarZ = 30f;
        var pillarX = ValleyX(pillarZ) - 3.6f;
        scene.AddChild(new MeshInstance3D
        {
            Name = "Pillar",
            Mesh = new BoxMesh { Size = new Vector3(1.2f, 4f, 1.2f) },
            Position = new Vector3(pillarX - 32f, Ground(pillarX, pillarZ) + 1.6f, pillarZ - 32f),
            MaterialOverride = new StandardMaterial3D { ShadingMode = ShadingMode.Pbr, AlbedoColor = Color.FromArgb(255, 150, 146, 140), Roughness = 0.8f },
        });

        Tree.ChangeScene(scene);
        if (!_river.FitToTerrain(0.35f))
            Fail("the stream found no terrain to fit to");
        if (!_river.Carve())
            Fail("the stream could not carve the terrain");

        static Vector3 Local(float z) => new(ValleyX(z) - 32f, 0f, z - 32f);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (Orbit)
        {
            var angle = gameTime.FrameCount % 120 / 120f * MathF.Tau;
            _camera.Position = new Vector3(20 * MathF.Sin(angle), 9, 20 * MathF.Cos(angle));
            _camera.LookAt(new Vector3(0, 2, 0));
            return;
        }

        if (gameTime.FrameCount != CheckFrame)
            return;
        var water = Tree.Root.World3D.Water;
        var stream = new Vector3(ValleyX(32f) - 32f, 0f, 0f);
        if (string.IsNullOrEmpty(_river.CarveId))
            Fail("the stream has no carve record");
        if (!(water.WaterDepthAt(stream) > 0.3f))
            Fail($"the stream's centre has {water.WaterDepthAt(stream):0.00} m of water");
        if (!(_terrain.HeightAt(stream.X, stream.Z) < water.SurfaceHeightAt(stream) - 0.3f))
            Fail("the stream's bed is not carved below its surface");
        var pond = new Vector3(PondCentre.X - 32f, 0f, PondCentre.Y - 32f);
        if (MathF.Abs(water.SurfaceHeightAt(pond) - _terrain.Data!.QuantizeHeight(PondLevel)) > 1e-3f)
            Fail($"the pond's surface is at {water.SurfaceHeightAt(pond):0.000} m");
        if (_terrain.GetChunkWater((int)(PondCentre.X / 16f), (int)(PondCentre.Y / 16f)) is null)
            Fail("the pond's chunk has no water surface");
        if (_river.Ribbon?.MaterialOverride is not WaterMaterial3D)
            Fail("the stream does not draw with WaterMaterial3D");
    }

    protected override void DisposeScene()
    {
    }
}
