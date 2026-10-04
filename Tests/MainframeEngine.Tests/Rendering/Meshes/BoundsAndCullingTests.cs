using System.Numerics;

namespace MainframeEngine.Tests.Rendering.Meshes;

public sealed class BoundsAndCullingTests
{
    [Fact]
    public void AabbBasics()
    {
        var box = Aabb.FromPoints([new Vector3(1, 2, 3), new Vector3(-1, 0, 5), new Vector3(0, 4, 4)]);
        Assert.Equal(new Vector3(-1, 0, 3), box.Min);
        Assert.Equal(new Vector3(1, 4, 5), box.Max);
        Assert.Equal(new Vector3(0, 2, 4), box.Center);
        Assert.Equal(new Vector3(1, 2, 1), box.Extents);
        Assert.True(box.Contains(new Vector3(0, 1, 4)));
        Assert.False(box.Contains(new Vector3(2, 1, 4)));

        Assert.True(Aabb.Empty.IsEmpty);
        Assert.True(Aabb.FromPoints([]).IsEmpty);
        Assert.Equal(box, Aabb.Empty.Merge(box));
        Assert.Equal(box, box.Merge(Aabb.Empty));
        Assert.Equal(new Vector3(9), box.Merge(new Aabb(new Vector3(8), new Vector3(9))).Max);
        Assert.Equal(new Vector3(-2, 0, 3), box.Encapsulate(new Vector3(-2, 1, 4)).Min);
    }

    [Fact]
    public void TransformedBoundsEncloseTheTransformedCorners()
    {
        var box = new Aabb(new Vector3(-1, -2, -0.5f), new Vector3(1, 2, 0.5f));
        var model = Matrix4x4.CreateScale(2, 1, 3) * Matrix4x4.CreateFromYawPitchRoll(0.7f, -0.3f, 1.1f) * Matrix4x4.CreateTranslation(5, -1, 2);
        var transformed = box.Transform(model);

        Span<Vector3> corners = stackalloc Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z);
            corners[i] = Vector3.Transform(corner, model);
        }

        var exact = Aabb.FromPoints(corners);
        Assert.True(Vector3.Distance(exact.Min, transformed.Min) < 1e-4f, $"{exact.Min} vs {transformed.Min}");
        Assert.True(Vector3.Distance(exact.Max, transformed.Max) < 1e-4f, $"{exact.Max} vs {transformed.Max}");
    }

    [Fact]
    public void FrustumKeepsVisibleBoxesAndCullsOthers()
    {
        // Camera at the origin looking down -Z, 90° vertical FOV, square aspect, near 0.1, far 100.
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1f, 0.1f, 100f);
        var frustum = new Frustum(view * projection);

        static Aabb At(float x, float y, float z) => new(new Vector3(x - 0.5f, y - 0.5f, z - 0.5f), new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));

        Assert.True(frustum.Intersects(At(0, 0, -10)));    // straight ahead
        Assert.True(frustum.Intersects(At(9.9f, 0, -10))); // just inside the right plane (x = -z)
        Assert.False(frustum.Intersects(At(0, 0, 10)));    // behind
        Assert.False(frustum.Intersects(At(12, 0, -10)));  // right of the frustum
        Assert.False(frustum.Intersects(At(0, -12, -10))); // below
        Assert.False(frustum.Intersects(At(0, 0, -200)));  // beyond the far plane
        Assert.True(frustum.Intersects(new Aabb(new Vector3(-1000), new Vector3(1000)))); // contains the frustum
        Assert.False(frustum.Intersects(Aabb.Empty));
    }

    [Fact]
    public void DeterminantDetectsMirroredTransforms()
    {
        Assert.True(MeshRenderer.Determinant3(Matrix4x4.CreateScale(2, 3, 4)) > 0);
        Assert.True(MeshRenderer.Determinant3(Matrix4x4.CreateScale(-1, 1, 1)) < 0);
        Assert.True(MeshRenderer.Determinant3(Matrix4x4.CreateRotationY(2f) * Matrix4x4.CreateTranslation(9, 9, 9)) > 0);
    }
}
