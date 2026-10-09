namespace MainframeEngine.Tests.Imaging;

/// <summary>ADR 0172: <see cref="MipChain"/>, the CPU mip chain with coverage-preserving alpha (Castaño 2010).</summary>
public sealed class MipChainTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(256, 256, 9)]
    [InlineData(256, 64, 9)]
    [InlineData(5, 3, 3)]
    public void LevelCountRunsDownToOnePixel(int width, int height, int levels)
    {
        Assert.Equal(levels, MipChain.LevelCount(width, height));
        Assert.Equal((1, 1), MipChain.LevelSize(width, height, levels - 1));
    }

    [Fact]
    public void ChainStartsWithTheImageAndBoxFiltersEachLevel()
    {
        // 2 × 2 → 1 × 1: the average of the four texels (linear data, rounded).
        byte[] rgba = [0, 0, 0, 0, 100, 10, 20, 255, 200, 30, 40, 255, 100, 50, 60, 255];
        var chain = MipChain.Build(rgba, 2, 2, srgb: false);

        Assert.Equal(MipChain.ChainBytes(2, 2), chain.Length);
        Assert.Equal(rgba, chain[..16]);
        Assert.Equal([100, 23, 30, 191], chain[16..]);
    }

    [Fact]
    public void SrgbLevelsAverageInLinearLight()
    {
        // Black and white average to linear 0.5: sRGB 188, not 128.
        byte[] rgba = [0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 0, 255, 255, 255, 255, 255];
        var chain = MipChain.Build(rgba, 2, 2, srgb: true);

        Assert.InRange(chain[16], (byte)187, (byte)189);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(0.3f)]
    public void CoveragePreservingMipsKeepTheAlphaTestCoverageWithinTwoPercent(float cutoff)
    {
        var (rgba, size) = Sprigs();
        var plain = MipChain.Build(rgba, size, size, srgb: true);
        var preserved = MipChain.Build(rgba, size, size, srgb: true, preserveCoverageCutoff: cutoff);
        var target = MipChain.Coverage(rgba, cutoff);
        Assert.InRange(target, 0.1f, 0.6f);

        var offset = 0;
        var plainLoss = 0f;
        for (var level = 0; level < MipChain.LevelCount(size, size) - 3; level++) // down to 8 × 8 texels
        {
            var (w, h) = MipChain.LevelSize(size, size, level);
            var bytes = w * h * 4;
            var coverage = MipChain.Coverage(preserved.AsSpan(offset, bytes), cutoff);
            Assert.True(MathF.Abs(coverage - target) <= 0.02f, $"level {level}: coverage {coverage:0.000}, level 0 {target:0.000}");
            plainLoss = MathF.Max(plainLoss, MathF.Abs(target - MipChain.Coverage(plain.AsSpan(offset, bytes), cutoff)));
            offset += bytes;
        }

        // Without it, coverage drifts with distance (at a high cutoff thin sprigs thin out: the speckled canopy).
        Assert.True(plainLoss > 0.03f, $"plain mips only drifted {plainLoss:0.000} from the coverage");
    }

    [Fact]
    public void BuildingIsDeterministic()
    {
        var (rgba, size) = Sprigs();
        Assert.Equal(MipChain.Build(rgba, size, size, srgb: true, 0.5f), MipChain.Build(rgba, size, size, srgb: true, 0.5f));
    }

    // Small anti-aliased leaflets (discs of 1.5–5 px) scattered over transparent texels: a leaf sprig's alpha in miniature.
    private static (byte[] Rgba, int Size) Sprigs()
    {
        const int size = 256;
        var alpha = new float[size * size];
        var state = 12345u;
        float Next()
        {
            state = state * 1664525u + 1013904223u;
            return (state >> 8) / 16777216f;
        }

        for (var n = 0; n < 900; n++)
        {
            float cx = Next() * size, cy = Next() * size, r = 1.5f + 3.5f * Next();
            for (var y = (int)MathF.Max(0, cy - r - 1); y <= (int)MathF.Min(size - 1, cy + r + 1); y++)
            {
                for (var x = (int)MathF.Max(0, cx - r - 1); x <= (int)MathF.Min(size - 1, cx + r + 1); x++)
                {
                    var d = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    alpha[y * size + x] = MathF.Max(alpha[y * size + x], Math.Clamp(r - d + 0.5f, 0f, 1f));
                }
            }
        }

        var rgba = new byte[size * size * 4];
        for (var i = 0; i < alpha.Length; i++)
        {
            rgba[i * 4] = 60;
            rgba[i * 4 + 1] = 140;
            rgba[i * 4 + 2] = 40;
            rgba[i * 4 + 3] = (byte)MathF.Round(alpha[i] * 255f);
        }

        return (rgba, size);
    }
}
