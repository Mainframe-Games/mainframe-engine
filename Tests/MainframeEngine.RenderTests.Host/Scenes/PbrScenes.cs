using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// ADR 0150: a grid of PBR spheres under the procedural sky, lit by a sun and by the sky's image-based lighting
/// (<see cref="AmbientSource.Sky"/>, reflections from the sky). Columns sweep roughness 0 → 1, rows metallic 1 (top),
/// 0.5 and 0. <c>--count 1</c> turns the sky's sun every frame, so the sky lighting keeps re-baking (allocation gate).
/// Checks that the sky was captured.
/// </summary>
public sealed class PbrSpheresScene(HostOptions host) : RenderTestGame(host)
{
    private WorldEnvironment _environment = null!;
    private Sky _sky = null!;

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(PbrSpheresScene) };
        var camera = new Camera3D { Name = "Camera", Position = new Vector3(0, 0.6f, 8f) };
        camera.LookAt(new Vector3(0, 0, 0));
        scene.AddChild(camera);

        _sky = new Sky { Mode = SkyEnvironmentType.Procedural };
        _environment = new WorldEnvironment { Name = "Environment", Sky = _sky, AmbientSource = AmbientSource.Sky };
        scene.AddChild(_environment);

        var sun = new DirectionalLight3D { Name = "Sun", Position = new Vector3(0, 5, 0), Color = new Vector3(1f, 0.96f, 0.9f), Energy = 1.2f };
        sun.LookAt(sun.Position + Vector3.Normalize(new Vector3(-0.4f, -0.8f, -0.6f)));
        scene.AddChild(sun);

        var sphere = new SphereMesh { Radius = 0.45f, Height = 0.9f, RadialSegments = 48, Rings = 24 };
        float[] metals = [1f, 0.5f, 0f];
        for (var row = 0; row < metals.Length; row++)
        {
            for (var column = 0; column < 5; column++)
            {
                scene.AddChild(new MeshInstance3D
                {
                    Name = $"Sphere{row}{column}",
                    Position = new Vector3((column - 2) * 1.15f, (1 - row) * 1.15f, 0),
                    Mesh = sphere,
                    MaterialOverride = new StandardMaterial3D
                    {
                        ShadingMode = ShadingMode.Pbr,
                        AlbedoColor = Color.FromArgb(255, 235, 185, 120),
                        Metallic = metals[row],
                        Roughness = column / 4f,
                    },
                });
            }
        }

        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (Host.Count == 1)
        {
            var angle = gameTime.FrameCount * 0.02f;
            _sky.SunDirection = Vector3.Normalize(new Vector3(MathF.Cos(angle), 0.6f, MathF.Sin(angle)));
        }

        // The lighting is captured before the first frame's views draw.
        if (gameTime.FrameCount == 3 && _environment.Radiance is not { IsBaked: true })
            Fail("the sky was not captured for image-based lighting");

        // A moving sun re-bakes, at most every SkyRadiance.MinFramesBetweenBakes frames; a still sky bakes once.
        if (gameTime.FrameCount == 120 && _environment.Radiance is { } radiance)
        {
            var expected = Host.Count == 1 ? 120 / SkyRadiance.MinFramesBetweenBakes - 2 : 1;
            if (Host.Count == 1 ? radiance.BakeCount < expected || radiance.BakeCount > 120 / SkyRadiance.MinFramesBetweenBakes + 1
                                : radiance.BakeCount != 1)
                Fail($"{radiance.BakeCount} sky lighting bakes in 120 frames (expected about {expected})");
        }
    }

    protected override void DisposeScene()
    {
    }
}

/// <summary>
/// ADR 0150: distance and height fog with sun scatter. Pillars recede along a long floor into fog that thins with height
/// (the procedural sky fades into it at the horizon); the camera looks roughly towards the sun.
/// </summary>
public sealed class FogScene(HostOptions host) : RenderTestGame(host)
{
    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(FogScene) };
        var camera = new Camera3D { Name = "Camera", Position = new Vector3(0, 1.7f, 6f), Far = 400f };
        camera.LookAt(new Vector3(0, 1.4f, -20f));
        scene.AddChild(camera);

        scene.AddChild(new WorldEnvironment
        {
            Name = "Environment",
            Sky = new Sky { Mode = SkyEnvironmentType.Procedural, SunDirection = Vector3.Normalize(new Vector3(0.3f, 0.25f, -1f)) },
            FogEnabled = true,
            FogLightColor = new Vector3(0.62f, 0.68f, 0.74f),
            FogDensity = 0.04f,
            FogHeight = 0.5f,
            FogHeightDensity = 0.25f,
            FogSunScatter = 0.6f,
        });

        var sun = new DirectionalLight3D { Name = "Sun", Position = new Vector3(0, 5, 0), Color = new Vector3(1f, 0.92f, 0.8f), Energy = 1f };
        sun.LookAt(sun.Position - Vector3.Normalize(new Vector3(0.3f, 0.25f, -1f)));
        scene.AddChild(sun);

        scene.AddChild(new MeshInstance3D
        {
            Name = "Floor",
            Position = new Vector3(0, 0, -60),
            Mesh = new PlaneMesh { Size = new Vector2(60, 160) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 90, 110, 70), Specular = 0f },
        });

        var pillar = new BoxMesh { Size = new Vector3(0.8f, 4f, 0.8f) };
        var stone = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 170, 160, 150) };
        for (var i = 0; i < 8; i++)
        {
            foreach (var side in (ReadOnlySpan<float>)[-2.5f, 2.5f])
            {
                scene.AddChild(new MeshInstance3D
                {
                    Name = $"Pillar{i}{(side < 0 ? "L" : "R")}",
                    Position = new Vector3(side, 2f, -i * 8f),
                    Mesh = pillar,
                    MaterialOverride = stone,
                });
            }
        }

        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }
}
