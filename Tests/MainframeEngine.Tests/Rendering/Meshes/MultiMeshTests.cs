using System.Numerics;

namespace MainframeEngine.Tests.Rendering.Meshes;

/// <summary>ADR 0151: the <see cref="MultiMesh"/> resource API, its bounds, and visibility ranges (no GPU).</summary>
public sealed class MultiMeshTests
{
    private static Transform3D At(float x, float y = 0f, float z = 0f) => new(Basis.Identity, new Vector3(x, y, z));

    [Fact]
    public void ResizingResetsEveryTransformToIdentity()
    {
        var multimesh = new MultiMesh { InstanceCount = 3 };
        multimesh.SetInstanceTransform(1, At(5));
        Assert.Equal(At(5), multimesh.GetInstanceTransform(1));

        var version = multimesh.Version;
        multimesh.InstanceCount = 3; // unchanged: kept
        Assert.Equal(version, multimesh.Version);
        Assert.Equal(At(5), multimesh.GetInstanceTransform(1));

        multimesh.InstanceCount = 4;
        Assert.True(multimesh.Version > version);
        Assert.All(multimesh.Transforms, t => Assert.Equal(Transform3D.Identity, t));
        Assert.Throws<ArgumentOutOfRangeException>(() => multimesh.InstanceCount = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => multimesh.GetInstanceTransform(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => multimesh.SetInstanceTransform(-1, At(0)));
    }

    [Fact]
    public void BulkTransformsCopyIntoARangeWithOneVersionBump()
    {
        var multimesh = new MultiMesh { InstanceCount = 5 };
        var version = multimesh.Version;
        multimesh.SetTransforms([At(1), At(2)], start: 3);
        Assert.Equal(version + 1, multimesh.Version);
        Assert.Equal(Transform3D.Identity, multimesh.GetInstanceTransform(2));
        Assert.Equal(At(1), multimesh.GetInstanceTransform(3));
        Assert.Equal(At(2), multimesh.GetInstanceTransform(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => multimesh.SetTransforms([At(1), At(2)], start: 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => multimesh.SetTransforms([At(1)], start: -1));
    }

    [Fact]
    public void VisibleInstanceCountLimitsTheDrawnInstances()
    {
        var multimesh = new MultiMesh { InstanceCount = 10 };
        Assert.Equal(-1, multimesh.VisibleInstanceCount);
        Assert.Equal(10, multimesh.DrawnInstanceCount);
        multimesh.VisibleInstanceCount = 4;
        Assert.Equal(4, multimesh.DrawnInstanceCount);
        multimesh.VisibleInstanceCount = 50;
        Assert.Equal(10, multimesh.DrawnInstanceCount);
        multimesh.VisibleInstanceCount = -7;
        Assert.Equal(-1, multimesh.VisibleInstanceCount);
        Assert.Equal(10, multimesh.DrawnInstanceCount);
    }

    [Fact]
    public void BoundsCoverTheDrawnInstancesAndFollowTheMesh()
    {
        var box = new BoxMesh { Size = Vector3.One };
        var multimesh = new MultiMesh { InstanceCount = 3 };
        Assert.True(multimesh.GetAabb().IsEmpty); // no mesh

        multimesh.Mesh = box;
        multimesh.SetTransforms([At(0), At(10), At(0, 0, -4)]);
        Assert.Equal(new Aabb(new Vector3(-0.5f, -0.5f, -4.5f), new Vector3(10.5f, 0.5f, 0.5f)), multimesh.GetAabb());

        multimesh.VisibleInstanceCount = 1;
        Assert.Equal(new Aabb(new Vector3(-0.5f), new Vector3(0.5f)), multimesh.GetAabb());

        box.Size = new Vector3(2f); // a mesh change moves the bounds without touching the multimesh
        Assert.Equal(new Aabb(new Vector3(-1f), new Vector3(1f)), multimesh.GetAabb());
    }

    [Fact]
    public void TheNodeDrawsNothingWithoutInstances()
    {
        var box = new BoxMesh();
        var node = new MultiMeshInstance3D();
        Assert.Null(node.GetRenderMesh());
        node.Multimesh = new MultiMesh { Mesh = box };
        Assert.Null(node.GetRenderMesh()); // no instances
        node.Multimesh.InstanceCount = 2;
        Assert.Same(box, node.GetRenderMesh());
        node.Multimesh.VisibleInstanceCount = 0;
        Assert.Null(node.GetRenderMesh());
        node.Free();
    }

    [Theory]
    [InlineData(5f, 0f, 0f, true)]
    [InlineData(5f, 10f, 0f, false)] // nearer than begin
    [InlineData(10f, 10f, 0f, true)] // begin is inclusive
    [InlineData(20f, 0f, 20f, false)] // end is exclusive
    [InlineData(19.9f, 0f, 20f, true)]
    [InlineData(1e6f, 0f, 0f, true)] // both open
    [InlineData(15f, 10f, 20f, true)]
    [InlineData(25f, 10f, 20f, false)]
    public void VisibilityRangesAreHalfOpenAndZeroIsUnbounded(float distance, float begin, float end, bool visible) =>
        Assert.Equal(visible, GeometryInstance3D.IsInVisibilityRange(distance, begin, end));

    [Fact]
    public void VisibilityRangeMeasuresToTheCentreOfTheWorldBounds()
    {
        var node = new MeshInstance3D { VisibilityRangeBegin = 10f, VisibilityRangeEnd = 20f };
        var bounds = new Aabb(new Vector3(14, -1, -1), new Vector3(16, 1, 1)); // centre (15, 0, 0)
        Assert.True(node.IsInVisibilityRange(Vector3.Zero, bounds));
        Assert.False(node.IsInVisibilityRange(new Vector3(10, 0, 0), bounds)); // 5 m
        Assert.False(node.IsInVisibilityRange(new Vector3(-6, 0, 0), bounds)); // 21 m
        node.VisibilityRangeBegin = node.VisibilityRangeEnd = 0f;
        Assert.True(node.IsInVisibilityRange(new Vector3(1e5f, 0, 0), bounds));
        node.Free();
    }
}
