using System.Globalization;
using MainframeEngine.Video.Timelines;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// Draws a title (G20.2) into an RGBA picture at the timeline's size on the CPU, with the engine's own TrueType
/// rasteriser (<see cref="Font.RenderLine"/>): a box, a shadow, an outline and the fill, line by line. The picture is
/// cached as a PNG named by the title's media hash, and both the preview and export overlay that same file, so the
/// viewer shows exactly what is written. No GPU: a headless run makes the same pixels.
/// </summary>
public static class TitleRasterizer
{
    /// <summary>The fonts a title can use: the engine's UI fonts, by file name.</summary>
    public static readonly string[] Fonts = ["LatoLatin-Bold", "LatoLatin-Regular", "LatoLatin-Italic", "RobotoMono-Regular"];

    private static readonly Dictionary<string, Font> Loaded = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    /// <summary>The PNG of <paramref name="media"/>'s title in <paramref name="directory"/>, drawn first when missing.</summary>
    public static string Ensure(MediaRef media, TitleStyle style, string directory)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(style);
        var path = Path.Combine(directory, media.Hash + ".png");
        lock (Gate)
        {
            if (File.Exists(path)) // disk-io: the editor's own cache
                return path;
            Directory.CreateDirectory(directory);
            var rgba = Render(style, media.Width, media.Height);
            var temp = path + ".tmp";
            Png.WriteRgba8(temp, media.Width, media.Height, rgba);
            File.Move(temp, path, overwrite: true);
            return path;
        }
    }

    /// <summary>The title as straight-alpha RGBA, <paramref name="width"/>×<paramref name="height"/>, transparent around it.</summary>
    public static byte[] Render(TitleStyle style, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);
        var canvas = new float[width * height * 4];
        var font = FontFor(style.Font);
        var size = Math.Clamp((int)Math.Round(style.Size), 4, 2000);
        var outline = (float)Math.Clamp(style.Outline, 0, 64);
        var outlinePad = (int)MathF.Ceiling(outline);
        var lines = (style.Text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var fills = new TextBitmap[lines.Length];
        var strokes = new TextBitmap?[lines.Length];
        var advances = new float[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            // The same padding for both, so the fill and its stroke share one layout.
            fills[i] = font.RenderLine(lines[i], size, 0, outlinePad);
            strokes[i] = outline > 0 ? font.RenderLine(lines[i], size, outline, 0) : null;
            advances[i] = font.GetStringSize(lines[i], fontSize: size).X;
        }

        var lineHeight = (int)Math.Round(font.GetHeight(size) * 1.15);
        var blockWidth = advances.Length == 0 ? 0 : (int)Math.Ceiling(advances.Max());
        var blockHeight = lineHeight * lines.Length;
        var centreX = width / 2.0 + style.X;
        var centreY = height / 2.0 + style.Y;
        var left = (int)Math.Round(centreX - blockWidth / 2.0);
        var top = (int)Math.Round(centreY - blockHeight / 2.0);

        if (style.Box && style.BoxOpacity > 0)
        {
            var pad = (int)Math.Round(Math.Max(0, style.BoxPadding));
            FillRect(canvas, width, height, left - pad, top - pad, blockWidth + 2 * pad, blockHeight + 2 * pad, Colour(style.BoxColor), Math.Clamp(style.BoxOpacity, 0, 1));
        }

        var ascentShift = (int)Math.Round((lineHeight - font.GetHeight(size)) / 2.0);
        for (var pass = 0; pass < 3; pass++) // shadow, outline, fill
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var lineLeft = style.Align switch
                {
                    TitleAlign.Left => left,
                    TitleAlign.Right => left + blockWidth - (int)Math.Round(advances[i]),
                    _ => left + (int)Math.Round((blockWidth - advances[i]) / 2),
                };
                var baseline = top + i * lineHeight + ascentShift + (int)font.GetAscent(size);
                var fill = fills[i];
                var cover = strokes[i] ?? fill;
                switch (pass)
                {
                    case 0 when style.ShadowOpacity > 0:
                        Blend(canvas, width, height, cover, lineLeft + (int)Math.Round(style.ShadowX), baseline + (int)Math.Round(style.ShadowY),
                            Colour(style.ShadowColor), Math.Clamp(style.ShadowOpacity, 0, 1));
                        break;
                    case 1 when strokes[i] is { } stroke:
                        Blend(canvas, width, height, stroke, lineLeft, baseline, Colour(style.OutlineColor), 1);
                        break;
                    case 2:
                        Blend(canvas, width, height, fill, lineLeft, baseline, Colour(style.Color), 1);
                        break;
                }
            }
        }

        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var a = canvas[i + 3];
            rgba[i + 3] = (byte)Math.Round(Math.Clamp(a, 0, 1) * 255);
            if (a <= 0)
                continue;
            rgba[i] = (byte)Math.Round(Math.Clamp(canvas[i] / a, 0, 1) * 255); // un-premultiply
            rgba[i + 1] = (byte)Math.Round(Math.Clamp(canvas[i + 1] / a, 0, 1) * 255);
            rgba[i + 2] = (byte)Math.Round(Math.Clamp(canvas[i + 2] / a, 0, 1) * 255);
        }

        return rgba;
    }

    private static Font FontFor(string name)
    {
        var key = Array.IndexOf(Fonts, name) >= 0 ? name : Fonts[0];
        lock (Gate)
        {
            if (!Loaded.TryGetValue(key, out var font))
                Loaded[key] = font = Font.FromData(ContentFiles.ReadAllBytes(ContentPaths.Resolve($"Assets/UI/fonts/{key}.ttf")));
            return font;
        }
    }

    // "#RRGGBB" (or "RRGGBB") as 0–1 channels; anything else is white.
    private static (float R, float G, float B) Colour(string? hex)
    {
        var text = (hex ?? "").TrimStart('#');
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return (1, 1, 1);
        return (((value >> 16) & 0xFF) / 255f, ((value >> 8) & 0xFF) / 255f, (value & 0xFF) / 255f);
    }

    // Premultiplied "over" of a coverage bitmap whose pen starts at (x, baseline).
    private static void Blend(float[] canvas, int width, int height, TextBitmap bitmap, int x, int baseline, (float R, float G, float B) colour, double opacity)
    {
        var x0 = x - bitmap.Origin;
        var y0 = baseline - bitmap.Baseline;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var ty = y0 + y;
            if ((uint)ty >= (uint)height)
                continue;
            for (var col = 0; col < bitmap.Width; col++)
            {
                var tx = x0 + col;
                var coverage = bitmap.Alpha[y * bitmap.Width + col];
                if ((uint)tx >= (uint)width || coverage == 0)
                    continue;
                Over(canvas, (ty * width + tx) * 4, colour, (float)(coverage / 255.0 * opacity));
            }
        }
    }

    private static void FillRect(float[] canvas, int width, int height, int x, int y, int w, int h, (float R, float G, float B) colour, double opacity)
    {
        for (var ty = Math.Max(0, y); ty < Math.Min(height, y + h); ty++)
            for (var tx = Math.Max(0, x); tx < Math.Min(width, x + w); tx++)
                Over(canvas, (ty * width + tx) * 4, colour, (float)opacity);
    }

    private static void Over(float[] canvas, int i, (float R, float G, float B) colour, float alpha)
    {
        var keep = 1 - alpha;
        canvas[i] = colour.R * alpha + canvas[i] * keep;
        canvas[i + 1] = colour.G * alpha + canvas[i + 1] * keep;
        canvas[i + 2] = colour.B * alpha + canvas[i + 2] * keep;
        canvas[i + 3] = alpha + canvas[i + 3] * keep;
    }
}
