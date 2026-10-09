namespace MainframeEngine;

/// <summary>
/// CPU mip chains for RGBA8 images (ADR 0172): a 2 × 2 box filter per level (colour averaged in linear light for sRGB
/// images, as the GPU's blits do), optionally preserving alpha-test coverage (Castaño 2010): each level's alpha is scaled
/// so the share of texels at or above the cutoff matches level 0's, so cut-out leaves keep their density in the distance
/// instead of thinning into speckles.
/// </summary>
public static class MipChain
{
    private static readonly float[] SrgbToLinearTable = BuildSrgbTable();

    /// <summary>Levels of a full chain for a <paramref name="width"/> × <paramref name="height"/> image (down to 1 × 1).</summary>
    public static int LevelCount(int width, int height)
    {
        var levels = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            levels++;
        }

        return levels;
    }

    /// <summary>The size of level <paramref name="level"/>.</summary>
    public static (int Width, int Height) LevelSize(int width, int height, int level)
    {
        for (var i = 0; i < level; i++)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        return (width, height);
    }

    /// <summary>Bytes of a full chain of RGBA8 levels, one after the other.</summary>
    public static int ChainBytes(int width, int height)
    {
        var bytes = 0;
        var levels = LevelCount(width, height);
        for (var i = 0; i < levels; i++)
        {
            var (w, h) = LevelSize(width, height, i);
            bytes += w * h * 4;
        }

        return bytes;
    }

    /// <summary>
    /// The full mip chain of <paramref name="rgba"/> (level 0 first, every level tightly packed, then the next). With
    /// <paramref name="preserveCoverageCutoff"/> > 0, each level's alpha keeps level 0's coverage at that cutoff (0..1).
    /// <paramref name="srgb"/>: the colour is sRGB-encoded (averaged in linear light).
    /// </summary>
    public static byte[] Build(ReadOnlySpan<byte> rgba, int width, int height, bool srgb, float preserveCoverageCutoff = 0f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (rgba.Length != width * height * 4)
            throw new ArgumentException($"Expected {width * height * 4} bytes of RGBA8, got {rgba.Length}.", nameof(rgba));

        var chain = new byte[ChainBytes(width, height)];
        rgba.CopyTo(chain);
        var levels = LevelCount(width, height);
        var preserve = preserveCoverageCutoff > 0f && preserveCoverageCutoff < 1f;
        var targetCoverage = preserve ? Coverage(rgba, preserveCoverageCutoff) : 0f;
        int offset = 0, w = width, h = height;
        for (var level = 1; level < levels; level++)
        {
            var nw = Math.Max(1, w / 2);
            var nh = Math.Max(1, h / 2);
            var src = chain.AsSpan(offset, w * h * 4);
            var dstOffset = offset + w * h * 4;
            var dst = chain.AsSpan(dstOffset, nw * nh * 4);
            Downsample(src, w, h, dst, nw, nh, srgb);
            if (preserve)
                ScaleAlphaToCoverage(dst, preserveCoverageCutoff, targetCoverage);
            offset = dstOffset;
            w = nw;
            h = nh;
        }

        return chain;
    }

    /// <summary>The share of texels whose alpha (0..1) is at least <paramref name="cutoff"/>.</summary>
    public static float Coverage(ReadOnlySpan<byte> rgba, float cutoff)
    {
        var count = rgba.Length / 4;
        if (count == 0)
            return 0f;
        var threshold = cutoff * 255f;
        var covered = 0;
        for (var i = 0; i < count; i++)
            if (rgba[i * 4 + 3] >= threshold)
                covered++;
        return (float)covered / count;
    }

    // 2 × 2 box (odd sizes: the last row/column is clamped), colour in linear light for sRGB, alpha straight.
    private static void Downsample(ReadOnlySpan<byte> src, int w, int h, Span<byte> dst, int nw, int nh, bool srgb)
    {
        for (var y = 0; y < nh; y++)
        {
            var y0 = Math.Min(2 * y, h - 1);
            var y1 = Math.Min(2 * y + 1, h - 1);
            for (var x = 0; x < nw; x++)
            {
                var x0 = Math.Min(2 * x, w - 1);
                var x1 = Math.Min(2 * x + 1, w - 1);
                var a = (y0 * w + x0) * 4;
                var b = (y0 * w + x1) * 4;
                var c = (y1 * w + x0) * 4;
                var d = (y1 * w + x1) * 4;
                var o = (y * nw + x) * 4;
                for (var ch = 0; ch < 3; ch++)
                {
                    if (srgb)
                    {
                        var sum = SrgbToLinearTable[src[a + ch]] + SrgbToLinearTable[src[b + ch]] +
                                  SrgbToLinearTable[src[c + ch]] + SrgbToLinearTable[src[d + ch]];
                        dst[o + ch] = ToByte(ColorSpace.LinearToSrgb(sum * 0.25f));
                    }
                    else
                    {
                        dst[o + ch] = (byte)((src[a + ch] + src[b + ch] + src[c + ch] + src[d + ch] + 2) >> 2);
                    }
                }

                dst[o + 3] = (byte)((src[a + 3] + src[b + 3] + src[c + 3] + src[d + 3] + 2) >> 2);
            }
        }
    }

    // Scales the level's alpha so the share of texels at or above the cutoff matches the target: the alpha quantile at
    // the target coverage is mapped onto the cutoff (a 256-bin histogram; the closest achievable coverage wins).
    private static void ScaleAlphaToCoverage(Span<byte> rgba, float cutoff, float target)
    {
        var count = rgba.Length / 4;
        if (count == 0 || target <= 0f)
            return;
        Span<int> histogram = stackalloc int[256];
        for (var i = 0; i < count; i++)
            histogram[rgba[i * 4 + 3]]++;

        // atLeast(t) = texels with alpha >= t; choose the t (1..255) whose share is closest to the target.
        var best = 255;
        var bestError = float.MaxValue;
        var atLeast = 0;
        for (var t = 255; t >= 1; t--)
        {
            atLeast += histogram[t];
            var error = MathF.Abs((float)atLeast / count - target);
            if (error < bestError)
            {
                bestError = error;
                best = t;
            }
        }

        // Integer scaling a × k / best (floored), k the smallest byte that passes the test: exactly the texels with
        // alpha >= best pass afterwards.
        var k = (int)MathF.Ceiling(cutoff * 255f - 1e-4f);
        if (k == best)
            return;
        for (var i = 0; i < count; i++)
        {
            var p = i * 4 + 3;
            rgba[p] = (byte)Math.Min(255, rgba[p] * k / best);
        }
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    private static float[] BuildSrgbTable()
    {
        var table = new float[256];
        for (var i = 0; i < 256; i++)
            table[i] = ColorSpace.SrgbToLinear(i / 255f);
        return table;
    }
}
