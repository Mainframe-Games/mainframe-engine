using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// A Realistic <see cref="Terrain3D"/> (64 m at 0.5 m, 16 m chunks with four LOD levels and skirts): a noise hill lit
/// and shadowed by the sun under the procedural sky, seen at a low angle so the far chunks draw coarser levels.
/// Self-checks at frame 5: every chunk draws one level, some far chunks are coarser than LOD 0, and a ray down the
/// middle hits the ground at <see cref="Terrain3D.HeightAt"/>. With <c>--alloc</c> the camera orbits the hill (a
/// 120-frame loop, so every LOD switch has happened once before the measured window).
/// </summary>
public sealed class TerrainScene(HostOptions host) : RenderTestGame(host)
{
    private const uint CheckFrame = 5;
    private Camera3D _camera = null!;
    private Terrain3D _terrain = null!;
    private bool Orbit => Host.AllocationMeasuredFrames > 0;

    internal static float Hill(float x, float z)
    {
        var dx = x - 32f;
        var dz = z - 30f;
        return 9f * MathF.Exp(-(dx * dx + dz * dz) / 260f)
               + 1.4f * MathF.Sin(x * 0.23f) * MathF.Cos(z * 0.19f)
               + 0.35f * MathF.Sin(x * 0.9f + z * 0.7f);
    }

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(TerrainScene) };
        _camera = new Camera3D { Name = "Camera", Position = new Vector3(22, 19, 48) };
        _camera.LookAt(new Vector3(-2, 1, -4));
        scene.AddChild(_camera);
        scene.AddChild(new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });

        var sun = new DirectionalLight3D { Name = "Sun", Color = new Vector3(1f, 0.95f, 0.86f), Energy = 1f };
        sun.LookAt(Vector3.Normalize(new Vector3(0.8f, -0.4f, -0.35f))); // low, from the left: the hill shades its right side
        scene.AddChild(sun);

        var data = TerrainData.Create(TerrainProfile.Realistic, 64, 0.5f, 16);
        data.SetHeightsFrom(Hill);
        _terrain = new Terrain3D
        {
            Name = "Terrain",
            Position = new Vector3(-32, 0, -32),
            Data = data,
            Material = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 128, 146, 96), Specular = 0.05f, Shininess = 8f },
            LodBias = 0.25f, // coarse levels from ~20 m, so the view shows them
        };
        scene.AddChild(_terrain);
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (Orbit)
        {
            var angle = gameTime.FrameCount % 120 / 120f * MathF.Tau;
            _camera.Position = new Vector3(42 * MathF.Sin(angle), 12, 42 * MathF.Cos(angle));
            _camera.LookAt(new Vector3(0, 3, 0));
            return;
        }

        if (gameTime.FrameCount != CheckFrame)
            return;
        var chunks = _terrain.ChunksPerSide;
        var coarser = 0;
        for (var cz = 0; cz < chunks; cz++)
            for (var cx = 0; cx < chunks; cx++)
            {
                if (_terrain.GetChunkLod(cx, cz) > 0)
                    coarser++;
                var visible = 0;
                for (var l = 0; l < _terrain.LodLevels; l++)
                    visible += _terrain.GetChunkNode(cx, cz, l).Visible ? 1 : 0;
                if (visible != 1)
                    Fail($"chunk ({cx}, {cz}) shows {visible} levels");
            }

        if (_terrain.LodLevels != 4)
            Fail($"expected 4 LOD levels, got {_terrain.LodLevels}");
        if (coarser == 0)
            Fail("no chunk uses a coarser level from this view");
        if (Servers.Render is { } render && render.MeshStats.Instances != chunks * chunks)
            Fail($"expected {chunks * chunks} terrain chunk instances, got {render.MeshStats.Instances}");
        if (!_terrain.Raycast(new Vector3(1.3f, 50, 2.7f), -Vector3.UnitY, 100, out var hit) ||
            MathF.Abs(hit.Position.Y - _terrain.HeightAt(1.3f, 2.7f)) > 1e-3f)
            Fail("a vertical ray does not hit the ground at HeightAt");
    }

    protected override void DisposeScene()
    {
    }
}
