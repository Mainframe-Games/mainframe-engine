namespace MainframeEngine.Cli.ContactSheet;

/// <summary>An sRGB colour for <see cref="SheetCanvas"/> (8 bits a channel, straight alpha).</summary>
internal readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba Hex(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}

/// <summary>
/// An RGBA8 picture drawn on the CPU (contact sheets): fills, anti-aliasing-free lines, box-filtered blits of captured
/// frames and text through <see cref="Font.RenderLine"/>. Top row first, like <see cref="FrameCapture"/> and
/// <see cref="Png.WriteRgba8(string, int, int, ReadOnlySpan{byte})"/>.
/// </summary>
internal sealed class SheetCanvas
{
    public SheetCanvas(int width, int height, Rgba background)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
        Fill(0, 0, width, height, background);
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public Rgba this[int x, int y]
    {
        get
        {
            var i = (y * Width + x) * 4;
            return new Rgba(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
        }
    }

    public void Fill(int x, int y, int width, int height, Rgba colour)
    {
        var x0 = Math.Max(0, x);
        var y0 = Math.Max(0, y);
        var x1 = Math.Min(Width, x + width);
        var y1 = Math.Min(Height, y + height);
        for (var py = y0; py < y1; py++)
            for (var px = x0; px < x1; px++)
                Blend(px, py, colour, colour.A);
    }

    /// <summary>Draws a straight line <paramref name="thickness"/> pixels wide.</summary>
    public void Line(float x0, float y0, float x1, float y1, Rgba colour, int thickness = 1)
    {
        var steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(x1 - x0), MathF.Abs(y1 - y0)));
        var half = thickness / 2;
        for (var i = 0; i <= steps; i++)
        {
            var t = steps == 0 ? 0f : i / (float)steps;
            var x = (int)MathF.Round(x0 + (x1 - x0) * t);
            var y = (int)MathF.Round(y0 + (y1 - y0) * t);
            Fill(x - half, y - half, thickness, thickness, colour);
        }
    }

    public void Rectangle(int x, int y, int width, int height, Rgba colour, int thickness = 1)
    {
        Fill(x, y, width, thickness, colour);
        Fill(x, y + height - thickness, width, thickness, colour);
        Fill(x, y, thickness, height, colour);
        Fill(x + width - thickness, y, thickness, height, colour);
    }

    /// <summary>
    /// Draws <paramref name="source"/> (RGBA8, <paramref name="sourceWidth"/> × <paramref name="sourceHeight"/>) scaled to
    /// fit <paramref name="width"/> × <paramref name="height"/> at (<paramref name="x"/>, <paramref name="y"/>), each output
    /// pixel the average of the source pixels it covers (a box filter: captures rendered larger come out smooth).
    /// </summary>
    public void Blit(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int x, int y, int width, int height)
    {
        for (var py = 0; py < height; py++)
        {
            var sy0 = py * sourceHeight / height;
            var sy1 = Math.Max(sy0 + 1, (py + 1) * sourceHeight / height);
            for (var px = 0; px < width; px++)
            {
                var sx0 = px * sourceWidth / width;
                var sx1 = Math.Max(sx0 + 1, (px + 1) * sourceWidth / width);
                int r = 0, g = 0, b = 0, n = 0;
                for (var sy = sy0; sy < sy1; sy++)
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var i = (sy * sourceWidth + sx) * 4;
                        r += source[i];
                        g += source[i + 1];
                        b += source[i + 2];
                        n++;
                    }

                var tx = x + px;
                var ty = y + py;
                if ((uint)tx >= (uint)Width || (uint)ty >= (uint)Height)
                    continue;
                var o = (ty * Width + tx) * 4;
                Pixels[o] = (byte)(r / n);
                Pixels[o + 1] = (byte)(g / n);
                Pixels[o + 2] = (byte)(b / n);
                Pixels[o + 3] = 255;
            }
        }
    }

    /// <summary>Draws <paramref name="text"/> with its top-left at (<paramref name="x"/>, <paramref name="y"/>); returns its width.</summary>
    public int Text(Font font, string text, int size, int x, int y, Rgba colour)
    {
        if (text.Length == 0)
            return 0;
        var bitmap = font.RenderLine(text, size);
        for (var by = 0; by < bitmap.Height; by++)
            for (var bx = 0; bx < bitmap.Width; bx++)
            {
                var alpha = bitmap.Alpha[by * bitmap.Width + bx];
                var px = x + bx - bitmap.Origin;
                var py = y + by;
                if (alpha > 0 && (uint)px < (uint)Width && (uint)py < (uint)Height)
                    Blend(px, py, colour, (byte)(alpha * colour.A / 255));
            }

        return bitmap.Width;
    }

    private void Blend(int x, int y, Rgba colour, byte alpha)
    {
        var i = (y * Width + x) * 4;
        if (alpha == 255)
        {
            Pixels[i] = colour.R;
            Pixels[i + 1] = colour.G;
            Pixels[i + 2] = colour.B;
            Pixels[i + 3] = 255;
            return;
        }

        Pixels[i] = (byte)((colour.R * alpha + Pixels[i] * (255 - alpha)) / 255);
        Pixels[i + 1] = (byte)((colour.G * alpha + Pixels[i + 1] * (255 - alpha)) / 255);
        Pixels[i + 2] = (byte)((colour.B * alpha + Pixels[i + 2] * (255 - alpha)) / 255);
        Pixels[i + 3] = 255;
    }
}
