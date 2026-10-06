using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// The 2D canvas (ADR 0111): sprites (plain, flipped, modulated, a region), a y-sorted group whose tree order is the
/// reverse of its draw order, z-index, every draw primitive (rects filled and outlined, circles, thin and wide lines,
/// an antialiased line, a polyline, an arc, a triangulated polygon), additive blending, a <see cref="CanvasModulate"/>
/// tint on the root canvas, a <see cref="PointLight2D"/> showing through it (ADR 0117), outlined text (ADR 0118) and a <see cref="CanvasLayer"/> neither touches. Cleared to Godot's default grey; drawn with
/// Godot's canvas_items stretch from a 480×270 base.
/// </summary>
public sealed class CanvasScene(HostOptions host) : RenderTestGame(host)
{
    private static readonly Vector4 Red = new(1f, 0.2f, 0.2f, 1f);
    private static readonly Vector4 Green = new(0.2f, 0.9f, 0.3f, 1f);
    private static readonly Vector4 Blue = new(0.25f, 0.45f, 1f, 1f);
    private static readonly Vector4 Yellow = new(1f, 0.9f, 0.2f, 1f);

    /// <summary>An 8×8 checker of opaque magenta and half-transparent cyan, so filtering and alpha show.</summary>
    public static Texture2D Checker()
    {
        var pixels = new byte[8 * 8 * 4];
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                var i = (y * 8 + x) * 4;
                var odd = ((x / 2) + (y / 2)) % 2 == 1;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = odd ? ((byte)230, (byte)40, (byte)200, (byte)255) : ((byte)40, (byte)220, (byte)230, (byte)128);
            }

