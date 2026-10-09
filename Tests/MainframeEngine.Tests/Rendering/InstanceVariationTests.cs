using System.Numerics;

namespace MainframeEngine.Tests.Rendering;

/// <summary>ADR 0175: per-instance colour variation from the instance origin's hash.</summary>
public sealed class InstanceVariationTests
{
    [Fact]
    public void TheHashIsDeterministicAndQuantised()
    {
        var origin = new Vector3(12.3f, 4.56f, -78.9f);
        Assert.Equal(InstanceVariation.Hash(origin), InstanceVariation.Hash(origin));
        // Float noise well inside a 1/16 m step does not change it; a whole step does.
        Assert.Equal(InstanceVariation.Hash(origin), InstanceVariation.Hash(origin + new Vector3(0.001f, -0.001f, 0.002f)));
        Assert.NotEqual(InstanceVariation.Hash(origin), InstanceVariation.Hash(origin + new Vector3(InstanceVariation.Quantum, 0f, 0f)));
    }

    [Fact]
    public void NoJitterIsExactlyOne()
    {
        Assert.Equal(Vector3.One, InstanceVariation.Tint(new Vector3(5f, 1f, 7f), 0f, 0f));
        Assert.Equal(Vector4.Zero, InstanceVariation.Pack(0f, 0f));
        Assert.Equal(new Vector4(0.5f, 0.2f, 0f, 0f), InstanceVariation.Pack(2f, 0.2f)); // clamped to 0.5
    }

    [Fact]
    public void ValuesStayInRangeAndAverageToOne()
    {
        const float value = 0.15f, hue = 0.1f;
        var sum = Vector3.Zero;
        float minLuma = float.MaxValue, maxLuma = float.MinValue;
        var warmer = 0;
        const int count = 4096;
        for (var i = 0; i < count; i++)
        {
            var origin = new Vector3(i * 4.13f % 256f, (i % 7) * 0.37f, i * 2.71f % 256f);
            var r = InstanceVariation.Random(origin);
            Assert.InRange(r.X, -1f, 1f);
            Assert.InRange(r.Y, -1f, 1f);
            var tint = InstanceVariation.Tint(origin, value, hue);
            Assert.True(tint.X >= 0f && tint.Y >= 0f && tint.Z >= 0f);
            var luma = Vector3.Dot(tint, new Vector3(0.2126f, 0.7152f, 0.0722f));
            minLuma = MathF.Min(minLuma, luma);
            maxLuma = MathF.Max(maxLuma, luma);
            sum += tint;
            if (tint.X > tint.Z)
                warmer++;
        }

        var mean = sum / count;
        Assert.Equal(1f, mean.X, 0.02f);
        Assert.Equal(1f, mean.Y, 0.02f);
        Assert.Equal(1f, mean.Z, 0.02f);
        Assert.InRange(minLuma, 0.8f, 0.9f); // ≈ (1 − 0.15)(1 − 0.012)
        Assert.InRange(maxLuma, 1.1f, 1.2f);
        Assert.InRange(warmer, count * 45 / 100, count * 55 / 100); // half warmer, half cooler
    }

    [Fact]
    public void FoliageAndImpostorMaterialsPackTheirJitter()
    {
        var foliage = new FoliageMaterial3D();
        Assert.Equal(Vector4.Zero, MaterialParams.From(foliage, 0).Variation);
        var version = foliage.Version;
        foliage.InstanceValueJitter = 0.12f;
        foliage.InstanceHueJitter = 0.08f;
        Assert.Equal(version + 2, foliage.Version);
        Assert.Equal(new Vector4(0.12f, 0.08f, 0f, 0f), MaterialParams.From(foliage, 0).Variation);

        var impostor = new ImpostorMaterial3D { InstanceValueJitter = 0.1f, InstanceHueJitter = 0.05f };
        Assert.Equal(new Vector4(0.1f, 0.05f, 0f, 0f), MaterialParams.From(impostor, 0).Variation);
        Assert.Equal(Vector4.Zero, MaterialParams.From(new StandardMaterial3D(), 0).Variation);
    }
}
