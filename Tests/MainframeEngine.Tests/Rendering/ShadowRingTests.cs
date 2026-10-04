using System.Numerics;

namespace MainframeEngine.Tests.Rendering;

public sealed class ShadowRingTests
{
    [Theory]
    [InlineData(0ul, 256ul)]   // unspecified alignment → spec maximum
    [InlineData(16ul, 256ul)]  // Apple GPUs
    [InlineData(64ul, 256ul)]
    [InlineData(256ul, 256ul)]
    public void StrideIsAtLeastTheSpecMaximumAlignment(ulong deviceAlignment, ulong expectedStride)
    {
        var ring = new UniformRing(64, ShadowSystem.MaxShadowPasses, IVulkanContext.MaxFramesInFlight, deviceAlignment);

        Assert.Equal(expectedStride, ring.Stride);
        Assert.Equal(expectedStride * ShadowSystem.MaxShadowPasses * IVulkanContext.MaxFramesInFlight, ring.Size);
    }

    [Fact]
    public void EveryPassOfEveryFrameSlotHasItsOwnAlignedOffset()
    {
        var ring = new UniformRing(64, ShadowSystem.MaxShadowPasses, IVulkanContext.MaxFramesInFlight, 64);
        var offsets = new HashSet<uint>();

        for (var slot = 0; slot < IVulkanContext.MaxFramesInFlight; slot++)
        {
            for (var pass = 0; pass < ShadowSystem.MaxShadowPasses; pass++)
            {
                var offset = ring.Offset(slot, pass);
                Assert.Equal(0u, offset % 256);
                Assert.True(offset + 64 <= ring.Size);
                Assert.True(offsets.Add(offset), $"slot {slot} pass {pass} reuses offset {offset}");
            }
        }
    }

    [Fact]
    public void OutOfRangeSlotsThrow()
    {
        var ring = new UniformRing(64, 4, 2, 256);

        Assert.Throws<ArgumentOutOfRangeException>(() => ring.Offset(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.Offset(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.Offset(-1, 0));
        Assert.Throws<ArgumentException>(() => new UniformRing(64, 4, 2, 48)); // not a power of two
    }

    [Theory]
    [InlineData(0f, -1f, 0f)]       // straight down
    [InlineData(0.05f, 1f, 0f)]     // nearly straight up
    public void VerticalLightsUseZAsUp(float x, float y, float z)
    {
        Assert.Equal(Vector3.UnitZ, ShadowMath.ChooseUp(new Vector3(x, y, z)));

        // ...and still produce a finite light matrix (CreateLookAt with a parallel up gives NaNs).
        var rotation = ShadowMath.LightRotation(new Vector3(x, y, z));
        var matrix = ShadowMath.SphereLightMatrix(rotation, Vector3.Zero, 20f, 2048, 10f, snap: true, out _);
        Assert.False(float.IsNaN(matrix.M11) || float.IsNaN(matrix.M44));
    }

    [Fact]
    public void ObliqueLightsUseYAsUp()
    {
        Assert.Equal(Vector3.UnitY, ShadowMath.ChooseUp(Vector3.Normalize(new Vector3(-0.4f, -1f, -0.6f))));
    }

    [Fact]
    public void RingHasASlotForEveryPassOfAFullFrame()
    {
        // 4 cascades + 11 atlas tiles (3 secondary directional + 8 spot) + 4 cubes x 6 faces.
        Assert.Equal(39, ShadowSystem.MaxShadowPasses);
        Assert.Equal(ShaderLimits.MaxShadowCascades + ShaderLimits.MaxShadowAtlasMaps + ShaderLimits.MaxShadowPoint * 6, ShadowSystem.MaxShadowPasses);
        Assert.Equal(ShaderLimits.MaxShadowDirectional - 1 + ShaderLimits.MaxShadowSpot, ShaderLimits.MaxShadowAtlasMaps);
    }
}
