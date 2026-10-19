using System.Numerics;

namespace MainframeEngine.Tests.Rendering.Meshes;

/// <summary>CPU billboarding (ADR 0199) and the <see cref="Sprite3D"/> members that use it.</summary>
public sealed class BillboardTests
{
    private static readonly Vector3 SpritePosition = new(4, 1, -2);

    // Scaled, turned and placed like a node: rows are the scaled basis, the fourth row the position.
    private static readonly Matrix4x4 Model =
        Matrix4x4.CreateScale(2, 3, 1) * Matrix4x4.CreateRotationY(1f) * Matrix4x4.CreateTranslation(SpritePosition);

    private static Vector3 Row(in Matrix4x4 m, int row) => row switch
    {
        0 => new Vector3(m.M11, m.M12, m.M13),
        1 => new Vector3(m.M21, m.M22, m.M23),
        2 => new Vector3(m.M31, m.M32, m.M33),
        _ => new Vector3(m.M41, m.M42, m.M43),
    };

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void DisabledKeepsTheModelMatrix()
    {
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 5, 10), Vector3.Zero, Vector3.UnitY);
        Assert.Equal(Model, Billboard.Face(Model, BillboardMode.Disabled, view));
    }

    [Fact]
    public void EnabledFacesTheCameraKeepingPositionAndScale()
    {
        var camera = new Vector3(-6, 9, 12);
        var view = Matrix4x4.CreateLookAt(camera, SpritePosition, Vector3.UnitY);
        var faced = Billboard.Face(Model, BillboardMode.Enabled, view);

        AssertNear(SpritePosition, Row(faced, 3));
        Assert.Equal(2f, Row(faced, 0).Length(), 4);
        Assert.Equal(3f, Row(faced, 1).Length(), 4);
        Assert.Equal(1f, Row(faced, 2).Length(), 4);
        // The quad's +Z (its normal) points back at the camera, and its +Y is the camera's up.
        AssertNear(Vector3.Normalize(camera - SpritePosition), Vector3.Normalize(Row(faced, 2)));
        Assert.Equal(1f, Vector3.Dot(Vector3.Normalize(Row(faced, 1)), new Vector3(view.M12, view.M22, view.M32)), 4);
        Assert.True(MeshRenderer.Determinant3(faced) > 0f); // never mirrored
    }

    [Fact]
    public void FixedYStaysUprightAndTurnsAboutY()
    {
        var camera = new Vector3(-6, 30, 12); // high above, looking down at the sprite
        var view = Matrix4x4.CreateLookAt(camera, SpritePosition, Vector3.UnitY);
        var faced = Billboard.Face(Model, BillboardMode.FixedY, view);

        AssertNear(SpritePosition, Row(faced, 3));
        AssertNear(new Vector3(0, 3, 0), Row(faced, 1)); // upright: world Y, its scale kept
        var toCamera = camera - SpritePosition;
        AssertNear(Vector3.Normalize(new Vector3(toCamera.X, 0, toCamera.Z)), Vector3.Normalize(Row(faced, 2)));
        Assert.Equal(2f, Row(faced, 0).Length(), 4);
        Assert.Equal(0f, Row(faced, 0).Y, 5);
    }

    [Fact]
    public void FixedYLookingStraightDownUsesTheCameraRight()
    {
        var view = Matrix4x4.CreateLookAt(SpritePosition + new Vector3(0, 20, 0), SpritePosition, -Vector3.UnitZ);
        var faced = Billboard.Face(Model, BillboardMode.FixedY, view);

        AssertNear(new Vector3(2, 0, 0), Row(faced, 0)); // the camera's right is +X
        AssertNear(new Vector3(0, 3, 0), Row(faced, 1));
        AssertNear(new Vector3(0, 0, 1), Row(faced, 2));
    }

    [Fact]
    public void SpriteBillboardReachesTheRendererAndFlipsMirrorTheUvs()
    {
        var sprite = new Sprite3D { Texture = Texture2D.FromPixels(4, 2, new byte[4 * 2 * 4]), Billboard = BillboardMode.FixedY };
        Assert.Equal(BillboardMode.FixedY, sprite.RenderBillboard);

        var mesh = sprite.GetRenderMesh()!;
        var material = (StandardMaterial3D)sprite.GetRenderMaterial(mesh, 0);
        Assert.Equal(Vector2.One, material.UvScale);
        Assert.Equal(Vector2.Zero, material.UvOffset);

        sprite.FlipH = true;
        sprite.GetRenderMesh();
        Assert.Equal(new Vector2(-1, 1), material.UvScale);
        Assert.Equal(new Vector2(1, 0), material.UvOffset);

        sprite.FlipH = false;
        sprite.FlipV = true;
        sprite.GetRenderMesh();
        Assert.Equal(new Vector2(1, -1), material.UvScale);
        Assert.Equal(new Vector2(0, 1), material.UvOffset);
        sprite.Free();
    }

    [Fact]
    public void BillboardAndFlipsRoundTripThroughScenes()
    {
        var sprite = new Sprite3D { Name = "Tree", Billboard = BillboardMode.FixedY, FlipH = true };
        var copy = (Sprite3D)PackedScene.Parse(SceneSaver.ToJson(sprite)).Instantiate();
        Assert.Equal(BillboardMode.FixedY, copy.Billboard);
        Assert.True(copy.FlipH);
        Assert.False(copy.FlipV);
        sprite.Free();
        copy.Free();
    }
}
