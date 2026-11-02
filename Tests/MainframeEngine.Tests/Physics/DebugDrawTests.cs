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
        h2.AddBox(new Vector2(0, -100));
        h2.Run(1);
        Assert.Equal(8, h2.Tree.Root.DebugLines.LineCount); // two rectangles

        // A joint adds its anchors (two circles), the line between them and its current-angle marker.
        var pin = new PinJoint2D { Name = "Pin", Position = new Vector2(0, -150) };
        h2.Add(pin);
        pin.NodeB = "../Box";
        h2.Tree.Root.DebugLines.Clear(); // the renderer clears it each frame; headless, the test does
        h2.Run(1);
        Assert.Equal(8 + 1 + 2 * 25 + 1, h2.Tree.Root.DebugLines.LineCount); // circles: 24 segments and a radius line
    }

    [Fact]
    public void AMotorJointDrawsItsTargetAndTurnsRedFarFromIt()
    {
        using var h = new PhysicsHarness2D();
        var box = h.AddBox(Vector2.Zero, 20);
        box.GravityScale = 0;
        var motor = new MotorJoint2D { Name = "Motor", LinearOffset = new Vector2(100, 0), LinearHertz = 0.5f };
        h.Add(motor);
        motor.NodeB = "../Box";
        h.Server.DebugDrawJoints = true;
        var lines = h.Tree.Root.DebugLines;
        h.Run(1);
        // Anchor A is the target: two circles (24 segments and a radius line each), the line between them, the target's
        // cross (two lines) and its +X.
        Assert.Equal(2 * 25 + 1 + 3, lines.LineCount);
        Assert.Contains(lines.Vertices.ToArray(), v => Vector2.Distance(new Vector2(v.Position.X, v.Position.Y), new Vector2(106, 6)) < 0.01f);
        Assert.All(lines.Vertices.ToArray(), v => Assert.Equal(1f, v.Color.X)); // red or amber: the box is 100 px from it
        Assert.Contains(lines.Vertices.ToArray(), v => v.Color.Y < 0.5f); // red

        h.RunSeconds(10f);
        lines.Clear();
        h.Run(1);
        Assert.DoesNotContain(lines.Vertices.ToArray(), v => v.Color.Y < 0.5f); // amber once it is on the target
    }

    [Fact]
    public void DebugDrawJointsDrawsOnlyTheJoints()
    {
        using var h = new PhysicsHarness3D();
        h.AddFloor();
        var box = h.AddBox(new Vector3(0, 3, 0));
        var pin = new PinJoint3D { Name = "Pin", Position = new Vector3(0, 4, 0) };
        h.Add(pin);
        pin.NodeB = "../" + box.Name;
        var lines = h.Tree.Root.DebugLines;
        h.Server.DebugDrawJoints = true;
        h.Run(1);
        var jointsOnly = lines.LineCount;
        Assert.True(jointsOnly > 0);
        lines.Clear();
        h.Server.DebugDrawEnabled = true; // shapes and joints
        h.Run(1);
        Assert.Equal(jointsOnly + 24, lines.LineCount); // two boxes × 12 edges
    }
}
