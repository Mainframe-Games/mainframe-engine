namespace MainframeEngine.Trees;

/// <summary>A bilinear footprint in one level of a <see cref="BakeTexture"/>: four texel offsets and the weights.</summary>
internal readonly record struct BakeTap(float[] Data, int P00, int P10, int P01, int P11, float Fx, float Fy);

/// <summary>
/// A texture for the CPU bakes (ADR 0172): RGBA8 pixels decoded once into a linear float pyramid (colour from sRGB unless
/// the image is data, alpha straight), sampled bilinearly at a level of detail, repeating or clamped. Immutable after
/// construction, so many bake threads can sample it at once.
/// </summary>
internal sealed class BakeTexture
{
    private readonly float[][] _levels; // per level: RGBA floats, row-major
    private readonly int[] _widths;
    private readonly int[] _heights;

    public BakeTexture(ReadOnlySpan<byte> rgba, int width, int height, bool srgb, bool clamp)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Clamp = clamp;
        var count = MipChain.LevelCount(width, height);
        _levels = new float[count][];
        _widths = new int[count];
        _heights = new int[count];

        var level0 = new float[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            for (var c = 0; c < 3; c++)
                level0[i * 4 + c] = srgb ? SrgbTable[rgba[i * 4 + c]] : rgba[i * 4 + c] / 255f;
            level0[i * 4 + 3] = rgba[i * 4 + 3] / 255f;
        }

        _levels[0] = level0;
        _widths[0] = width;
        _heights[0] = height;
        for (var l = 1; l < count; l++)
        {
            int w = _widths[l - 1], h = _heights[l - 1];
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var src = _levels[l - 1];
            var dst = new float[nw * nh * 4];
            for (var y = 0; y < nh; y++)
            {
                int y0 = Math.Min(2 * y, h - 1), y1 = Math.Min(2 * y + 1, h - 1);
                for (var x = 0; x < nw; x++)
                {
                    int x0 = Math.Min(2 * x, w - 1), x1 = Math.Min(2 * x + 1, w - 1);
                    for (var c = 0; c < 4; c++)
                        dst[(y * nw + x) * 4 + c] = 0.25f * (src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c] +
                                                             src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c]);
                }
            }

            _levels[l] = dst;
            _widths[l] = nw;
            _heights[l] = nh;
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Texture2D, CachedTexture> Decoded = [];

    private sealed record CachedTexture(int Version, bool Srgb, bool Clamp, BakeTexture Texture);

    /// <summary>
    /// A texture from a <see cref="Texture2D"/>'s pixels (decoded once per texture version and kept while the texture
    /// lives: bakes of many trees share a leaf or bark image).
    /// </summary>
    public static BakeTexture From(Texture2D texture, bool srgb, bool clamp)
    {
        ArgumentNullException.ThrowIfNull(texture);
        lock (Decoded)
        {
            if (Decoded.TryGetValue(texture, out var cached) && cached.Version == texture.Version && cached.Srgb == srgb && cached.Clamp == clamp)
                return cached.Texture;
        }

        var (rgba, width, height) = texture.DecodePixels();
        var bake = new BakeTexture(rgba, width, height, srgb, clamp);
        lock (Decoded)
            Decoded.AddOrUpdate(texture, new CachedTexture(texture.Version, srgb, clamp, bake));
        return bake;
    }

    public int Width => _widths[0];

    public int Height => _heights[0];

    public bool Clamp { get; }

    /// <summary>Linear RGBA at <paramref name="u"/>, <paramref name="v"/> (origin top-left) and level of detail <paramref name="lod"/> (0 = full size).</summary>
    public (float R, float G, float B, float A) Sample(float u, float v, float lod)
    {
        var tap = Tap(u, v, Level(lod));
        return (Color(tap, 0), Color(tap, 1), Color(tap, 2), Color(tap, 3));
    }

    /// <summary>The level of detail <paramref name="lod"/> resolves to (nearest; pass it to <see cref="Tap"/>).</summary>
    public int Level(float lod) => Math.Clamp((int)(MathF.Max(lod, 0f) + 0.5f), 0, _levels.Length - 1);

    /// <summary>A bilinear tap at <paramref name="u"/>, <paramref name="v"/> of level <paramref name="level"/>; read it with <see cref="Color"/>.</summary>
    public BakeTap Tap(float u, float v, int level)
    {
        int w = _widths[level], h = _heights[level];
        var x = u * w - 0.5f;
        var y = v * h - 0.5f;
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        int ax = Wrap(x0, w), bx = Wrap(x0 + 1, w), ay = Wrap(y0, h), by = Wrap(y0 + 1, h);
        return new BakeTap(_levels[level], (ay * w + ax) * 4, (ay * w + bx) * 4, (by * w + ax) * 4, (by * w + bx) * 4, fx, fy);
    }

    /// <summary>Channel <paramref name="channel"/> (0 R … 3 A) of a tap.</summary>
    public static float Color(in BakeTap tap, int channel)
    {
        var d = tap.Data;
        float top = d[tap.P00 + channel] + (d[tap.P10 + channel] - d[tap.P00 + channel]) * tap.Fx;
        float bottom = d[tap.P01 + channel] + (d[tap.P11 + channel] - d[tap.P01 + channel]) * tap.Fx;
        return top + (bottom - top) * tap.Fy;
    }

    private int Wrap(int i, int size)
    {
        if (Clamp)
            return Math.Clamp(i, 0, size - 1);
        i %= size;
        return i < 0 ? i + size : i;
    }

    private static readonly float[] SrgbTable = BuildTable();

    private static float[] BuildTable()
    {
        var table = new float[256];
        for (var i = 0; i < 256; i++)
            table[i] = ColorSpace.SrgbToLinear(i / 255f);
        return table;
    }
}
