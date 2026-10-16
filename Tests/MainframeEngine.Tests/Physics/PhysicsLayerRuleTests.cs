namespace MainframeEngine.Tests.Physics;

/// <summary>The layer rule both backends' filters call (ADR 0021).</summary>
public sealed class PhysicsLayerRuleTests
{
    [Theory]
    [InlineData(1u, 1u, 1u, 1u, true)]
    [InlineData(1u, 0u, 2u, 1u, true)]   // B scans A's layer: enough
    [InlineData(1u, 2u, 2u, 0u, true)]   // A scans B's layer: enough
    [InlineData(1u, 0u, 2u, 0u, false)]
    [InlineData(4u, 3u, 3u, 3u, true)]
    [InlineData(4u, 3u, 8u, 3u, false)]
    public void BodiesCollideWhenEitherScansTheOther(uint layerA, uint maskA, uint layerB, uint maskB, bool expected)
    {
        Assert.Equal(expected, PhysicsSpace3D.Collides(layerA, maskA, false, layerB, maskB, false));
        Assert.Equal(expected, PhysicsSpace3D.Collides(layerB, maskB, false, layerA, maskA, false));
        Assert.Equal(expected, CollisionLayers.ShouldCollide(layerA, maskA, layerB, maskB));
    }

    [Fact]
    public void AreasDetectBodiesByTheirMaskOnlyAndNeverAreas()
    {
        Assert.True(PhysicsSpace3D.Collides(0, 1, true, 1, 0, false));  // the area's mask has the body's layer
        Assert.False(PhysicsSpace3D.Collides(1, 0, true, 0, 1, false)); // the body's mask scanning the area's layer is not enough
        Assert.True(PhysicsSpace3D.Collides(1, 0, false, 0, 1, true));  // either argument order
        Assert.False(PhysicsSpace3D.Collides(0, 1, false, 1, 0, true));
        Assert.False(PhysicsSpace3D.Collides(1, 1, true, 1, 1, true));  // areas never detect areas
    }
}
