using System.Numerics;

namespace MainframeEngine.Tests.Paths;

/// <summary><see cref="Path3D"/> and <see cref="PathFollow3D"/> in a headless tree (ADR 0215).</summary>
public sealed class PathFollow3DTests
{
    private const float Step = 1f / 60f;

    private sealed class Rig
    {
        public Rig(Curve3D curve)
        {
            Tree = new SceneTree(new ServerRegistry());
            var scene = new Node3D { Name = "Scene" };
            Tree.ChangeScene(scene);
            Path = new Path3D { Name = "Path", Curve = curve };
            Follow = new PathFollow3D { Name = "Follow" };
            Path.AddChild(Follow);
            scene.AddChild(Path);
        }

        public SceneTree Tree { get; }

        public Path3D Path { get; }

        public PathFollow3D Follow { get; }

        private uint _frame;

        public void Tick(int frames = 1)
        {
            for (var i = 0; i < frames; i++)
                Tree.Tick(new GameTime { DeltaTime = Step, FrameCount = ++_frame });
        }
    }

    // A 10 m line along +X, then a 10 m climb along +Y (open).
    private static Curve3D LineAndClimb()
    {
        var curve = new Curve3D { BakeInterval = 0.05f };
        curve.AddPoint(Vector3.Zero);
        curve.AddPoint(new Vector3(10, 0, 0));
        curve.AddPoint(new Vector3(10, 10, 0));
        return curve;
    }

    private static Curve3D Line(float length = 10f)
    {
        var curve = new Curve3D { BakeInterval = 0.05f };
        curve.AddPoint(Vector3.Zero);
        curve.AddPoint(new Vector3(length, 0, 0));
        return curve;
    }

    [Fact]
    public void ProgressPlacesTheFollowerAndKeepsItsScale()
    {
        var rig = new Rig(Line());
        rig.Follow.Scale = new Vector3(2, 2, 2);
        rig.Follow.Progress = 4f;
        Assert.Equal(4f, rig.Follow.Position.X, 3);
        Assert.Equal(1f, (-rig.Follow.Transform.Basis.Z / 2).X, 3); // forward = -Z along +X
        Assert.Equal(new Vector3(2, 2, 2), rig.Follow.Scale);
        Assert.Equal(0.4f, rig.Follow.ProgressRatio, 3);
        rig.Follow.ProgressRatio = 0.75f;
        Assert.Equal(7.5f, rig.Follow.Position.X, 3);
    }

    [Fact]
    public void LoopWrapsAndClampWithoutIt()
    {
        var rig = new Rig(Line());
        rig.Follow.Progress = 12f;
        Assert.Equal(2f, rig.Follow.Progress, 3);
        rig.Follow.Progress = -1f;
        Assert.Equal(9f, rig.Follow.Progress, 3);
        rig.Follow.Loop = false;
        rig.Follow.Progress = 12f;
        Assert.Equal(10f, rig.Follow.Progress, 3);
    }

    [Fact]
    public void OffsetsFollowTheLocalFrame()
    {
        var rig = new Rig(Line());
        rig.Follow.HOffset = 1f;
        rig.Follow.VOffset = 2f;
        rig.Follow.Progress = 5f;
        // Forward +X, so local X (right) is +Z and local Y is +Y.
        Assert.Equal(5f, rig.Follow.Position.X, 3);
        Assert.Equal(2f, rig.Follow.Position.Y, 3);
        Assert.Equal(1f, rig.Follow.Position.Z, 3);
    }

