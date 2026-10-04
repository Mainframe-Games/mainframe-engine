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

    [Fact]
    public void PassIndicesCoverTheRingWithoutOverlap()
    {
        var indices = new List<int>();
        for (var i = 0; i < ShadowSystem.MaxShadowDir; i++) indices.Add(ShadowSystem.PassIndexDir(i));
        for (var i = 0; i < ShadowSystem.MaxShadowSpot; i++) indices.Add(ShadowSystem.PassIndexSpot(i));
        for (var i = 0; i < ShadowSystem.MaxShadowPoint; i++)
            for (var face = 0; face < 6; face++)
                indices.Add(ShadowSystem.PassIndexPoint(i, face));

        Assert.Equal(35, ShadowSystem.MaxShadowPasses);
        Assert.Equal(Enumerable.Range(0, ShadowSystem.MaxShadowPasses), indices);
    }

    [Theory]
    [InlineData(0f, -1f, 0f)]       // straight down
    [InlineData(0.05f, 1f, 0f)]     // nearly straight up
    public void VerticalLightsUseZAsUp(float x, float y, float z)
    {
        Assert.Equal(Vector3.UnitZ, ShadowSystem.ChooseUp(new Vector3(x, y, z)));

        // ...and still produce a finite light matrix (CreateLookAt with a parallel up gives NaNs).
        var matrix = ShadowSystem.CalcDirLightMatrix(new DirectionalLight { Direction = new Vector3(x, y, z) });
        Assert.False(float.IsNaN(matrix.M11) || float.IsNaN(matrix.M44));
    }

    [Fact]
    public void ObliqueLightsUseYAsUp()
    {
        Assert.Equal(Vector3.UnitY, ShadowSystem.ChooseUp(Vector3.Normalize(new Vector3(-0.4f, -1f, -0.6f))));
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(12.5f, -3f, 99f)]
    public void FallbackMatrixPutsEveryPointOutsideTheShadowRange(float x, float y, float z)
    {
        // Same math as the shader: lightSpace * vec4(worldPos, 1) with the matrix uploaded as-is.
        var ls = Vector4.Transform(new Vector4(x, y, z, 1f), ShadowFallback.OutOfRangeLightMatrix);
        var ndc = ls / ls.W;

        Assert.Equal(1f, ls.W);
        Assert.True(ndc.Z > 1f, "depth must be outside [0, 1] so the shadow term is 1 (lit)");
    }
}
