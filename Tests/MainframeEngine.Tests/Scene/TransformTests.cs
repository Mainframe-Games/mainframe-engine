using System.Numerics;

namespace MainframeEngine.Tests.Scene;

public sealed class TransformTests
{
    private const float Eps = 1e-4f;

    private static void AssertNear(Vector3 expected, Vector3 actual, float eps = Eps)
    {
        Assert.True(Vector3.Distance(expected, actual) < eps, $"expected {expected}, got {actual}");
    }

    private static void AssertNear(in Matrix4x4 expected, in Matrix4x4 actual, float eps = Eps)
    {
        for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                Assert.True(MathF.Abs(expected[r, c] - actual[r, c]) < eps, $"[{r},{c}] expected {expected[r, c]}, got {actual[r, c]}");
    }

    [Theory]
    [InlineData(90, 0, 0)]
    [InlineData(10, 20, 30)]
    [InlineData(-45, 170, 5)]
    [InlineData(0, 90, 0)]
    [InlineData(30, -90, 60)]
    [InlineData(123, 45, -67)]
    public void EulerDegreesMatchTheLegacyXThenYThenZMatrices(float x, float y, float z)
    {
        // The pre-M2 Node3D built Rx × Ry × Rz from Euler degrees; scenes must keep their look.
        var legacy = Matrix4x4.CreateRotationX(float.DegreesToRadians(x))
                     * Matrix4x4.CreateRotationY(float.DegreesToRadians(y))
                     * Matrix4x4.CreateRotationZ(float.DegreesToRadians(z));
        var q = EulerAngles.ToQuaternion(new Vector3(x, y, z));
        AssertNear(legacy, Matrix4x4.CreateFromQuaternion(q));

        // Decomposing gives an equivalent triple (not necessarily the same numbers).
        var roundTrip = EulerAngles.ToQuaternion(EulerAngles.FromQuaternion(q));
        AssertNear(legacy, Matrix4x4.CreateFromQuaternion(roundTrip));
    }

    [Fact]
    public void ModelMatrixMatchesLegacyScaleRotateTranslate()
    {
        var node = new Node3D { Position = new Vector3(3, 1, -2), RotationDegrees = new Vector3(10, 20, 30), Scale = new Vector3(2, 3, 4) };
        var legacy = Matrix4x4.CreateScale(node.Scale)
                     * Matrix4x4.CreateRotationX(float.DegreesToRadians(10))
                     * Matrix4x4.CreateRotationY(float.DegreesToRadians(20))
                     * Matrix4x4.CreateRotationZ(float.DegreesToRadians(30))
                     * Matrix4x4.CreateTranslation(node.Position);
        AssertNear(legacy, node.ModelMatrix);
        node.Free();
    }

    [Fact]
    public void RotationDegreesReturnsWhatWasSetSoIncrementsAreStable()
    {
        using var node = new Node3D();
        for (var i = 0; i < 200; i++)
            node.RotationDegrees += new Vector3(1, 1, 0);
        Assert.Equal(new Vector3(200, 200, 0), node.RotationDegrees);

        node.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        AssertNear(new Vector3(0, 90, 0), node.RotationDegrees);
    }

    [Fact]
    public void GlobalTransformComposesThroughNode3DParentsOnly()
    {
        using var root = new Node3D { Position = new Vector3(10, 0, 0), RotationDegrees = new Vector3(0, 90, 0) };
        var child = new Node3D { Position = new Vector3(0, 0, -1) };
        root.AddChild(child);

        // Root rotates -Z (forward) to -X: the child one unit forward lands at (9, 0, 0).
        AssertNear(new Vector3(9, 0, 0), child.GlobalPosition);
        AssertNear(new Vector3(-1, 0, 0), child.GlobalForward);

        // A plain Node in between starts a new transform root (Godot semantics).
        var plain = new PlainNode();
        root.AddChild(plain);
        var orphan3D = new Node3D { Position = new Vector3(1, 2, 3) };
        plain.AddChild(orphan3D);
        AssertNear(new Vector3(1, 2, 3), orphan3D.GlobalPosition);
    }

