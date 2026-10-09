using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// Light probes (ADR 0170): a roofed shed open towards the camera on a floor under the procedural sky (ambient from the
/// sky), a red wall standing in the sun to its left, and a <see cref="LightProbeVolume"/> (box, 1 m) over all of it,
/// baked when the scene loads. Linear tonemap, so captured values decode to linear radiance. Self-checked on the captured
/// frame:
/// <list type="bullet">
/// <item>the floor deep inside the shed is darker than the open floor in front of it, by the ratio the bake predicts
/// (its probes sampled on the CPU as the shaders sample them) ± 10 % (no sun: only the sky and its bounce light);</item>
/// <item>without the volume the two floor points shade alike (the darkening is the probes');</item>
/// <item>with the sun, the floor next to the sunlit red wall is redder than open floor (bounce light);</item>
/// <item>with half the sky occlusion and a green <see cref="LightProbeVolume.OcclusionTint"/>, the shed's floor again
/// matches the prediction and is greener than open floor (the blocked sky light that remains takes the tint).</item>
/// </list>
/// <c>--count</c>: 0 sun and probes (the golden; red bleed), 1 no sun (the occlusion ratio), 2 no sun and no volume,
/// 3 sun, probes, SSAO, contact shadows and volumetric fog with the camera orbiting (the allocation run), 4 no sun, half
/// the occlusion, a green tint.
/// </summary>
public sealed class ProbesScene(HostOptions host) : RenderTestGame(host)
{
    /// <summary>The inside/outside ratio's tolerance against the bake's prediction (relative).</summary>
    public const float RatioTolerance = 0.1f;

    /// <summary>Without probes, the two floor points' linear luminance may differ by this much (relative).</summary>
    public const float SameTolerance = 0.05f;

    /// <summary>How much redder (red share of r + g + b) the floor by the red wall must be than open floor.</summary>
    public const float RedBleed = 0.015f;

    /// <summary>How much greener (green share) the tinted shed's floor must be than open floor (<c>--count 4</c>).</summary>
    public const float TintShift = 0.02f;

    /// <summary>The <c>--count 4</c> volume's sky occlusion strength.</summary>
    private const float TintedOcclusion = 0.5f;

    private static readonly Color Tint = Color.FromArgb(255, 120, 220, 90);

    private static readonly Vector3 CameraPosition = new(-1.5f, 6f, 10.5f);
    private static readonly Vector3 CameraTarget = new(-1.8f, 0.6f, -2f);
    private static readonly Vector3 Inside = new(0f, 0f, -4f);
    private static readonly Vector3 Outside = new(1.2f, 0f, 3.2f);
    private static readonly Vector3 ByRedWall = new(-5.5f, 0f, 1f);
    private static readonly Vector3 OpenFloor = new(3.6f, 0f, 1.5f);
    private static readonly Vector3 FloorAlbedo = ColorSpace.SrgbToLinear(new Vector3(200, 196, 188) / 255f);

    private Camera3D _camera = null!;
    private LightProbeVolume _volume = null!;
    private WorldEnvironment _environment = null!;
    private bool _checking;

    private bool Sun => Host.Count is 0 or 3;

    private bool Probes => Host.Count != 2;

    private bool Tinted => Host.Count == 4;

