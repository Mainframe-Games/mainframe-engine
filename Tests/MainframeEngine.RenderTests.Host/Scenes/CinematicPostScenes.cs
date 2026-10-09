using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// ADR 0168's scenes share a colour chart: unshaded quads that tile the view exactly (a 60° camera 1 m from the chart),
/// so patch (column, row)'s centre is at a known pixel and its HDR value is its sRGB albedo decoded to linear.
/// </summary>
public static class ColorChart
{
    public const int Columns = 6;
    public const int Rows = 4;
    public const float Fov = 60f;

    /// <summary>The patches' sRGB albedos, row by row (top row first): greys, primaries, skin and foliage tones.</summary>
    public static readonly byte[][] Albedo =
    [
        [255, 255, 255], [200, 200, 200], [128, 128, 128], [64, 64, 64], [24, 24, 24], [0, 0, 0],
        [255, 0, 0], [0, 255, 0], [0, 0, 255], [255, 255, 0], [0, 255, 255], [255, 0, 255],
        [224, 172, 140], [90, 140, 60], [40, 70, 30], [150, 110, 70], [120, 160, 210], [250, 220, 160],
        [200, 60, 30], [30, 90, 160], [100, 100, 40], [180, 40, 120], [60, 180, 150], [230, 120, 20],
    ];

    /// <summary>Patch (column, row)'s centre pixel in an image of <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static (int X, int Y) Center(int column, int row, int width, int height) =>
        ((int)((column + 0.5f) / Columns * width), (int)((row + 0.5f) / Rows * height));

    /// <summary>Adds the chart (and its camera) to <paramref name="scene"/>, filling an image of aspect <paramref name="aspect"/>.</summary>
    public static void Build(Node3D scene, float aspect)
    {
        var height = 2f * MathF.Tan(Fov * MathF.PI / 360f); // the view's height 1 m away
        var width = height * aspect;
        scene.AddChild(new Camera3D { Name = "ChartCamera", Position = new Vector3(0f, 0f, 1f), Fov = Fov, Near = 0.05f, Far = 100f, Current = true });
        var size = new Vector2(width / Columns, height / Rows);
        for (var row = 0; row < Rows; row++)
            for (var column = 0; column < Columns; column++)
            {
                var a = Albedo[row * Columns + column];
                scene.AddChild(new MeshInstance3D
                {
                    Name = $"Patch{row}_{column}",
                    Position = new Vector3(-width / 2 + (column + 0.5f) * size.X, height / 2 - (row + 0.5f) * size.Y, 0f),
                    Mesh = new QuadMesh { Size = size * 1.001f }, // a hair of overlap: no cracks between patches
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, a[0], a[1], a[2]), ShadingMode = ShadingMode.Unshaded },
                });
            }
    }
}

/// <summary>
/// The colour grade and tonemappers (ADR 0168) on the <see cref="ColorChart"/>. <c>--count</c>:
/// 0 the baseline (adjustments on with nothing to do, linear tonemapper, exposure 1: each patch shows its albedo);
/// 1 an identity 33³ LUT (parsed from <c>.cube</c> text); 2 the channel-rotation LUT <see cref="Rotate"/> (17³);
/// 3 brightness 1.1, contrast 1.2, saturation 0.5; 4 the rotation LUT at strength 0.5;
/// 10–15 tonemapper <c>count − 10</c> (<see cref="Tonemapper"/>) at exposure <see cref="TonemapExposure"/>.
/// </summary>
public sealed class PostGradeScene(HostOptions host) : RenderTestGame(host)
{
    public const float TonemapExposure = 3f;
    public const float Brightness = 1.1f, Contrast = 1.2f, Saturation = 0.5f;

    /// <summary>The known LUT: output (b, r, g) for input (r, g, b) (linear, so trilinear filtering reproduces it exactly).</summary>
    public static Vector3 Rotate(Vector3 c) => new(c.Z, c.X, c.Y);

