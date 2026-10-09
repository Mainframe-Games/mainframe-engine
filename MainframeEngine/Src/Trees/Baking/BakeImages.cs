using System.Numerics;

namespace MainframeEngine.Trees;

/// <summary>Encoding and post-processing of resolved bakes into RGBA8 atlases (ADR 0172).</summary>
internal static class BakeImages
{
    /// <summary>
    /// Writes a resolved cell into an atlas at (<paramref name="x0"/>, <paramref name="y0"/>): albedo (sRGB, alpha =
    /// coverage), normal (view space × 0.5 + 0.5, alpha = depth mapped by <paramref name="depthToByte"/> when given, else
    /// 255) and, when <paramref name="thickness"/> is given, thickness (R: 1 − 0.5^(layers − 1)) and ambient occlusion (G).
    /// </summary>
    public static void WriteCell(BakePixel[] pixels, float[]? ao, int width, int height, byte[] albedo, byte[] normal, byte[]? thickness,
        int atlasWidth, int x0, int y0, Func<float, byte>? depthToByte = null)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = pixels[y * width + x];
                var o = ((y0 + y) * atlasWidth + x0 + x) * 4;
                var srgb = ColorSpace.LinearToSrgb(Vector3.Clamp(p.Albedo, Vector3.Zero, Vector3.One));
                albedo[o] = ToByte(srgb.X);
                albedo[o + 1] = ToByte(srgb.Y);
                albedo[o + 2] = ToByte(srgb.Z);
                albedo[o + 3] = ToByte(p.Coverage);
                var n = p.Coverage > 0f ? p.Normal : Vector3.UnitZ;
                normal[o] = ToByte(n.X * 0.5f + 0.5f);
                normal[o + 1] = ToByte(n.Y * 0.5f + 0.5f);
                normal[o + 2] = ToByte(n.Z * 0.5f + 0.5f);
                normal[o + 3] = p.Coverage > 0f && depthToByte is not null ? depthToByte(p.Depth) : (byte)255;
                if (thickness is null)
                    continue;
                var t = p.Coverage > 0f ? 1f - MathF.Pow(0.5f, MathF.Max(p.Layers - 1f, 0f)) : 0f;
                thickness[o] = ToByte(t);
                thickness[o + 1] = ToByte(ao is null || p.Coverage <= 0f ? 1f : ao[y * width + x]);
                thickness[o + 2] = 0;
                thickness[o + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Ambient occlusion from the depth seen by the camera (ADR 0172's "depth-based self-occlusion"): the share of
    /// neighbours within <paramref name="radius"/> pixels standing in front of a pixel by more than
    /// <paramref name="bias"/> (depth units) darkens it by up to <paramref name="strength"/>. 1 where nothing is covered.
    /// </summary>
    public static float[] DepthOcclusion(BakePixel[] pixels, int width, int height, float radius, float bias, float strength)
    {
        var ao = new float[pixels.Length];
        Span<(int Dx, int Dy)> taps = stackalloc (int, int)[24];
        var count = 0;
        for (var ring = 1; ring <= 3; ring++)
        {
            var r = radius * ring / 3f;
            for (var k = 0; k < 8; k++)
            {
                var a = (k + 0.5f * (ring & 1)) * MathF.Tau / 8f;
                taps[count++] = ((int)MathF.Round(MathF.Cos(a) * r), (int)MathF.Round(MathF.Sin(a) * r));
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var p = pixels[i];
                if (p.Coverage <= 0f)
                {
                    ao[i] = 1f;
                    continue;
                }

                var occluded = 0;
                foreach (var (dx, dy) in taps)
                {
                    int sx = x + dx, sy = y + dy;
                    if ((uint)sx >= (uint)width || (uint)sy >= (uint)height)
                        continue;
                    var q = pixels[sy * width + sx];
                    if (q.Coverage > 0f && q.Depth < p.Depth - bias)
                        occluded++;
                }

                ao[i] = 1f - strength * occluded / count;
            }
        }

        return ao;
    }

    /// <summary>
    /// Fills every uncovered texel (alpha 0 in <paramref name="coverage"/>) of every map's RGB inside the rectangle with
    /// its nearest covered texel's (a two-pass chamfer propagation of the nearest seed), so filtering and mips never bleed
    /// black into the edges. Alpha is untouched; a rectangle without coverage is left as it is.
    /// </summary>
    public static void Dilate(byte[] coverage, byte[][] maps, int atlasWidth, int x0, int y0, int width, int height)
    {
        var seed = new int[width * height];
        var distance = new float[width * height];
        var any = false;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var covered = coverage[((y0 + y) * atlasWidth + x0 + x) * 4 + 3] > 0;
                seed[i] = covered ? i : -1;
                distance[i] = covered ? 0f : float.MaxValue;
                any |= covered;
            }
        }

        if (!any)
            return;

        void Relax(int x, int y, int dx, int dy)
        {
            int sx = x + dx, sy = y + dy;
            if ((uint)sx >= (uint)width || (uint)sy >= (uint)height)
                return;
            var s = seed[sy * width + sx];
            if (s < 0)
                return;
            float ex = x - s % width, ey = y - s / width;
            var d = ex * ex + ey * ey;
            var i = y * width + x;
            if (d < distance[i])
            {
                distance[i] = d;
                seed[i] = s;
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Relax(x, y, -1, -1);
                Relax(x, y, 0, -1);
                Relax(x, y, 1, -1);
                Relax(x, y, -1, 0);
            }
        }

        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = width - 1; x >= 0; x--)
            {
                Relax(x, y, 1, 0);
                Relax(x, y, -1, 1);
                Relax(x, y, 0, 1);
                Relax(x, y, 1, 1);
            }
        }

        for (var i = 0; i < seed.Length; i++)
        {
            var s = seed[i];
            if (s == i || s < 0)
                continue;
            var o = ((y0 + i / width) * atlasWidth + x0 + i % width) * 4;
            var from = ((y0 + s / width) * atlasWidth + x0 + s % width) * 4;
            foreach (var map in maps)
            {
                map[o] = map[from];
                map[o + 1] = map[from + 1];
                map[o + 2] = map[from + 2];
            }
        }
    }

    public static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