    [Fact]
    public void MovingAParentDirtiesTheWholeSubtree()
    {
        using var root = new Node3D();
        var a = new Node3D { Position = Vector3.UnitX };
        var b = new Node3D { Position = Vector3.UnitX };
        root.AddChild(a);
        a.AddChild(b);
        AssertNear(new Vector3(2, 0, 0), b.GlobalPosition); // cached now

        root.Position = new Vector3(0, 5, 0);
        AssertNear(new Vector3(2, 5, 0), b.GlobalPosition);

        a.Scale = new Vector3(2);
        AssertNear(new Vector3(3, 5, 0), b.GlobalPosition);
        AssertNear(Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(3, 5, 0), b.ModelMatrix);
    }

    [Fact]
    public void SettingGlobalTransformComputesTheLocalOne()
    {
        using var root = new Node3D { Position = new Vector3(5, 0, 0), Scale = new Vector3(2) };
        var child = new Node3D();
        root.AddChild(child);

        child.GlobalPosition = new Vector3(7, 4, 0);
        AssertNear(new Vector3(1, 2, 0), child.Position);
        AssertNear(new Vector3(7, 4, 0), child.GlobalPosition);

        var target = Transform3D.FromTrs(new Vector3(1, 1, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f), new Vector3(4));
        child.GlobalTransform = target;
        AssertNear(target.ToMatrix4x4(), child.GlobalTransform.ToMatrix4x4());
        AssertNear(new Vector3(2), child.Scale);
    }

    [Fact]
    public void ReparentKeepsTheGlobalTransformByDefault()
    {
        using var root = new Node3D();
        var from = new Node3D { Position = new Vector3(1, 0, 0) };
        var to = new Node3D { Position = new Vector3(0, 0, 10), RotationDegrees = new Vector3(0, 45, 0) };
        var moved = new Node3D { Position = new Vector3(0, 2, 0) };
        root.AddChild(from);
        root.AddChild(to);
        from.AddChild(moved);
        var before = moved.GlobalTransform.ToMatrix4x4();

        moved.Reparent(to);
        Assert.Same(to, moved.Parent);
        AssertNear(before, moved.GlobalTransform.ToMatrix4x4());

        // Without keepGlobalTransform the local transform is kept and the global one follows the new parent.
        var local = moved.Transform.ToMatrix4x4();
        moved.Reparent(from, keepGlobalTransform: false);
        AssertNear(local, moved.Transform.ToMatrix4x4());
        AssertNear((from.GlobalTransform * moved.Transform).ToMatrix4x4(), moved.GlobalTransform.ToMatrix4x4());
    }

    [Fact]
    public void LookAtPointsMinusZAtTheTargetAndKeepsScale()
    {
        using var node = new Node3D { Position = new Vector3(0, 5, 10), Scale = new Vector3(2) };
        node.LookAt(Vector3.Zero);
        AssertNear(Vector3.Normalize(new Vector3(0, -5, -10)), node.GlobalForward);
        Assert.Equal(new Vector3(2), node.Scale);
        AssertNear(new Vector3(-26.565052f, 0, 0), node.RotationDegrees, 1e-3f);

        // Under a rotated parent the global direction is still right.
        using var parent = new Node3D { RotationDegrees = new Vector3(0, 90, 0) };
        var child = new Node3D { Position = new Vector3(1, 0, 0) };
        parent.AddChild(child);
        child.LookAt(new Vector3(0, 0, 0));
        AssertNear(Vector3.Normalize(-child.GlobalPosition), child.GlobalForward);
    }

    [Fact]
    public void TransformNotificationsAreBatchedOncePerFrame()
    {
        var tree = new SceneTree();
        var parent = new Node3D();
        var watcher = new NotifyingNode3D();
        parent.AddChild(watcher);
        tree.Root.AddChild(parent);

        tree.Tick(default);
        Assert.Equal(1, watcher.Notifications); // entering the tree

        parent.Position = Vector3.One;
        parent.Position = new Vector3(2);
        watcher.RotationDegrees = new Vector3(0, 10, 0);
        tree.Tick(default);
        Assert.Equal(2, watcher.Notifications);

        tree.Tick(default); // nothing moved
        Assert.Equal(2, watcher.Notifications);

        parent.Position = Vector3.Zero; // parent moved while the child was clean
        tree.Tick(default);
        Assert.Equal(3, watcher.Notifications);
        tree.Shutdown();
    }

    [Fact]
    public void VisibilityIsInheritedThrough3DParents()
    {
        using var root = new Node3D();
        var child = new Node3D();
        root.AddChild(child);
        Assert.True(child.IsVisibleInTree());
        root.Visible = false;
        Assert.False(child.IsVisibleInTree());
        Assert.True(child.Visible);
    }