    /// <summary>The world settings of mode <paramref name="count"/> (the test recomputes the expected colours from them).</summary>
    public static PostProcessSettings Settings(int count)
    {
        var settings = PostProcessSettings.Default with { AdjustmentEnabled = true, Tonemapper = Tonemapper.Linear };
        return count switch
        {
            1 => settings with { AdjustmentColorCorrection = CubeLut.Parse(IdentityCubeText()).ToTexture3D() },
            2 => settings with { AdjustmentColorCorrection = CubeLut.FromFunction(17, Rotate, "rotate").ToTexture3D() },
            3 => settings with { AdjustmentBrightness = Brightness, AdjustmentContrast = Contrast, AdjustmentSaturation = Saturation },
            4 => settings with { AdjustmentColorCorrection = CubeLut.FromFunction(17, Rotate, "rotate").ToTexture3D(), AdjustmentColorCorrectionStrength = 0.5f },
            >= 10 and <= 15 => PostProcessSettings.Default with { Tonemapper = (Tonemapper)(count - 10), TonemapExposure = TonemapExposure },
            _ => settings,
        };
    }

    private static string IdentityCubeText()
    {
        using var writer = new StringWriter();
        CubeLut.Identity().Write(writer);
        return writer.ToString();
    }

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(PostGradeScene) };
        ColorChart.Build(scene, AspectRatio);
        var s = Settings(Host.Count);
        if (Host.Count is >= 10 and <= 15)
            Vulkan.Exposure = TonemapExposure; // the engine curve uses the project exposure
        scene.AddChild(new WorldEnvironment
        {
            Name = "Environment",
            AmbientColor = Vector3.Zero,
            PostProcess = PostProcessProfile.FromSettings(s),
        });
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }
}

/// <summary>
/// Bokeh depth of field (ADR 0168): a camera at the origin looking down −z at a box <see cref="BoxDistance"/> m away and,
/// behind it at <see cref="WallDistance"/> m, a wall of black and white vertical stripes (each <see cref="StripeWidth"/>
/// m, about 5 pixels at 640 × 480). <c>--count</c>: 0 far blur (from 8 m, full by 12 m, amount 0.15, 32 taps); 1 no depth
/// of field; 2 near blur (sharp from 20 m on, full by 10 m, amount 0.3: the box blurs, the wall stays sharp); 3 the allocation run:
/// everything ADR 0168 adds at once (far and near blur, AgX, high-quality glow, adjustments, a LUT at strength 0.3,
/// vignette, grain, aberration, FXAA) with the camera swaying.
/// </summary>
public sealed class PostDofScene(HostOptions host) : RenderTestGame(host)
{
    public const float BoxDistance = 3f;
    public const float WallDistance = 40f;
    public const float StripeWidth = 0.5f;

