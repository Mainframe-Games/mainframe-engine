using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Glyph coverage (ADR 0118): contours flattened to lines (quadratics subdivided by length) and rasterised with exact
/// signed-area accumulation (font-rs's algorithm), filled non-zero; an outline is the glyph grown by a disc, rasterised
/// at 4× and box-filtered (Godot strokes with FreeType's stroker at <c>outline_size / 4</c> px radius with round joins
/// and draws the text over it, so the stroke's inner half never shows).
/// </summary>
internal static class GlyphRasterizer
{
    /// <summary>A rasterised glyph: alpha bytes (row-major, Y down) and the bitmap's offset from the pen on the baseline.</summary>
    public readonly record struct Bitmap(byte[] Alpha, int Width, int Height, int Left, int Top);

    /// <summary>
    /// Rasterises <paramref name="contours"/> (font units, Y up) at <paramref name="scale"/> pixels per unit, padded by
    /// <paramref name="outlineRadius"/> and dilated by it when it is positive.
    /// </summary>
    public static Bitmap Rasterize(List<GlyphContour> contours, float scale, float outlineRadius)
    {
        if (contours.Count == 0)
            return new Bitmap([], 0, 0, 0, 0);
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var contour in contours)
            foreach (var p in contour.Points)
            {
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }

        var pad = 1 + (int)MathF.Ceiling(outlineRadius);
        var left = (int)MathF.Floor(min.X * scale) - pad;
        var top = (int)MathF.Ceiling(max.Y * scale) + pad; // pixels above the baseline
        var width = (int)MathF.Ceiling(max.X * scale) + pad - left;
        var height = top - ((int)MathF.Floor(min.Y * scale) - pad);
        if (width <= 0 || height <= 0)
            return new Bitmap([], 0, 0, 0, 0);

        byte[] alpha;
        if (outlineRadius > 0)
        {
            const int Ss = 4;
            var hi = Coverage(contours, scale * Ss, new Vector2(left, top) * Ss, width * Ss, height * Ss);
            alpha = GrowAndReduce(hi, width, height, Ss, outlineRadius * Ss);
        }
        else
        {
            alpha = Coverage(contours, scale, new Vector2(left, top), width, height);
        }

