using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// Screen-space light shafts (ADR 0160): a low sun (the physical sky's, 10° up) behind a row of tall dark posts, the
/// camera looking straight at it, so the sun sits at the centre of the image and the sky shows between the posts.
/// <c>--count</c>: 0 shafts on, 1 shafts off, 2 shafts on with a sun that swings left and right every frame, through
/// the screen and out past its edges (the allocation run).
/// </summary>
public sealed class LightShaftsScene(HostOptions host) : RenderTestGame(host)
{
    public const float SunElevation = 10f;

    /// <summary>The frame whose update checks the sun's screen position (the run must reach it).</summary>
    public const uint CheckFrame = 4;

    private DirectionalLight3D _sun = null!;

    /// <summary>World direction towards a sun at <paramref name="elevationDegrees"/>, <paramref name="azimuthDegrees"/> right of −Z.</summary>
    public static Vector3 SunDirection(float elevationDegrees, float azimuthDegrees = 0f)
    {
        var e = float.DegreesToRadians(elevationDegrees);
        var a = float.DegreesToRadians(azimuthDegrees);
        return Vector3.Normalize(new Vector3(MathF.Cos(e) * MathF.Sin(a), MathF.Sin(e), -MathF.Cos(e) * MathF.Cos(a)));
    }

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(LightShaftsScene) };
        var camera = new Camera3D { Name = "Camera", Position = new Vector3(0f, 1.7f, 8f) };
        camera.Rotation = Transform3D.BasisLookingAlong(SunDirection(SunElevation), Vector3.UnitY).GetRotation();
        scene.AddChild(camera);

        scene.AddChild(new MeshInstance3D
        {
            Name = "Floor",
            Mesh = new PlaneMesh { Size = new Vector2(400f, 400f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 110, 100, 90) },
        });

        // Posts 0.4 m wide every 1.2 m with a gap straight ahead, 14 m from the camera: the sun shows in the middle gap.
        var post = new BoxMesh { Size = new Vector3(0.4f, 12f, 0.4f) };
        var bark = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 60, 45, 35) };
        for (var i = 0; i < 10; i++)
        {
            var x = (i - 4.5f) * 1.2f;
            scene.AddChild(new MeshInstance3D { Name = $"Post{i}", Position = new Vector3(x, 6f, -6f), Mesh = post, MaterialOverride = bark });
        }

        _sun = new DirectionalLight3D { Name = "Sun", Energy = 2f };
        _sun.Rotation = Transform3D.BasisLookingAlong(-SunDirection(SunElevation), Vector3.UnitY).GetRotation();
        scene.AddChild(_sun);
        scene.AddChild(new WorldEnvironment
        {
            Name = "Environment",
            Sky = new Sky { Mode = SkyEnvironmentType.Physical },
            PostProcess = new PostProcessProfile { LightShaftsEnabled = Host.Count != 1 },
        });
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        // Self-check: the camera looks at the sun, so it is at the centre of the screen, fully faded in.
        if (gameTime.FrameCount == CheckFrame && Host.Count == 0)
        {
            var sun = Vulkan.LightShaftsSun;
            if (MathF.Abs(sun.ScreenUv.X - 0.5f) > 0.01f || MathF.Abs(sun.ScreenUv.Y - 0.5f) > 0.01f || sun.Fade < 0.999f)
                Fail($"The sun should be at the screen centre with fade 1; got {sun.ScreenUv} with fade {sun.Fade}.");
        }

        if (Host.Count != 2)
            return;
        // ±90° of azimuth over 4 s: on screen, off past the margin (no shaft passes) and back.
        var azimuth = 90f * MathF.Sin(gameTime.FrameCount * MathF.Tau / 240f);
        _sun.Rotation = Transform3D.BasisLookingAlong(-SunDirection(SunElevation, azimuth), Vector3.UnitY).GetRotation();
    }

    protected override void DisposeScene()
    {
    }
}