    private Camera3D _camera = null!;

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(PostDofScene) };
        _camera = new Camera3D { Name = "Camera", Fov = 60f, Near = 0.1f, Far = 200f };
        scene.AddChild(_camera);
        scene.AddChild(new MeshInstance3D
        {
            Name = "Box",
            Position = new Vector3(0f, 0f, -BoxDistance),
            RotationDegrees = new Vector3(15f, 30f, 0f),
            Mesh = new BoxMesh { Size = new Vector3(1f, 1f, 1f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 220, 120, 40), ShadingMode = ShadingMode.Unshaded },
        });
        var white = new StandardMaterial3D { AlbedoColor = Color.White, ShadingMode = ShadingMode.Unshaded };
        var black = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 10, 10, 10), ShadingMode = ShadingMode.Unshaded };
        var stripe = new QuadMesh { Size = new Vector2(StripeWidth * 1.001f, 80f) };
        var count = (int)(100f / StripeWidth);
        for (var i = 0; i < count; i++)
        {
            scene.AddChild(new MeshInstance3D
            {
                Name = $"Stripe{i}",
                Position = new Vector3((i - count / 2 + 0.5f) * StripeWidth, 0f, -WallDistance),
                Mesh = stripe,
                MaterialOverride = (i & 1) == 0 ? white : black,
            });
        }

        var post = new PostProcessProfile { AdjustmentEnabled = true };
        var environment = new WorldEnvironment { Name = "Environment", AmbientColor = Vector3.Zero, PostProcess = post };
        var lens = new CameraAttributesPractical { DofBlurAmount = 0.15f, DofQuality = DepthOfFieldQuality.High };
        switch (Host.Count)
        {
            case 0:
                lens.DofBlurFarEnabled = true;
                lens.DofBlurFarDistance = 8f;
                lens.DofBlurFarTransition = 4f;
                break;
            case 2:
                lens.DofBlurNearEnabled = true;
                lens.DofBlurNearDistance = 20f;
                lens.DofBlurNearTransition = 10f;
                lens.DofBlurAmount = 0.3f; // a wide circle: still several pixels at 320 × 240 (lavapipe)
                break;
            case 3:
                lens.DofBlurFarEnabled = true;
                lens.DofBlurFarDistance = 8f;
                lens.DofBlurNearEnabled = true;
                lens.DofBlurNearDistance = 2f;
                lens.VignetteIntensity = 0.3f;
                lens.FilmGrainIntensity = 0.02f;
                lens.ChromaticAberrationIntensity = 2f;
                post.Tonemapper = Tonemapper.Agx;
                post.GlowEnabled = true;
                post.GlowQuality = GlowQuality.High;
                post.AdjustmentContrast = 1.1f;
                post.AdjustmentColorCorrection = CubeLut.FromFunction(17, static c => new Vector3(c.Z, c.X, c.Y)).ToTexture3D();
                post.AdjustmentColorCorrectionStrength = 0.3f;
                Vulkan.AntiAliasing = AntiAliasing.Fxaa;
                break;
        }

        _camera.Attributes = lens; // the camera's lens (count 1: everything off)
        scene.AddChild(environment);
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (Host.Count == 3)
            _camera.RotationDegrees = new Vector3(0f, 5f * MathF.Sin(gameTime.FrameCount * 0.05f), 0f);
    }

    protected override void DisposeScene()
    {
    }
}

/// <summary>
/// Film effects (ADR 0168) over a uniform grey: one unshaded quad (sRGB <see cref="Grey"/>) fills the view (linear
/// tonemapper, exposure 1: every pixel is <see cref="Grey"/> without effects), with black vertical bars in the right
/// quarter. <c>--count</c>: 0 nothing; 1 vignette <see cref="Vignette"/> (round); 2 grain <see cref="Grain"/>; 3 chromatic
/// aberration <see cref="Aberration"/> pixels.
/// </summary>
public sealed class PostFilmScene(HostOptions host) : RenderTestGame(host)
{
    public const byte Grey = 128;
    public const float Vignette = 0.5f;
    public const float Grain = 0.03f;
    public const float Aberration = 6f;

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(PostFilmScene) };
        scene.AddChild(new Camera3D { Name = "Camera", Position = new Vector3(0f, 0f, 1f), Fov = 60f, Near = 0.05f, Far = 100f });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Grey",
            Mesh = new QuadMesh { Size = new Vector2(8f, 8f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, Grey, Grey, Grey), ShadingMode = ShadingMode.Unshaded },
        });
        var black = new StandardMaterial3D { AlbedoColor = Color.Black, ShadingMode = ShadingMode.Unshaded };
        var bar = new QuadMesh { Size = new Vector2(0.03f, 4f) };
        for (var i = 0; i < 4; i++)
            scene.AddChild(new MeshInstance3D { Name = $"Bar{i}", Position = new Vector3(0.45f + 0.08f * i, 0f, 0.01f), Mesh = bar, MaterialOverride = black });

        var lens = new CameraAttributesPractical();
        switch (Host.Count)
        {
            case 1:
                lens.VignetteIntensity = Vignette;
                break;
            case 2:
                lens.FilmGrainIntensity = Grain;
                break;
            case 3:
                lens.ChromaticAberrationIntensity = Aberration;
                break;
        }

        scene.AddChild(new WorldEnvironment
        {
            Name = "Environment",
            AmbientColor = Vector3.Zero,
            PostProcess = new PostProcessProfile { Tonemapper = Tonemapper.Linear },
            CameraAttributes = lens,
        });
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }
}
