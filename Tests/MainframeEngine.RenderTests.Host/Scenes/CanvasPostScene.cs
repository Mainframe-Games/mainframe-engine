using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// The 2D canvas's post-processing (ADR 0185): a <see cref="CanvasEnvironment"/> grades the root canvas (adjustments,
/// lift/gamma/gain, tint, vignette) and adds glow around a bright disc; a <see cref="CanvasLayer"/> above the max layer
/// draws on top untouched. No grain or chromatic aberration (the golden stays deterministic). Drawn with canvas_items
/// stretch from a 480×270 base.
/// </summary>
public sealed class CanvasPostScene(HostOptions host) : RenderTestGame(host)
{
    /// <summary>The environment's settings, shared with the test's expected colours.</summary>
    public static readonly CanvasPostSettings Settings = new()
    {
        Enabled = true,
        GlowIntensity = 2.5f,
        GlowThreshold = 0.7f,
        GlowSoftKnee = 0.5f,
        GlowSpread = 0.7f,
        Brightness = 0.95f,
        Contrast = 1.1f,
        Saturation = 0.6f,
        Lift = new Vector3(0.02f, 0.03f, 0.05f),
        Gamma = new Vector3(1f, 1.05f, 1f),
        Gain = new Vector3(1.05f, 1f, 0.95f),
        Tint = new Vector4(0.9f, 1f, 0.8f, 0.5f),
        VignetteStrength = 0.6f,
        VignetteRadius = 0.35f,
        VignetteSoftness = 0.5f,
        VignetteColor = new Vector3(0.05f, 0.02f, 0.08f),
    };

    /// <summary>A flat swatch's colour (a dim red, below the glow knee).</summary>
    public static readonly Vector4 SwatchColor = new(0.7f, 0.2f, 0.15f, 1f);

    /// <summary>The HUD layer's swatch (layer 1, drawn after the post-process: exact).</summary>
    public static readonly Vector4 HudColor = new(0.2f, 0.6f, 1f, 1f);

    protected override void LoadScene()
    {
        Servers.Get<CanvasServer>()!.ClearColor = CanvasServer.GodotDefaultClearColor;
        Root.ContentScaleMode = ContentScaleMode.CanvasItems;
        Root.ContentScaleAspect = ContentScaleAspect.Expand;
        Root.ContentScaleSize = new Vector2(480, 270);

        var s = Settings;
        var root = new Node2D { Name = "Canvas" };
        root.AddChild(new CanvasEnvironment
        {
            Name = "Environment",
            GlowIntensity = s.GlowIntensity,
            GlowThreshold = s.GlowThreshold,
            GlowSoftKnee = s.GlowSoftKnee,
            GlowSpread = s.GlowSpread,
            Brightness = s.Brightness,
            Contrast = s.Contrast,
            Saturation = s.Saturation,
            Lift = s.Lift,
            Gamma = s.Gamma,
            Gain = s.Gain,
            Tint = s.Tint,
            VignetteStrength = s.VignetteStrength,
            VignetteRadius = s.VignetteRadius,
            VignetteSoftness = s.VignetteSoftness,
            VignetteColor = s.VignetteColor,
        });
        root.AddChild(new Swatch { Name = "Swatch", Position = new Vector2(40, 40), Size = new Vector2(80, 60), Color = SwatchColor });
        root.AddChild(new Swatch { Name = "Green", Position = new Vector2(150, 180), Size = new Vector2(60, 50), Color = new Vector4(0.2f, 0.7f, 0.3f, 1f) });
        root.AddChild(new Disc { Name = "Bright", Position = new Vector2(300, 135), Radius = 12, Color = new Vector4(1f, 1f, 0.9f, 1f) });
        root.AddChild(new Disc { Name = "Ember", Position = new Vector2(380, 70), Radius = 6, Color = new Vector4(1f, 0.55f, 0.2f, 1f) });

        var hud = new CanvasLayer { Name = "Hud", Layer = 1 };
        hud.AddChild(new Swatch { Name = "HudSwatch", Position = new Vector2(420, 220), Size = new Vector2(40, 40), Color = HudColor });
        root.AddChild(hud);
        Tree.ChangeScene(root);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }

    private sealed class Swatch : Node2D
    {
        public Vector2 Size { get; set; }
        public Vector4 Color { get; set; }

        protected override void OnDraw() => DrawRect(new Rect2(Vector2.Zero, Size), Color);
    }

    private sealed class Disc : Node2D
    {
        public float Radius { get; set; }
        public Vector4 Color { get; set; }

        protected override void OnDraw() => DrawCircle(Vector2.Zero, Radius, Color);
    }
}

/// <summary>
/// 2D lights beyond the old eight (ADR 0186): 40 small lights in an 8 × 5 grid under a dark <see cref="CanvasModulate"/>,
/// alternating two textures (shared slots), plus lights far off screen that are culled. Every cell is lit, the gaps are
/// not.
/// </summary>
public sealed class CanvasManyLightsScene(HostOptions host) : RenderTestGame(host)
{
    public const int Columns = 8;
    public const int Rows = 4;
    public const float Spacing = 56f;
    public static readonly Vector2 Origin = new(44, 51);

    protected override void LoadScene()
    {
        Servers.Get<CanvasServer>()!.ClearColor = new Vector4(0.5f, 0.5f, 0.5f, 1f);
        Root.ContentScaleMode = ContentScaleMode.CanvasItems;
        Root.ContentScaleAspect = ContentScaleAspect.Expand;
        Root.ContentScaleSize = new Vector2(480, 270);

        var root = new Node2D { Name = "Canvas" };
        root.AddChild(new Floor { Name = "Floor" });
        root.AddChild(new CanvasModulate { Name = "Night", Color = new Vector4(0.15f, 0.15f, 0.2f, 1f) });
        var radial = CanvasScene.Radial();
        var square = Texture2D.FromPixels(4, 4, Enumerable.Repeat((byte)255, 4 * 4 * 4).ToArray());
        // First and far off screen: culled, so they take no slot (kept, the last grid lights would not fit in the 32).
        for (var i = 0; i < 12; i++)
            root.AddChild(new PointLight2D { Name = $"Off{i}", Texture = radial, Position = new Vector2(-4000 - i * 50, 0), Energy = 2f });
        for (var i = 0; i < Columns * Rows; i++)
        {
            var hue = i / (float)(Columns * Rows);
            root.AddChild(new PointLight2D
            {
                Name = $"L{i}",
                Texture = i % 2 == 0 ? radial : square,
                TextureScale = i % 2 == 0 ? 1.2f : 5f,
                Position = Origin + new Vector2(i % Columns, i / Columns) * Spacing,
                Color = new Vector4(0.6f + 0.4f * MathF.Cos(hue * 6.28f), 0.6f + 0.4f * MathF.Cos((hue + 0.33f) * 6.28f), 0.6f + 0.4f * MathF.Cos((hue + 0.66f) * 6.28f), 1f),
                Energy = 2f,
            });
        }

        Tree.ChangeScene(root);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }

    private sealed class Floor : Node2D
    {
        protected override void OnDraw() => DrawRect(new Rect2(0, 0, 480, 270), new Vector4(0.8f, 0.8f, 0.8f, 1f));
    }
}