    /// <summary>The bake the scene made at load (tests).</summary>
    public ProbeBakeResult? Bake { get; private set; }

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(ProbesScene) };
        _camera = new Camera3D { Name = "Camera", Position = CameraPosition };
        _camera.LookAt(CameraTarget);
        scene.AddChild(_camera);

        var plaster = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 200, 196, 188), Specular = 0f };
        var red = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 210, 40, 30), Specular = 0f };
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(40f, 40f) }, MaterialOverride = plaster });
        void Box(string name, Vector3 position, Vector3 size, Material material) =>
            scene.AddChild(new MeshInstance3D { Name = name, Position = position, Mesh = new BoxMesh { Size = size }, MaterialOverride = material });
        // The shed: x −2…2, z −5…−1, 2.4 m high, open towards +z.
        Box("ShedBack", new Vector3(0f, 1.2f, -5.1f), new Vector3(4.4f, 2.4f, 0.2f), plaster);
        Box("ShedLeft", new Vector3(-2.1f, 1.2f, -3f), new Vector3(0.2f, 2.4f, 4.4f), plaster);
        Box("ShedRight", new Vector3(2.1f, 1.2f, -3f), new Vector3(0.2f, 2.4f, 4.4f), plaster);
        Box("ShedRoof", new Vector3(0f, 2.5f, -3f), new Vector3(4.6f, 0.2f, 4.6f), plaster);
        // The red wall, its face towards +x in the sun.
        Box("RedWall", new Vector3(-6.1f, 1.5f, -0.5f), new Vector3(0.2f, 3f, 6f), red);

        if (Sun)
        {
            var sun = new DirectionalLight3D { Name = "Sun", Energy = 1.2f, Position = new Vector3(0f, 6f, 0f), ContactShadows = Host.Count == 3 };
            sun.LookAt(sun.Position + Vector3.Normalize(new Vector3(-0.6f, -0.7f, -0.3f)));
            scene.AddChild(sun);
        }

        scene.AddChild(_environment = new WorldEnvironment
        {
            Name = "Environment",
            Sky = new Sky { Mode = SkyEnvironmentType.Procedural },
            AmbientSource = AmbientSource.Sky,
            AmbientEnergy = 1.5f,
            PostProcess = new PostProcessProfile
            {
                Tonemapper = Tonemapper.Linear,
                SsaoEnabled = Host.Count == 3,
            },
            // The fog's ambient in-scatter takes the probes' open sky (ADR 0170): exercised by the allocation run.
            VolumetricFogEnabled = Host.Count == 3,
            VolumetricFogDensity = 0.02f,
            VolumetricFogLength = 30f,
            VolumetricFogAmbientInject = 0.5f,
        });

        // Probes 1 m apart from 0.25 m above the floor (none on the floor plane itself) over the shed and the red wall.
        _volume = new LightProbeVolume
        {
            Name = "Probes",
            Position = new Vector3(-1f, 3.25f, -1f),
            Size = new Vector3(16f, 6f, 16f),
            ProbeSpacing = Vector3.One,
            RaysPerProbe = 256,
            Bounces = 2,
            SkyOcclusion = Tinted ? TintedOcclusion : 1f,
            OcclusionTint = Tinted ? Tint : Color.White,
        };
        scene.AddChild(_volume);
        if (Probes)
        {
            Bake = _volume.Bake(); // before the tree has it: the bake reads the scene's nodes directly
            Log.Info($"Probes baked: {Bake.Probes} probes, {Bake.InvalidProbes} inside geometry, {Bake.Rays / 1e6:0.0}M rays, " +
                     $"{Bake.Elapsed.TotalMilliseconds:0} ms");
        }

        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (Host.Count == 3)
        {
            var angle = gameTime.FrameCount * 0.01f;
            _camera.Position = new Vector3(MathF.Sin(angle) * 3f - 1.5f, 6f, 10.5f + MathF.Cos(angle));
            _camera.LookAt(CameraTarget);
        }

        _checking = Host.Count != 3 && Array.BinarySearch(Host.CaptureFrames, gameTime.FrameCount) >= 0;
    }

    protected override void OnFrameCaptured(FrameCapture capture)
    {
        base.OnFrameCaptured(capture);
        if (!_checking)
            return;
        _checking = false;

        var inside = Linear(capture, Inside);
        var outside = Linear(capture, Outside);
        var ratio = Luminance(inside) / MathF.Max(Luminance(outside), 1e-5f);
        Log.Info($"Probes: inside {inside}, outside {outside}, ratio {ratio:F3}");
        switch (Host.Count)
        {
            case 1 or 4:
                {
                    var expected = Luminance(Predicted(Inside)) / Luminance(Predicted(Outside));
                    Log.Info($"Probes: the bake predicts the ratio {expected:F3}");
                    if (!(expected < 0.8f))
                        Fail($"The bake does not darken the shed ({expected:F3}): the scene is wrong.");
                    if (MathF.Abs(ratio - expected) > RatioTolerance * expected)
                        Fail($"Inside/outside {ratio:F3}, the bake predicts {expected:F3} (± {RatioTolerance:P0}).");
                    if (Tinted)
                    {
                        var insideGreen = inside.Y / (inside.X + inside.Y + inside.Z);
                        var outsideGreen = outside.Y / (outside.X + outside.Y + outside.Z);
                        Log.Info($"Probes: green share inside {insideGreen:F3}, outside {outsideGreen:F3}");
                        if (!(insideGreen > outsideGreen + TintShift))
                            Fail($"With a green occlusion tint the shed's floor (green share {insideGreen:F3}) is not greener than open floor ({outsideGreen:F3}).");
                    }

                    break;
                }
            case 2:
                if (MathF.Abs(ratio - 1f) > SameTolerance)
                    Fail($"Without probes the shed's floor and the open floor should shade alike (ratio {ratio:F3}).");
                break;
            case 0:
                {
                    var near = Linear(capture, ByRedWall);
                    var open = Linear(capture, OpenFloor);
                    var nearRed = near.X / (near.X + near.Y + near.Z);
                    var openRed = open.X / (open.X + open.Y + open.Z);
                    Log.Info($"Probes: floor by the red wall {near} (red share {nearRed:F3}), open floor {open} ({openRed:F3})");
                    if (!(nearRed > openRed + RedBleed))
                        Fail($"The floor by the red wall (red share {nearRed:F3}) is not redder than open floor ({openRed:F3}).");
                    if (!(ratio < 0.5f))
                        Fail($"With the sun the shed's floor ({ratio:F3} of the open floor) should be far darker.");
                    break;
                }
        }
    }

    // What the shaders compute for floor at `point` with no sun: albedo × (sky around the bent normal × the sky light +
    // bounce), the probes sampled on the CPU where the shader samples them (include/indirect.slang; the sky light is
    // probeSkyLight: the visibility plus the occluded rest the strength leaves, tinted).
    private Vector3 Predicted(Vector3 point)
    {
        var data = _volume.Data!;
        Span<float> c = stackalloc float[LightProbeData.CoefficientsPerProbe];
        var grid = data.Grid;
        var spacing = MathF.Min(grid.Spacing.X, grid.Spacing.Z);
        var v = Vector3.Normalize(_camera.GlobalPosition - point);
        data.Sample(point + Vector3.UnitY * (0.3f * spacing) + v * 0.1f, c);
        var n = Vector3.UnitY;
        var visibility = Math.Clamp(SphericalHarmonics.L1Irradiance(c[0], new Vector3(c[1], c[2], c[3]), n), 0f, 1f);
        var bentDirection = new Vector3(c[1], c[2], c[3]);
        var bent = Vector3.Normalize(n + (bentDirection.Length() > 1e-3f ? Vector3.Normalize(bentDirection) * 0.5f : Vector3.Zero));
        var sky = ProbeBakeSceneBuilder.SkyFrom(_environment, Vector3.UnitY, Vector3.Zero).Irradiance(bent);
        var skyLight = new Vector3(visibility) + (1f - visibility) * (1f - Math.Clamp(_volume.SkyOcclusion, 0f, 1f)) * _volume.OcclusionTintLinear;
        var bounce = Vector3.Max(Vector3.Zero, new Vector3(
            SphericalHarmonics.L1Irradiance(c[4], new Vector3(c[5], c[6], c[7]), n),
            SphericalHarmonics.L1Irradiance(c[8], new Vector3(c[9], c[10], c[11]), n),
            SphericalHarmonics.L1Irradiance(c[12], new Vector3(c[13], c[14], c[15]), n)));
        return FloorAlbedo * (sky * skyLight + bounce);
    }

    private static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    // The mean linear colour (sRGB decoded) of the 3 × 3 pixels around a world point.
    private Vector3 Linear(FrameCapture capture, Vector3 world)
    {
        var camera = _camera.SyncRenderCamera((float)capture.Width / capture.Height);
        var clip = Vector4.Transform(new Vector4(world, 1f), camera.ViewMatrix * camera.ProjectionMatrix);
        var ndc = new Vector2(clip.X, clip.Y) / clip.W;
        var cx = (int)((ndc.X * 0.5f + 0.5f) * capture.Width);
        var cy = (int)((0.5f - ndc.Y * 0.5f) * capture.Height);
        if (clip.W <= 0f || cx < 1 || cy < 1 || cx >= capture.Width - 1 || cy >= capture.Height - 1)
        {
            Fail($"The point {world} is off screen.");
            return Vector3.Zero;
        }

        var sum = Vector3.Zero;
        for (var y = cy - 1; y <= cy + 1; y++)
        {
            for (var x = cx - 1; x <= cx + 1; x++)
            {
                var i = (y * capture.Width + x) * 4;
                sum += ColorSpace.SrgbToLinear(new Vector3(capture.Pixels[i], capture.Pixels[i + 1], capture.Pixels[i + 2]) / 255f);
            }
        }

        return sum / 9f;
    }

    protected override void DisposeScene()
    {
    }
}
