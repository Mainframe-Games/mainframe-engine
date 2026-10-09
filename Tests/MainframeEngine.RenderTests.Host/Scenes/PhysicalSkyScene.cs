using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// The physical sky (ADR 0154) over a grey floor with a white box, lit by one <see cref="DirectionalLight3D"/> of energy
/// 2 that is also the sky's sun. The camera looks towards the sun's azimuth, slightly up. <c>--count</c>: 0 noon (sun
/// 60° up, out of view), 1 sunset (sun 2° up, its disc in view), 2 a sun that rises a little every frame (the sky-view
/// LUT re-renders each frame) with auto exposure, glow and FXAA on: the allocation run.
/// </summary>
public sealed class PhysicalSkyScene(HostOptions host) : RenderTestGame(host)
{
    public const float NoonElevation = 60f;
    public const float SunsetElevation = 2f;

    /// <summary>The frame whose update checks the LUT render counts (the run must reach it).</summary>
    public const uint CheckFrame = 4;

    private DirectionalLight3D _sun = null!;
    private WorldEnvironment _environment = null!;

    /// <summary>World direction towards a sun at <paramref name="elevationDegrees"/>, in front of the camera (−Z), turned 10° right.</summary>
    public static Vector3 SunDirection(float elevationDegrees)
    {
        var e = float.DegreesToRadians(elevationDegrees);
        var a = float.DegreesToRadians(10f);
        return Vector3.Normalize(new Vector3(MathF.Cos(e) * MathF.Sin(a), MathF.Sin(e), -MathF.Cos(e) * MathF.Cos(a)));
    }

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(PhysicalSkyScene) };
        var camera = new Camera3D { Name = "Camera", Position = new Vector3(0f, 1.7f, 6f) };
        camera.Rotation = Transform3D.BasisLookingAlong(new Vector3(0f, 0.12f, -1f), Vector3.UnitY).GetRotation();
        scene.AddChild(camera);

        scene.AddChild(new MeshInstance3D
        {
            Name = "Floor",
            Mesh = new PlaneMesh { Size = new Vector2(400f, 400f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 128, 128, 128) },
        });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Box",
            Position = new Vector3(-0.8f, 0.5f, 0f),
            RotationDegrees = new Vector3(0f, 30f, 0f),
            Mesh = new BoxMesh(),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.White },
        });

        var elevation = Host.Count == 1 ? SunsetElevation : NoonElevation;
        _sun = new DirectionalLight3D { Name = "Sun", Energy = 2f };
        _sun.Rotation = Transform3D.BasisLookingAlong(-SunDirection(elevation), Vector3.UnitY).GetRotation();
        scene.AddChild(_sun);

        // The allocation run (2) also covers the other per-frame post paths of ADR 0154: auto exposure and FXAA.
        var everything = Host.Count == 2;
        if (everything)
            Vulkan.AntiAliasing = AntiAliasing.Fxaa;
        _environment = new WorldEnvironment
        {
            Name = "Environment",
            Sky = new Sky { Mode = SkyEnvironmentType.Physical },
            AutoExposureEnabled = everything,
            GlowEnabled = everything,
        };
        scene.AddChild(_environment);
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        // Self-check: the atmosphere LUTs render once; the sky view once for a still sun, every frame for a moving one.
        if (gameTime.FrameCount == CheckFrame && _environment.SkyEnvironment?.Luts is { } luts)
        {
            if (luts.AtmosphereBuilds != 1)
                Fail($"The transmittance and multiple-scattering LUTs rendered {luts.AtmosphereBuilds} times; expected once.");
            var expected = Host.Count == 2 ? (int)CheckFrame - 1 : 1;
            if (Host.Count == 2 ? luts.SkyViewBuilds < expected : luts.SkyViewBuilds != expected)
                Fail($"The sky-view LUT rendered {luts.SkyViewBuilds} times by frame {CheckFrame}; expected {expected}.");
        }
        else if (gameTime.FrameCount == CheckFrame)
        {
            Fail("The physical sky has no LUTs.");
        }

        if (Host.Count != 2)
            return;
        var elevation = 5f + gameTime.FrameCount % 600 * 0.1f; // the sky-view LUT re-renders every frame
        _sun.Rotation = Transform3D.BasisLookingAlong(-SunDirection(elevation), Vector3.UnitY).GetRotation();
    }

    protected override void DisposeScene()
    {
    }
}