        return Texture2D.FromPixels(8, 8, pixels);
    }

    /// <summary>A 32×32 radial falloff, white to transparent (a <see cref="GradientTexture2D"/>): the point light's texture.</summary>
    public static Texture2D Radial() => new GradientTexture2D
    {
        Width = 32,
        Height = 32,
        Fill = GradientTexture2D.FillEnum.Radial,
        FillFrom = new Vector2(0.5f, 0.5f),
        FillTo = new Vector2(0.5f, 0f),
        Gradient = new Gradient { Offsets = [0f, 1f], Colors = [Vector4.One, new Vector4(1, 1, 1, 0)] },
    };

    protected override void LoadScene()
    {
        Servers.Get<CanvasServer>()!.ClearColor = CanvasServer.GodotDefaultClearColor;
        // Godot's canvas_items stretch from a 480×270 base: the capture (480×270 × the display scale) shows it whole.
        Root.ContentScaleMode = ContentScaleMode.CanvasItems;
        Root.ContentScaleAspect = ContentScaleAspect.Expand;
        Root.ContentScaleSize = new Vector2(480, 270);
        var texture = Checker();
        var root = new Node2D { Name = "Canvas" };

        root.AddChild(new Sprite2D { Name = "Plain", Texture = texture, Position = new Vector2(40, 40), Scale = new Vector2(6) });
        root.AddChild(new Sprite2D { Name = "Flipped", Texture = texture, Position = new Vector2(110, 40), Scale = new Vector2(6), FlipH = true, RotationDegrees = 15 });
        root.AddChild(new Sprite2D { Name = "Tinted", Texture = texture, Position = new Vector2(180, 40), Scale = new Vector2(6), Modulate = new Vector4(1f, 0.6f, 0.2f, 1f) });
        root.AddChild(new Sprite2D
        {
            Name = "Region",
            Texture = texture,
            Position = new Vector2(240, 20),
            Scale = new Vector2(10),
            Centered = false,
            RegionEnabled = true,
            RegionRect = new Rect2(2, 2, 4, 4),
            TextureFilter = CanvasTextureFilter.Nearest,
        });

        // Y-sort: added bottom-to-top, drawn top-to-bottom (the lower one in front).
        var ysort = new Node2D { Name = "YSort", YSortEnabled = true, Position = new Vector2(330, 30) };
        for (var i = 0; i < 3; i++)
            ysort.AddChild(new Swatch { Name = $"Y{i}", Position = new Vector2(i * 22, 40 - i * 18), Size = new Vector2(40, 40), Color = i switch { 0 => Red, 1 => Green, _ => Blue } });
        root.AddChild(ysort);

        // Z: the first child draws last.
        root.AddChild(new Swatch { Name = "ZTop", Position = new Vector2(420, 20), Size = new Vector2(40, 40), Color = Yellow, ZIndex = 1 });
        root.AddChild(new Swatch { Name = "ZBottom", Position = new Vector2(435, 35), Size = new Vector2(40, 40), Color = Blue });

        root.AddChild(new Primitives { Name = "Primitives", Position = new Vector2(0, 110) });
        root.AddChild(new Sprite2D
        {
            Name = "Additive",
            Texture = texture,
            Position = new Vector2(420, 170),
            Scale = new Vector2(8),
            Material = new CanvasItemMaterial { BlendMode = CanvasBlendMode.Add },
        });

        // A canvas shader (Godot shading language): vertex wobble, a uniform array, a sampler uniform, varyings.
        var wave = new ShaderMaterial { Shader = Shader.Load(ContentPaths.Resolve("Content/Shaders/wave.gdshader")) };
        wave.SetShaderParameter("tints", new[] { new Vector4(1f, 0.4f, 0.1f, 1f), new Vector4(0.1f, 0.6f, 1f, 1f) });
        wave.SetShaderParameter("mask", texture);
        root.AddChild(new Sprite2D { Name = "Shaded", Texture = texture, Position = new Vector2(60, 230), Scale = new Vector2(10, 4), Material = wave });

        // A 2D sub-viewport (transparent, 2D only) drawing SpineBoy on the canvas, shown through its texture.
        var rig = new SubViewport { Name = "Rig", Width = 96, Height = 96, Disable3D = true, TransparentBg = true };
        root.AddChild(rig);
        var spineBoy = new SpineSprite
        {
            Name = "SpineBoy",
            Position = new Vector2(48, 92),
            Scale = new Vector2(0.12f),
            UpdateMode = SpineSpriteUpdateMode.Manual,
            SkeletonDataRes = new SpineSkeletonDataResource
            {
                AtlasRes = ContentPaths.Resolve("Content/Models/Spine/SpineBoy/spineboy-pro.atlas"),
                SkeletonFileRes = ContentPaths.Resolve("Content/Models/Spine/SpineBoy/spineboy-pro.json"),
            },
        };
        rig.AddChild(spineBoy);
        spineBoy.EnsureSkeleton();
        spineBoy.AnimationState!.SetAnimation(0, "walk", true);
        spineBoy.UpdateSkeleton(0.25f);
        root.AddChild(new Sprite2D { Name = "RigView", Texture = rig.GetTexture(), Centered = false, Position = new Vector2(380, 170) });

        root.AddChild(new Caption { Name = "Caption", Position = new Vector2(160, 100) });
        root.AddChild(new CanvasModulate { Name = "Tint", Color = new Vector4(0.85f, 0.9f, 1f, 1f) });
        // An orange light over the flipped sprite: added after the tint, outside its texture nothing changes.
        root.AddChild(new PointLight2D { Name = "Light", Texture = Radial(), TextureScale = 3, Position = new Vector2(110, 40), Color = new Vector4(1f, 0.6f, 0.2f, 1f), Energy = 1.5f });

        var layer = new CanvasLayer { Name = "Hud", Layer = 1 };
        layer.AddChild(new Swatch { Name = "HudSwatch", Position = new Vector2(440, 230), Size = new Vector2(30, 30), Color = new Vector4(1, 1, 1, 1) });
        root.AddChild(layer);

        Tree.ChangeScene(root);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }

    /// <summary>A filled rectangle with its top-left corner at the origin.</summary>
    // Canvas text (ADR 0118): an outlined line in the engine's Lato, centred in 160 px.
    private sealed class Caption : Node2D
    {
        private readonly Font _font = Font.FromFile("Content/UI/fonts/LatoLatin-Regular.ttf");

        protected override void OnDraw()
        {
            DrawStringOutline(_font, Vector2.Zero, "Canvas text: AVTo", HorizontalAlignment.Center, 160, 16, 6, new Vector4(0.05f, 0.05f, 0.1f, 1));
            DrawString(_font, Vector2.Zero, "Canvas text: AVTo", HorizontalAlignment.Center, 160, 16);
        }
    }

    private sealed class Swatch : Node2D
    {
        public Vector2 Size { get; set; }
        public Vector4 Color { get; set; }

        protected override void OnDraw() => DrawRect(new Rect2(Vector2.Zero, Size), Color);
    }

    /// <summary>Every draw primitive.</summary>
    private sealed class Primitives : Node2D
    {
        protected override void OnDraw()
        {
            DrawRect(new Rect2(10, 10, 60, 40), Red);
            DrawRect(new Rect2(80, 10, 60, 40), Green, filled: false, width: 4);
            DrawRect(new Rect2(150, 10, 60, 40), Yellow, filled: false);
            DrawCircle(new Vector2(250, 30), 22, Blue);
            DrawCircle(new Vector2(310, 30), 22, Red, filled: false, width: 3);
            DrawLine(new Vector2(350, 10), new Vector2(400, 50), Yellow);
            DrawLine(new Vector2(360, 10), new Vector2(410, 50), Green, 6);
            DrawLine(new Vector2(370, 10), new Vector2(420, 50), Blue, 3, antialiased: true);
            DrawPolyline([new Vector2(10, 80), new Vector2(50, 120), new Vector2(90, 80), new Vector2(130, 120)], Yellow, 5);
            DrawArc(new Vector2(190, 100), 25, 0, MathF.PI * 1.5f, 24, Green, 4);
            DrawColoredPolygon([new Vector2(240, 70), new Vector2(300, 80), new Vector2(280, 100), new Vector2(310, 130), new Vector2(250, 120)], Blue);
            DrawSetTransform(new Vector2(350, 100), MathF.PI / 6, new Vector2(1.5f));
            DrawRect(new Rect2(-15, -15, 30, 30), Red);
        }
    }
}