        return new Bitmap(alpha, width, height, left, top);
    }

    // Coverage bytes of the contours in a width×height bitmap whose top-left is `origin` (pixels, Y up from the baseline).
    private static byte[] Coverage(List<GlyphContour> contours, float scale, Vector2 origin, int width, int height)
    {
        var acc = new float[width * height + 4];
        Vector2 ToPixel(Vector2 p) => new(p.X * scale - origin.X, origin.Y - p.Y * scale);

        foreach (var contour in contours)
        {
            var pts = contour.Points;
            var on = contour.OnCurve;
            var n = pts.Length;
            if (n < 2)
                continue;
            // Start on an on-curve point (or the midpoint of two off-curve ones).
            var first = Array.IndexOf(on, true);
            Vector2 start;
            int begin;
            if (first < 0)
            {
                start = (pts[0] + pts[1]) / 2;
                begin = 1;
            }
            else
            {
                start = pts[first];
                begin = first;
            }

            var pen = ToPixel(start);
            var startPixel = pen;
            Vector2? control = null;
            for (var k = 1; k <= n; k++)
            {
                var i = (begin + k) % n;
                var p = ToPixel(pts[i]);
                if (on[i])
                {
                    if (control is { } c)
                        Quad(acc, width, height, pen, c, p);
                    else
                        Line(acc, width, height, pen, p);
                    pen = p;
                    control = null;
                }
                else if (control is { } c)
                {
                    var mid = (c + p) / 2;
                    Quad(acc, width, height, pen, c, mid);
                    pen = mid;
                    control = p;
                }
                else
                {
                    control = p;
                }
            }

            if (control is { } last)
                Quad(acc, width, height, pen, last, startPixel);
            else if (pen != startPixel)
                Line(acc, width, height, pen, startPixel);
        }

        var alpha = new byte[width * height];
        var sum = 0f;
        for (var i = 0; i < alpha.Length; i++)
        {
            sum += acc[i];
            alpha[i] = (byte)(Math.Min(MathF.Abs(sum), 1f) * 255f + 0.5f);
        }

        return alpha;
    }

    // The supersampled glyph (inside = coverage ≥ ½) grown by a disc of `radius` samples, then each ss×ss block averaged.
    private static byte[] GrowAndReduce(byte[] hi, int width, int height, int ss, float radius)
    {
        int hw = width * ss, hh = height * ss;
        var r = (int)MathF.Ceiling(radius);
        var disc = new List<(int, int)>();
        for (var dy = -r; dy <= r; dy++)
            for (var dx = -r; dx <= r; dx++)
                if (dx * dx + dy * dy <= radius * radius)
                    disc.Add((dx, dy));
        var grown = new bool[hi.Length];
        for (var y = 0; y < hh; y++)
            for (var x = 0; x < hw; x++)
            {
                if (hi[y * hw + x] < 128)
                    continue;
                // Interior samples whose four neighbours are inside add nothing a neighbour's disc does not.
                if (x > 0 && y > 0 && x < hw - 1 && y < hh - 1 && hi[y * hw + x - 1] >= 128 && hi[y * hw + x + 1] >= 128 &&
                    hi[(y - 1) * hw + x] >= 128 && hi[(y + 1) * hw + x] >= 128)
                {
                    grown[y * hw + x] = true;
                    continue;
                }

                foreach (var (dx, dy) in disc)
                {
                    int gx = x + dx, gy = y + dy;
                    if ((uint)gx < (uint)hw && (uint)gy < (uint)hh)
                        grown[gy * hw + gx] = true;
                }
            }

        var alpha = new byte[width * height];
        var full = ss * ss;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var count = 0;
                for (var sy = 0; sy < ss; sy++)
                    for (var sx = 0; sx < ss; sx++)
                        if (grown[(y * ss + sy) * hw + x * ss + sx])
                            count++;
                alpha[y * width + x] = (byte)((count * 255 + full / 2) / full);
            }

        return alpha;
    }

    private static void Quad(float[] acc, int w, int h, Vector2 p0, Vector2 p1, Vector2 p2)
    {
        var devx = p0.X - 2 * p1.X + p2.X;
        var devy = p0.Y - 2 * p1.Y + p2.Y;
        var devsq = devx * devx + devy * devy;
        if (devsq < 0.333f)
        {
            Line(acc, w, h, p0, p2);
            return;
        }

        const float Tolerance = 3f;
        var steps = 1 + (int)MathF.Floor(MathF.Sqrt(MathF.Sqrt(Tolerance * devsq)));
        var prev = p0;
        for (var i = 1; i <= steps; i++)
        {
            var t = (float)i / steps;
            var mt = 1 - t;
            var p = mt * mt * p0 + 2 * mt * t * p1 + t * t * p2;
            Line(acc, w, h, prev, p);
            prev = p;
        }
    }

    // font-rs's draw_line: each row the segment crosses gets its exact signed area split over the pixels it spans.
    private static void Line(float[] a, int w, int h, Vector2 p0, Vector2 p1)
    {
        if (MathF.Abs(p0.Y - p1.Y) <= float.Epsilon)
            return;
        float dir;
        if (p0.Y < p1.Y)
            dir = 1;
        else
        {
            dir = -1;
            (p0, p1) = (p1, p0);
        }

        var dxdy = (p1.X - p0.X) / (p1.Y - p0.Y);
        var x = p0.X;
        var y0 = (int)MathF.Max(0, p0.Y);
        if (p0.Y < 0)
            x -= p0.Y * dxdy;
        var yEnd = Math.Min(h, (int)MathF.Ceiling(p1.Y));
        for (var y = y0; y < yEnd; y++)
        {
            var lineStart = y * w;
            var dy = MathF.Min(y + 1, p1.Y) - MathF.Max(y, p0.Y);
            var xnext = x + dxdy * dy;
            var d = dy * dir;
            var (x0, x1) = x < xnext ? (x, xnext) : (xnext, x);
            var x0Floor = MathF.Floor(x0);
            var x0i = (int)x0Floor;
            var x1Ceil = MathF.Ceiling(x1);
            var x1i = (int)x1Ceil;
            var at = lineStart + x0i;
            if (at < 0 || lineStart + x1i + 1 >= a.Length)
            {
                x = xnext;
                continue;
            }

            if (x1i <= x0i + 1)
            {
                var xmf = 0.5f * (x + xnext) - x0Floor;
                a[at] += d - d * xmf;
                a[at + 1] += d * xmf;
            }
            else
            {
                var s = 1f / (x1 - x0);
                var x0f = x0 - x0Floor;
                var a0 = 0.5f * s * (1 - x0f) * (1 - x0f);
                var x1f = x1 - x1Ceil + 1;
                var am = 0.5f * s * x1f * x1f;
                a[at] += d * a0;
                if (x1i == x0i + 2)
                {
                    a[at + 1] += d * (1 - a0 - am);
                }
                else
                {
                    var a1 = s * (1.5f - x0f);
                    a[at + 1] += d * (a1 - a0);
                    for (var xi = x0i + 2; xi < x1i - 1; xi++)
                        a[lineStart + xi] += d * s;
                    var a2 = a1 + (x1i - x0i - 3) * s;
                    a[lineStart + x1i - 1] += d * (1 - a2 - am);
                }

                a[lineStart + x1i] += d * am;
            }

            x = xnext;
        }
    }
}
