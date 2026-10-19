using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// <see cref="Sprite3D.Billboard"/> and the flips (ADR 0199): three lit, alpha-cut flag sprites stand on a plain floor
/// under a sun from behind the camera, each turned so that without billboarding the camera sees it edge-on. Left: Y-billboard; middle:
/// Y-billboard with <c>FlipH</c> (the flag points the other way); right: full billboard with <c>FlipV</c> (tilted back
/// toward the raised camera, the pennant at the bottom). Their shadows are cast from the quads as the camera sees them,
/// as in Godot. <c>--count 1</c> turns billboarding off (the edge-on comparison run).
/// </summary>
public sealed class SpriteBillboardScene(HostOptions host) : ShadowSceneBase(host)
{
    /// <summary>The flag's colour (sRGB), distinct from the floor, pole and sky.</summary>
    public static readonly System.Drawing.Color Flag = System.Drawing.Color.FromArgb(255, 235, 50, 205);

    private const int Width = 32, Height = 48;

    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0, 3f, 6.5f);
        Camera.LookAt(new Vector3(0, 0.9f, 0));
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(14, 14) }, MaterialOverride = Plain(215, 210, 200) });
        scene.AddChild(Sun(new Vector3(0.25f, -0.6f, -0.75f))); // from behind the camera: faces lit, full shadows fall behind them

        var billboards = Host.Count != 1;
        var texture = Texture2D.FromPixels(Width, Height, FlagPixels(),
            TextureImportSettings.Default with { Filter = TextureFilter.Nearest, Mipmaps = false });
        scene.AddChild(Sprite("FixedY", texture, new Vector3(-2.2f, 0, 0), 90f, billboards ? BillboardMode.FixedY : BillboardMode.Disabled));
        var flipped = Sprite("FixedYFlipH", texture, new Vector3(0, 0, 0), -90f, billboards ? BillboardMode.FixedY : BillboardMode.Disabled);
        flipped.FlipH = true;
        scene.AddChild(flipped);
        var enabled = Sprite("EnabledFlipV", texture, new Vector3(2.2f, 0, 0), 90f, billboards ? BillboardMode.Enabled : BillboardMode.Disabled);
        enabled.FlipV = true;
        scene.AddChild(enabled);
    }

    private static Sprite3D Sprite(string name, Texture2D texture, Vector3 position, float yaw, BillboardMode mode) => new()
    {
        Name = name,
        Texture = texture,
        Position = position,
        RotationDegrees = new Vector3(0, yaw, 0), // edge-on to the camera unless billboarded
        PixelSize = 0.04f,
        Offset = new Vector2(0, Height / 2f), // stands on its base
        Shaded = true,
        AlphaCut = SpriteAlphaCut.Discard,
        Billboard = mode,
    };

    // A pole on the left and a pennant pointing right from its top half; the rest transparent.
    private static byte[] FlagPixels()
    {
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var i = (y * Width + x) * 4;
                if (x is >= 2 and < 5)
                    Set(pixels, i, 110, 70, 40);
                else if (y is >= 2 and <= 22 && x >= 5 && x < 5 + (int)(26 * (1f - MathF.Abs(y - 12) / 11f)))
                    Set(pixels, i, Flag.R, Flag.G, Flag.B);
            }

        return pixels;
    }

    private static void Set(byte[] pixels, int i, byte r, byte g, byte b)
    {
        pixels[i] = r;
        pixels[i + 1] = g;
        pixels[i + 2] = b;
        pixels[i + 3] = 255;
    }
}
