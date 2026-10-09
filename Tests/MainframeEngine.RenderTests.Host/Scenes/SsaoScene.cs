using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// Screen-space ambient occlusion (ADR 0165): a floor with two walls meeting in a corner behind a box and a (PBR) sphere
/// resting on it, under the procedural sky (ambient from the sky) and a sun from behind the camera, so every probe below
/// is sunlit. Diffuse Blinn-Phong surfaces without highlights: two floor (or wall) points shade alike unless something
/// occludes one. Self-checked on the captured frame: creases (the floor in front of the box's base, under the sphere's
/// overhang, along a wall's foot, a wall beside the corner) are darker than open floor and wall with SSAO on, alike with
/// it off, and floor beyond the radius from the box is not darkened (no halo).
/// <c>--count</c>: 0 SSAO on, 1 SSAO off, 2 SSAO on with the camera orbiting (the allocation run).
/// </summary>
public sealed class SsaoScene(HostOptions host) : RenderTestGame(host)
{
    /// <summary>The SSAO radius (m).</summary>
    public const float Radius = 1f;

    /// <summary>With SSAO on, a crease is at most this fraction of its open reference (display luminance).</summary>
    public const float CreaseRatio = 0.9f;

    /// <summary>Probes that must shade alike (SSAO off; open floor beyond the radius with it on), in 8-bit steps.</summary>
    public const float SameTolerance = 3f;

    private static readonly Vector3 CameraPosition = new(1.4f, 6.5f, 5f);
    private static readonly Vector3 CameraTarget = new(-0.8f, 0.3f, -1.2f);

    // A crease and an open point with the same normal and the same direct light; creases sit a few pixels (at 320 × 240)
    // off the edge, so the 3 × 3 probe stays on its surface.
    private static readonly (string Name, Vector3 Crease, Vector3 Open)[] Creases =
    [
        ("floor in front of the box", new Vector3(0.8f, 0f, 0.12f), new Vector3(2.7f, 0f, 0.12f)),
        ("floor under the sphere", new Vector3(-1.2f, 0f, 1f), new Vector3(2.7f, 0f, 1f)),
        ("floor at the back wall's foot", new Vector3(-0.4f, 0f, -2.72f), new Vector3(2.7f, 0f, -1.4f)),
        ("back wall beside the corner", new Vector3(-2.8f, 1.5f, -2.9f), new Vector3(-0.2f, 1.5f, -2.9f)),
    ];

    // Floor beyond the radius from everything (and lit): must match the open floor with SSAO on.
    private static readonly Vector3 BeyondRadius = new(0.8f, 0f, 1.3f);

    private Camera3D _camera = null!;
    private bool _checking;