    [Fact]
    public void Transform3DMathIsConsistent()
    {
        var a = Transform3D.FromTrs(new Vector3(1, 2, 3), Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, 0.1f), new Vector3(1, 2, 3));
        var b = Transform3D.FromTrs(new Vector3(-4, 0, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1f), Vector3.One);

        // Composition matches matrix multiplication in System.Numerics' row-vector order.
        AssertNear(b.ToMatrix4x4() * a.ToMatrix4x4(), (a * b).ToMatrix4x4());
        AssertNear(Matrix4x4.Identity, (a * a.AffineInverse()).ToMatrix4x4());
        AssertNear(Vector3.Transform(new Vector3(1, 1, 1), a.ToMatrix4x4()), a * new Vector3(1, 1, 1));
        Assert.Equal(a, Transform3D.FromMatrix4x4(a.ToMatrix4x4()));

        a.Decompose(out var t, out var r, out var s);
        AssertNear(new Vector3(1, 2, 3), t);
        AssertNear(new Vector3(1, 2, 3), s);
        AssertNear(a.ToMatrix4x4(), Transform3D.FromTrs(t, r, s).ToMatrix4x4());

        var looking = Transform3D.LookingAt(new Vector3(0, 0, 5), Vector3.Zero, Vector3.UnitY);
        AssertNear(-Vector3.UnitZ, -looking.Basis.Z);
        Assert.Throws<ArgumentException>(() => Transform3D.LookingAt(Vector3.One, Vector3.One, Vector3.UnitY));
        Assert.Throws<InvalidOperationException>(() => new Basis(Vector3.Zero, Vector3.UnitY, Vector3.UnitZ).Inverse());
    }

    [Fact]
    public void Node2DComposesLikeNode3D()
    {
        using var root = new Node2D { Position = new Vector2(10, 0), RotationDegrees = 90 };
        var child = new Node2D { Position = new Vector2(1, 0), Scale = new Vector2(2, 2) };
        root.AddChild(child);

        var p = child.GlobalPosition;
        Assert.Equal(10f, p.X, 4);
        Assert.Equal(1f, p.Y, 4);
        Assert.Equal(MathF.PI / 2, child.GlobalRotation, 4);

        child.GlobalPosition = new Vector2(0, 0);
        Assert.Equal(0f, child.GlobalPosition.X, 4);
        Assert.Equal(0f, child.GlobalPosition.Y, 4);

        var t = Transform2D.FromTrs(new Vector2(3, 4), 0.5f, new Vector2(2, 3));
        var inv = t.AffineInverse() * t;
        Assert.Equal(1f, inv.X.X, 4);
        Assert.Equal(0f, inv.Origin.Y, 4);
        Assert.Equal(new Vector2(2, 3).X, t.Scale.X, 4);
        Assert.Equal(new Vector2(2, 3).Y, t.Scale.Y, 4);

        root.Visible = false;
        Assert.False(child.IsVisibleInTree());
    }

    [Fact]
    public void Node2DKeepsASkewedTransformAndDecomposesItLikeGodot()
    {
        // A cast-shadow shear: X stays the unit axis, Y leans to the left and shortens.
        var shear = new Transform2D(new Vector2(1, 0), new Vector2(-1.2f, 0.4f), new Vector2(5, 7));
        using var node = new Node2D { Transform = shear };
        Assert.Equal(shear.X, node.Transform.X);
        Assert.Equal(shear.Y, node.Transform.Y); // kept exactly, not rebuilt from the decomposition
        Assert.Equal(new Vector2(5, 7), node.Position);
        Assert.Equal(0f, node.Rotation, 5);
        Assert.Equal(MathF.Atan2(1.2f, 0.4f), node.Skew, 4); // Godot: acos(x̂·ŷ) − π/2
        Assert.Equal(new Vector2(1, new Vector2(-1.2f, 0.4f).Length()), node.Scale);

        // Rebuilding from the values gives the same transform (Godot's Transform2D(rot, scale, skew, pos)).
        node.Position = node.Position;
        Assert.True(Vector2.Distance(shear.Y, node.Transform.Y) < Eps, $"{node.Transform.Y}");
        Assert.Equal(0f, Transform2D.Identity.Skew, 6);
    }
}
