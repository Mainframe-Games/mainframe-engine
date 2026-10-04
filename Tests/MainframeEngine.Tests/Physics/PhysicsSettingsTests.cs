namespace MainframeEngine.Tests.Physics;

/// <summary>Settings and small value types guard their inputs.</summary>
public sealed class PhysicsSettingsTests
{
    [Fact]
    public void SettingsRejectInvalidValues()
    {
        var s3 = new PhysicsSettings3D();
        Assert.Throws<ArgumentOutOfRangeException>(() => s3.SubstepCount = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => s3.SolverIterations = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => s3.RelaxationIterations = -1);
        Assert.False(s3.Deterministic);

        var s2 = new PhysicsSettings2D();
        Assert.Throws<ArgumentOutOfRangeException>(() => s2.PixelsPerMeter = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => s2.PixelsPerMeter = float.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => s2.SubstepCount = 0);
    }

    [Fact]
    public void ShapesAndBodiesRejectInvalidValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoxShape3D { Size = new System.Numerics.Vector3(1, 0, 1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SphereShape3D { Radius = -1 });
        Assert.Throws<ArgumentException>(() => new ConcavePolygonShape3D { Faces = [System.Numerics.Vector3.Zero] });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HeightMapShape3D { MapWidth = 1 });
        Assert.Throws<ArgumentException>(() => new ConvexPolygonShape2D { Points = new System.Numerics.Vector2[9] });
        Assert.Throws<ArgumentException>(() => new ConcavePolygonShape2D { Segments = new System.Numerics.Vector2[3] });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RigidBody3D { Mass = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PhysicsMaterial { Friction = -0.1f });
        Assert.Throws<ArgumentException>(() => new CharacterBody3D { UpDirection = System.Numerics.Vector3.Zero });
    }

    [Fact]
    public void LayerHelpersFollowGodotNumbering()
    {
        Assert.Equal(1u, CollisionLayers.Layer(1));
        Assert.Equal(1u << 31, CollisionLayers.Layer(32));
        Assert.Throws<ArgumentOutOfRangeException>(() => CollisionLayers.Layer(0));
        Assert.True(CollisionLayers.ShouldCollide(1, 0, 2, 1));  // B scans A's layer
        Assert.False(CollisionLayers.ShouldCollide(1, 0, 2, 0));

        var body = new StaticBody3D();
        body.SetCollisionLayerValue(3, true);
        body.SetCollisionMaskValue(1, false);
        Assert.Equal(0b101u, body.CollisionLayer);
        Assert.True(body.GetCollisionLayerValue(3));
        Assert.False(body.GetCollisionMaskValue(1));
        body.Free();
    }
}