    [Fact]
    public void RotationModes()
    {
        var rig = new Rig(LineAndClimb());
        var follow = rig.Follow;
        follow.CubicInterp = false;
        follow.Progress = 15f; // on the climb: the tangent is +Y

        follow.RotationMode = PathRotationMode.XY;
        Assert.Equal(1f, (-follow.Transform.Basis.Z).Y, 3);

        follow.RotationMode = PathRotationMode.Y; // no horizontal direction: identity
        Assert.Equal(Basis.Identity.Z.Z, follow.Transform.Basis.Z.Z, 3);

        follow.Progress = 5f;
        Assert.Equal(1f, (-follow.Transform.Basis.Z).X, 3);

        follow.RotationMode = PathRotationMode.None;
        follow.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f);
        follow.Progress = 2f;
        Assert.Equal(0.3f, 2 * MathF.Acos(follow.Rotation.W), 3);
    }

    [Fact]
    public void XyzRollsWithTilt()
    {
        var curve = Line();
        curve.SetPointTilt(0, MathF.PI / 2);
        curve.SetPointTilt(1, MathF.PI / 2);
        var rig = new Rig(curve);
        rig.Follow.Progress = 5f;
        Assert.Equal(0f, rig.Follow.Transform.Basis.Y.Y, 3);
        rig.Follow.TiltEnabled = false;
        Assert.Equal(1f, rig.Follow.Transform.Basis.Y.Y, 3);
    }

    [Fact]
    public void OrientedUsesTheUpVectors()
    {
        var curve = new Curve3D { BakeInterval = 0.05f, UpVectorEnabled = true };
        curve.SetSmoothPoints([new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0)], closed: false);
        var rig = new Rig(curve);
        rig.Follow.RotationMode = PathRotationMode.Oriented;
        var length = curve.GetBakedLength();
        var previous = Vector3.UnitY;
        for (var d = 0f; d <= length; d += 0.25f)
        {
            rig.Follow.Progress = d;
            var up = rig.Follow.Transform.Basis.Y;
            Assert.True(Vector3.Dot(up, previous) > 0.95f, $"up flips at {d}");
            previous = up;
        }
    }

    [Fact]
    public void SpeedAdvancesAndLoops()
    {
        var rig = new Rig(Line());
        var loops = 0;
        rig.Follow.Looped += () => loops++;
        rig.Follow.Speed = 6f; // 0.1 m per frame
        rig.Tick(30);
        Assert.Equal(3f, rig.Follow.Progress, 2);
        rig.Tick(90);
        Assert.Equal(2f, rig.Follow.Progress, 2);
        Assert.Equal(1, loops);
    }

    [Fact]
    public void SpeedWithoutLoopStopsOrPingPongs()
    {
        var rig = new Rig(Line());
        var ends = 0;
        rig.Follow.EndReached += () => ends++;
        rig.Follow.Loop = false;
        rig.Follow.Speed = 60f; // 1 m per frame
        rig.Tick(15);
        Assert.Equal(10f, rig.Follow.Progress, 3);
        Assert.Equal(1, ends);

        rig.Follow.Progress = 8f;
        rig.Follow.PingPong = true;
        rig.Tick(3); // 9, 10, then turns back to 9
        Assert.Equal(9f, rig.Follow.Progress, 3);
        Assert.Equal(2, ends);
        rig.Tick(2);
        Assert.Equal(7f, rig.Follow.Progress, 3);
    }

    [Fact]
    public void PhysicsCallbackAdvancesOnTheFixedTick()
    {
        var rig = new Rig(Line(100));
        rig.Follow.ProcessCallback = PathProcessCallback.Physics;
        rig.Follow.Speed = 60f;
        rig.Tick(10);
        Assert.Equal(10f, rig.Follow.Progress, 2);
    }

    [Fact]
    public void ChangingTheCurveReplacesTheFollower()
    {
        var rig = new Rig(Line());
        rig.Follow.Progress = 5f;
        rig.Path.Curve!.SetPointPosition(1, new Vector3(0, 0, 10));
        Assert.Equal(5f, rig.Follow.Position.Z, 3);
        rig.Path.Curve = LineAndClimb();
        Assert.Equal(5f, rig.Follow.Position.X, 3);
    }

    [Fact]
    public void FollowersRoundTripThroughAScene()
    {
        var root = new Node3D { Name = "Root" };
        var path = new Path3D { Name = "Path", Curve = Line() };
        var follow = new PathFollow3D { Name = "Follow", Speed = 4f, Loop = false, RotationMode = PathRotationMode.Y, HOffset = 1.5f };
        root.AddChild(path);
        path.AddChild(follow);
        path.Owner = root;
        follow.Owner = root;
        var copy = PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate().GetNode<PathFollow3D>("Path/Follow");
        Assert.Equal(4f, copy.Speed);
        Assert.False(copy.Loop);
        Assert.Equal(PathRotationMode.Y, copy.RotationMode);
        Assert.Equal(1.5f, copy.HOffset);
    }

    [Fact]
    public void FollowingAllocatesNothing()
    {
        var curve = new Curve3D { UpVectorEnabled = true };
        curve.SetSmoothPoints([new(0, 0, 0), new(30, 5, 0), new(30, 0, 30), new(0, 5, 30)], closed: true);
        var rig = new Rig(curve);
        for (var i = 0; i < 16; i++)
            rig.Path.AddChild(new PathFollow3D { Speed = 3 + i, RotationMode = (PathRotationMode)(i % 5), Progress = i * 7 });
        rig.Tick(10);
        var allocated = AllocationGate.SmallestWindow(() => rig.Tick(60));
        Assert.Equal(0, allocated);
    }
}