    private bool SsaoOn => Host.Count != 1;

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(SsaoScene) };
        _camera = new Camera3D { Name = "Camera", Position = CameraPosition };
        _camera.LookAt(CameraTarget);
        scene.AddChild(_camera);

        var plaster = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 200, 196, 188), Specular = 0f };
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(20f, 20f) }, MaterialOverride = plaster });
        scene.AddChild(new MeshInstance3D
        {
            Name = "BackWall",
            Position = new Vector3(0f, 1.5f, -3f),
            Mesh = new BoxMesh { Size = new Vector3(8f, 3f, 0.2f) },
            MaterialOverride = plaster,
        });
        scene.AddChild(new MeshInstance3D
        {
            Name = "SideWall",
            Position = new Vector3(-3f, 1.5f, 0f),
            Mesh = new BoxMesh { Size = new Vector3(0.2f, 3f, 8f) },
            MaterialOverride = plaster,
        });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Box",
            Position = new Vector3(0.8f, 0.5f, -0.5f),
            Mesh = new BoxMesh { Size = new Vector3(1f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 170, 120, 90), Specular = 0f },
        });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Sphere",
            Position = new Vector3(-1.2f, 0.6f, 0.6f),
            Mesh = new SphereMesh { Radius = 0.6f, Height = 1.2f },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = ShadingMode.Pbr,
                AlbedoColor = Color.FromArgb(255, 90, 130, 170),
                Roughness = 0.35f,
            },
        });

        // From behind and above the camera: every probe is sunlit, shadows fall away from it.
        var sun = new DirectionalLight3D { Name = "Sun", Energy = 0.5f, Position = new Vector3(0f, 5f, 0f) };
        sun.LookAt(sun.Position + Vector3.Normalize(new Vector3(-0.3f, -0.7f, -1f)));
        scene.AddChild(sun);
        scene.AddChild(new WorldEnvironment
        {
            Name = "Environment",
            Sky = new Sky { Mode = SkyEnvironmentType.Procedural },
            AmbientSource = AmbientSource.Sky,
            AmbientEnergy = 2f,
            PostProcess = new PostProcessProfile
            {
                SsaoEnabled = SsaoOn,
                SsaoRadius = Radius,
                SsaoIntensity = 1.5f,
                SsaoPower = 1.5f,
            },
        });
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (Host.Count == 2)
        {
            var angle = gameTime.FrameCount * 0.01f;
            _camera.Position = new Vector3(MathF.Sin(angle) * 3f + 1f, 6.5f, 5f + MathF.Cos(angle));
            _camera.LookAt(CameraTarget);
        }

        _checking = Host.Count != 2 && Array.BinarySearch(Host.CaptureFrames, gameTime.FrameCount) >= 0;
    }

    protected override void OnFrameCaptured(FrameCapture capture)
    {
        base.OnFrameCaptured(capture);
        if (!_checking)
            return;
        _checking = false;

        foreach (var (name, creasePoint, openPoint) in Creases)
        {
            var crease = Luminance(capture, creasePoint);
            var open = Luminance(capture, openPoint);
            Log.Info($"SSAO probe {name}: {crease:F1} against {open:F1}");
            if (SsaoOn && !(crease < CreaseRatio * open))
                Fail($"SSAO on: the {name} ({crease:F1}) is not darker than its open reference ({open:F1}).");
            if (!SsaoOn && MathF.Abs(crease - open) > SameTolerance)
                Fail($"SSAO off: the {name} ({crease:F1}) and its open reference ({open:F1}) should shade alike.");
        }

        var beyond = Luminance(capture, BeyondRadius);
        var floor = Luminance(capture, Creases[0].Open);
        if (MathF.Abs(beyond - floor) > SameTolerance)
            Fail($"Floor beyond the radius ({beyond:F1}) differs from the open floor ({floor:F1}): a halo.");
    }

    // The mean display luminance of the 3 × 3 pixels around a world point.
    private float Luminance(FrameCapture capture, Vector3 world)
    {
        var camera = _camera.SyncRenderCamera((float)capture.Width / capture.Height);
        var clip = Vector4.Transform(new Vector4(world, 1f), camera.ViewMatrix * camera.ProjectionMatrix);
        var ndc = new Vector2(clip.X, clip.Y) / clip.W;
        var cx = (int)((ndc.X * 0.5f + 0.5f) * capture.Width);
        var cy = (int)((0.5f - ndc.Y * 0.5f) * capture.Height);
        if (clip.W <= 0f || cx < 1 || cy < 1 || cx >= capture.Width - 1 || cy >= capture.Height - 1)
        {
            Fail($"The probe {world} is off screen.");
            return 0f;
        }

        var sum = 0f;
        for (var y = cy - 1; y <= cy + 1; y++)
        {
            for (var x = cx - 1; x <= cx + 1; x++)
            {
                var i = (y * capture.Width + x) * 4;
                sum += 0.2126f * capture.Pixels[i] + 0.7152f * capture.Pixels[i + 1] + 0.0722f * capture.Pixels[i + 2];
            }
        }

        return sum / 9f;
    }

    protected override void DisposeScene()
    {
    }
}
