using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

public static class Basic2DScene
{
    // The world is much larger than the 1280x720 base canvas so zooming out to 0.5 (or a big window) never shows the clear colour.
    public static readonly Vector2 SkySize = new(4000, 2400);
    public const float HillHalfWidth = 2000f;
    public const float HillBottom = 1200f;

    public static Node Build()
    {
        var root = new Node2D { Name = "basic_2d" };
        Add(root, root, new Camera2D { Name = "Camera", Current = true });
        // The sky is a flat zenith-coloured backdrop covering the whole world; the dusk gradient is a band across the
        // original 1280x720 canvas height and a flat horizon-coloured haze continues below it. (One gradient over the
        // whole backdrop would stretch the ramp and wash out the default view.)
        var zenith = new Vector4(0.09f, 0.15f, 0.36f, 1);
        var horizon = new Vector4(0.98f, 0.55f, 0.36f, 1);
        var sky = Add(root, root, new DemoShape2D { Name = "Sky", Kind = DemoShapeKind.Rect, Size = SkySize, Color = zenith });
        Add(root, sky, new DemoShape2D { Name = "Gradient", Kind = DemoShapeKind.Rect, Size = new Vector2(SkySize.X, 900),
            Color = zenith, ColorBottom = horizon });
        Add(root, sky, new DemoShape2D { Name = "Haze", Kind = DemoShapeKind.Rect, Size = new Vector2(SkySize.X, SkySize.Y / 2 - 450),
            Position = new Vector2(0, 450 + (SkySize.Y / 2 - 450) / 2), Color = horizon });

        var sun = Add(root, root, new Node2D { Name = "Sun", Position = new Vector2(260, -120) });
        Add(root, sun, new SunRays2D { Name = "Rays" });
        Add(root, sun, new DemoShape2D { Name = "Disc", Kind = DemoShapeKind.Circle, Radius = 80f, Color = new Vector4(1f, 0.86f, 0.45f, 1) });

        Add(root, root, Hill("FarHills", 120f, 70f, 0.002f, new Vector4(0.36f, 0.24f, 0.5f, 1)));
        Add(root, root, Hill("MidHills", 200f, 60f, 0.004f, new Vector4(0.22f, 0.16f, 0.38f, 1)));
        Add(root, root, Hill("NearHills", 290f, 40f, 0.007f, new Vector4(0.1f, 0.08f, 0.2f, 1)));

        var orbit = Add(root, root, new Orbit2D { Name = "Orbit", Position = new Vector2(-150, -20) });
        for (var i = 0; i < 6; i++)
        {
            var angle = i * MathF.Tau / 6;
            Add(root, orbit, new DemoShape2D
            {
                Name = $"Moon{i}", Kind = DemoShapeKind.Circle, Radius = 14f + i * 3,
                Position = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 110f,
                Color = new Vector4(0.6f + i * 0.06f, 0.75f, 1f, 0.95f), Outline = new Vector4(1, 1, 1, 0.5f), OutlineWidth = 2f,
            });
        }

        Add(root, root, new Sprite2D { Name = "Logo", Texture = ResourceLoader.Load<Texture2D>("Content/Basic2D/logo.png"),
            Position = new Vector2(-150, -20), Scale = new Vector2(0.35f) });

        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new Basic2DPanel { Name = "Panel" });
        return root;
    }

    // A silhouette of rolling hills from x=-2000..2000 down to y=1200, well below the screen even when zoomed out.
    private static DemoShape2D Hill(string name, float baseY, float amplitude, float frequency, Vector4 color)
    {
        const int steps = 120;
        var points = new Vector2[steps + 3];
        for (var i = 0; i <= steps; i++)
        {
            var x = -HillHalfWidth + 2f * HillHalfWidth * i / steps;
            points[i] = new Vector2(x, baseY - amplitude * (MathF.Sin(x * frequency) * 0.6f + MathF.Sin(x * frequency * 2.3f + 1.3f) * 0.4f));
        }

        points[steps + 1] = new Vector2(HillHalfWidth, HillBottom);
        points[steps + 2] = new Vector2(-HillHalfWidth, HillBottom);
        return new DemoShape2D { Name = name, Kind = DemoShapeKind.Polygon, Points = points, Color = color };
    }
}
