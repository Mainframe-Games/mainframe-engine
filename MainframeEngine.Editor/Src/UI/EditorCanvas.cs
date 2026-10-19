using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>
/// Base of the editor's canvas views drawn into SubViewports (song, video). The helpers take dp and draw in
/// framebuffer pixels (<see cref="PixelScale"/>), so text stays sharp on HiDPI screens.
/// </summary>
[Tool]
public abstract class EditorCanvas : Node2D
{
    private static Font? _font;
    private static bool _fontLoaded;

    /// <summary>Framebuffer pixels per dp.</summary>
    public float PixelScale { get; set; } = 1;

    /// <summary>The canvas size in dp.</summary>
    public Vector2 ViewSize { get; set; } = new(800, 300);

    /// <summary>The editor UI font (null when the file is missing: text is skipped).</summary>
    protected static Font? TextFont
    {
        get
        {
            if (_fontLoaded)
                return _font;
            _fontLoaded = true;
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "UI", "fonts", "LatoLatin-Regular.ttf");
            if (File.Exists(path))
                _font = Font.FromData(File.ReadAllBytes(path));
            return _font;
        }
    }

    protected void Box(float x, float y, float w, float h, Vector4 color)
    {
        if (w > 0 && h > 0)
            DrawRect(new Rect2(x * PixelScale, y * PixelScale, w * PixelScale, h * PixelScale), color);
    }

    // The border as four boxes centred on the rectangle's edges, as an unfilled DrawRect covers them: its polyline path
    // allocates its strips on every call, and the song canvases outline clips and notes every frame.
    protected void Outline(float x, float y, float w, float h, Vector4 color, float width = 1)
    {
        if (w <= 0 || h <= 0)
            return;
        var half = width * 0.5f;
        if (width >= w || width >= h)
        {
            Box(x - half, y - half, w + width, h + width, color);
            return;
        }

        Box(x - half, y - half, w + width, width, color);
        Box(x - half, y + h - half, w + width, width, color);
        Box(x - half, y + half, width, h - width, color);
        Box(x + w - half, y + half, width, h - width, color);
    }

    protected void Line(float x0, float y0, float x1, float y1, Vector4 color, float width = 1) =>
        DrawLine(new Vector2(x0 * PixelScale, y0 * PixelScale), new Vector2(x1 * PixelScale, y1 * PixelScale), color, width * PixelScale);

    protected void Text(string text, float x, float baseline, float size, Vector4 color)
    {
        if (TextFont is { } font)
            DrawString(font, new Vector2(MathF.Round(x * PixelScale), MathF.Round(baseline * PixelScale)), text, HorizontalAlignment.Left, -1,
                (int)MathF.Round(size * PixelScale), color);
    }

    protected static Vector4 Rgb(int r, int g, int b, float a = 1) => new(r / 255f, g / 255f, b / 255f, a);

    protected static Vector4 WithAlpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);
}
