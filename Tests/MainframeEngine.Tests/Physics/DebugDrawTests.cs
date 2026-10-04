using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>The <see cref="DebugLines"/> batch and the physics servers' collision-shape debug draw.</summary>
[Collection(nameof(SerialBox2D))]
public sealed class DebugDrawTests
{
    private static readonly Vector4 White = Vector4.One;

    [Fact]
    public void DebugLinesBatchesSegmentsAndPrimitives()
    {
        var lines = new DebugLines();
        lines.AddLine(Vector3.Zero, Vector3.UnitX, White);
        Assert.Equal(1, lines.LineCount);
        Assert.Equal(Vector3.UnitX, lines.Vertices[1].Position);

        lines.Clear();
        lines.AddBox(Transform3D.Identity, Vector3.One, White);
        Assert.Equal(12, lines.LineCount);
        foreach (var v in lines.Vertices)
            Assert.Equal(1, MathF.Abs(v.Position.X)); // every corner at ±1

        lines.Clear();
        lines.AddSphere(Transform3D.FromTrs(new Vector3(5, 0, 0), Quaternion.Identity, new Vector3(2)), 1, White);
        Assert.Equal(72, lines.LineCount);
        foreach (var v in lines.Vertices)
            Assert.Equal(2, Vector3.Distance(v.Position, new Vector3(5, 0, 0)), 3);

        lines.Clear();
        lines.AddPolygon2D(Transform2D.Identity, [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], White);
        Assert.Equal(3, lines.LineCount);
        Assert.All(lines.Vertices.ToArray(), v => Assert.Equal(0, v.Position.Z));
    }

    [Fact]
    public void DebugLinesDropLinesBeyondTheCap()
    {
        var lines = new DebugLines();
        for (var i = 0; i < DebugLines.MaxLines + 10; i++)
            lines.AddLine(Vector3.Zero, Vector3.One, White);
        Assert.Equal(DebugLines.MaxLines, lines.LineCount);
        Assert.Equal(10, lines.DroppedLines);
        lines.Clear();
        Assert.Equal(0, lines.LineCount);
        Assert.Equal(0, lines.DroppedLines);
    }

    [Fact]
    public void PhysicsServersDrawCollisionShapesWhenEnabled()
    {
        using var h3 = new PhysicsHarness3D();
        h3.AddFloor();
        h3.AddBox(new Vector3(0, 3, 0));
        h3.AddArea(new Vector3(5, 0, 0), Vector3.One);
        var lines = h3.Tree.Root.DebugLines;
        h3.Run(1);
        Assert.Equal(0, lines.LineCount); // off by default

        h3.Server.DebugDrawEnabled = true;
        h3.Run(1);
        Assert.Equal(36, lines.LineCount); // three boxes × 12 edges
        // The box's lines follow its (rendered) pose.
        var maxY = float.MinValue;
        foreach (var v in lines.Vertices)
            maxY = MathF.Max(maxY, v.Position.Y);
        Assert.Equal(h3.Scene.GetNode<RigidBody3D>("Box").Position.Y + 0.5f, maxY, 3);

        using var h2 = new PhysicsHarness2D();
        h2.Server.DebugDrawEnabled = true;
        h2.AddGround();
        h2.AddBox(new Vector2(0, 100));
        h2.Run(1);
        Assert.Equal(8, h2.Tree.Root.DebugLines.LineCount); // two rectangles
    }
}
