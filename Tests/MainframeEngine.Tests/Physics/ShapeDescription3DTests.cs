using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>What each shape resource hands a physics backend (B2): kind, sizes, transform, the resource's arrays.</summary>
public sealed class ShapeDescription3DTests
{
    private static readonly Basis Turned = Basis.FromRotationScale(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), new Vector3(1, 2, 3));
    private static readonly Vector3 Offset = new(1, 2, 3);

    private static ShapeDescription3D One(Shape3D shape)
    {
        var list = new List<ShapeDescription3D>();
        shape.Describe(list, Turned, Offset);
        var d = Assert.Single(list);
        Assert.Same(shape, d.Resource);
        Assert.Equal(shape.ChangeCount, d.ChangeCount);
        Assert.Equal(Turned, d.Linear);
        Assert.Equal(Offset, d.Translation);
        return d;
    }

    [Fact]
    public void Box()
    {
        var d = One(new BoxShape3D { Size = new Vector3(1, 2, 3) });
        Assert.Equal(ShapeKind3D.Box, d.Kind);
        Assert.Equal(new Vector3(1, 2, 3), d.Size);
    }

    [Fact]
    public void Sphere()
    {
        var d = One(new SphereShape3D { Radius = 0.75f });
        Assert.Equal(ShapeKind3D.Sphere, d.Kind);
        Assert.Equal(0.75f, d.Radius);
    }

    [Fact]
    public void Capsule()
    {
        var shape = new CapsuleShape3D { Radius = 0.4f, Height = 1.8f };
        var d = One(shape);
        Assert.Equal(ShapeKind3D.Capsule, d.Kind);
        Assert.Equal((0.4f, 1.8f), (d.Radius, d.Height));
        Assert.Equal(shape.MidHeight, d.MidHeight); // the same expression, so the same float
        Assert.Equal(0f, One(new CapsuleShape3D { Radius = 1f, Height = 1.5f }).MidHeight);
    }

    [Fact]
    public void Cylinder()
    {
        var d = One(new CylinderShape3D { Radius = 0.3f, Height = 0.8f });
        Assert.Equal(ShapeKind3D.Cylinder, d.Kind);
        Assert.Equal((0.3f, 0.8f), (d.Radius, d.Height));
    }

    [Fact]
    public void ConvexHullSharesThePointArrayAndNeedsFourPoints()
    {
        var shape = new ConvexPolygonShape3D { Points = [new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f), new(0, 0.5f, -0.5f), new(0, 0, 0.5f)] };
        var d = One(shape);
        Assert.Equal(ShapeKind3D.ConvexHull, d.Kind);
        Assert.Same(shape.Points, d.Points);

        var e = Assert.Throws<InvalidOperationException>(() => new ConvexPolygonShape3D { Points = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY] }.Describe([], Turned, Offset));
        Assert.Equal("ConvexPolygonShape3D needs at least 4 points.", e.Message);
    }

    [Fact]
    public void ConcaveMeshSharesTheFaceArray()
    {
        var shape = new ConcavePolygonShape3D { Faces = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ] };
        var d = One(shape);
        Assert.Equal(ShapeKind3D.ConcaveMesh, d.Kind);
        Assert.Same(shape.Faces, d.Points);
    }

    [Fact]
    public void HeightMapHandsItsCachedTriangleList()
    {
        var shape = new HeightMapShape3D { MapWidth = 3, MapDepth = 2, MapData = [0, 1, 0, 1, 2, 1] };
        var d = One(shape);
        Assert.Equal(ShapeKind3D.HeightMap, d.Kind);
        Assert.Equal(shape.GetFaces().ToArray(), d.Points);
        Assert.Same(d.Points, One(shape).Points); // built once until the map changes
        Assert.Equal(2 * 1 * 6, d.Points!.Length);
    }

    [Fact]
    public void QueriesDescribeConvexShapesAtTheOriginAndRejectConcaveOnes()
    {
        var d = new SphereShape3D { Radius = 2 }.DescribeQuery();
        Assert.Equal(Basis.Identity, d.Linear);
        Assert.Equal(Vector3.Zero, d.Translation);
        var e = Assert.Throws<NotSupportedException>(() => new HeightMapShape3D().DescribeQuery());
        Assert.Equal("HeightMapShape3D cannot be used as a query shape (it is concave).", e.Message);
    }

    [Fact]
    public void ChangeCountFollowsEveryGeometryEdit()
    {
        var box = new BoxShape3D();
        var count = box.ChangeCount;
        box.Size = Vector3.One; // no change: no bump
        Assert.Equal(count, box.ChangeCount);
        box.Size = new Vector3(2);
        Assert.Equal(count + 1, box.ChangeCount);

        // Terrain3D rewrites the faces in place and assigns the same array again: that must still count.
        var mesh = new ConcavePolygonShape3D { Faces = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ] };
        var faces = mesh.Faces;
        count = mesh.ChangeCount;
        faces[1] = Vector3.UnitY;
        mesh.Faces = faces;
        Assert.Equal(count + 1, mesh.ChangeCount);

        var map = new HeightMapShape3D();
        count = map.ChangeCount;
        map.MapData = [1, 1, 1, 1];
        map.MapWidth = 2; // unchanged
        map.MapDepth = 3;
        Assert.Equal(count + 2, map.ChangeCount);
    }

    [Fact]
    public void TheBodysScaleIsBakedIntoTheChildTransform()
    {
        var local = Transform3D.FromTrs(new Vector3(1, 1, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.25f), new Vector3(1, 2, 1));
        PhysicsSpace3D.BakeShapeTransform(new Vector3(2, 3, 4), local, out var linear, out var translation);
        Assert.Equal(new Vector3(2, 3, 4), translation);
        var scale = new Basis(new Vector3(2, 0, 0), new Vector3(0, 3, 0), new Vector3(0, 0, 4));
        Assert.Equal(scale * local.Basis, linear);
    }
}
