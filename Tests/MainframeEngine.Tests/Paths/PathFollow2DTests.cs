using System.Numerics;

namespace MainframeEngine.Tests.Paths;

/// <summary><see cref="Path2D"/> and <see cref="PathFollow2D"/> in a headless tree (G13).</summary>
public sealed class PathFollow2DTests
{
    private const float Step = 1f / 60f;

    private sealed class Rig
    {
        private uint _frame;

        public Rig(Curve2D curve)
        {
            Tree = new SceneTree(new ServerRegistry());
            var scene = new Node2D { Name = "Scene" };
            Tree.ChangeScene(scene);
            Path = new Path2D { Name = "Path", Curve = curve };
            Follow = new PathFollow2D { Name = "Follow" };
            Path.AddChild(Follow);
            scene.AddChild(Path);
        }

        public SceneTree Tree { get; }

        public Path2D Path { get; }

        public PathFollow2D Follow { get; }

        public void Tick(int frames = 1)
        {
            for (var i = 0; i < frames; i++)
                Tree.Tick(new GameTime { DeltaTime = Step, FrameCount = ++_frame });
        }
    }

    // 100 px along +X, then 100 px down (+Y), open.
    private static Curve2D LineAndTurn()
    {
        var curve = new Curve2D { BakeInterval = 0.5f };
        curve.AddPoint(Vector2.Zero);
        curve.AddPoint(new Vector2(100, 0));
        curve.AddPoint(new Vector2(100, 100));
        return curve;
    }

    private static Curve2D Line(float length = 100f)
    {
        var curve = new Curve2D { BakeInterval = 0.5f };
        curve.AddPoint(Vector2.Zero);
        curve.AddPoint(new Vector2(length, 0));
        return curve;
    }

    [Fact]
    public void ProgressPlacesTheFollowerAndKeepsItsScale()
    {
        var rig = new Rig(LineAndTurn());
        rig.Follow.Scale = new Vector2(2, 2);
        rig.Follow.CubicInterp = false;
        rig.Follow.Progress = 40f;
        Assert.Equal(new Vector2(40, 0), rig.Follow.Position);
        Assert.Equal(0f, rig.Follow.Rotation, 3);
        rig.Follow.Progress = 150f;
        Assert.Equal(100f, rig.Follow.Position.X, 3);
        Assert.Equal(50f, rig.Follow.Position.Y, 3);
        Assert.Equal(MathF.PI / 2, rig.Follow.Rotation, 3); // +X along the curve (down)
        Assert.Equal(new Vector2(2, 2), rig.Follow.Scale);
        Assert.Equal(0.75f, rig.Follow.ProgressRatio, 3);
        rig.Follow.ProgressRatio = 0.25f;
        Assert.Equal(50f, rig.Follow.Position.X, 3);
    }

    [Fact]
    public void LoopWrapsAndClampWithoutIt()
    {
        var rig = new Rig(Line());
        rig.Follow.Progress = 120f;
        Assert.Equal(20f, rig.Follow.Progress, 3);
        rig.Follow.Progress = -10f;
        Assert.Equal(90f, rig.Follow.Progress, 3);
        rig.Follow.Loop = false;
        rig.Follow.Progress = 120f;
        Assert.Equal(100f, rig.Follow.Progress, 3);
    }

    [Fact]
    public void OffsetsFollowTheCurveOrThePathWithoutRotation()
    {
        var rig = new Rig(LineAndTurn());
        rig.Follow.CubicInterp = false;
        rig.Follow.HOffset = 5f;
        rig.Follow.VOffset = 10f;
        rig.Follow.Progress = 150f; // heading down: local X is +Y, local Y is -X
        Assert.Equal(90f, rig.Follow.Position.X, 3);
        Assert.Equal(55f, rig.Follow.Position.Y, 3);

        rig.Follow.Rotates = false;
        rig.Follow.Rotation = 0.3f;
        rig.Follow.Progress = 150f;
        Assert.Equal(new Vector2(105, 60), rig.Follow.Position);
        Assert.Equal(0.3f, rig.Follow.Rotation);
    }

    [Fact]
    public void SpeedAdvancesAndLoops()
    {
        var rig = new Rig(Line());
        var loops = 0;
        rig.Follow.Looped += () => loops++;
        rig.Follow.Speed = 60f; // 1 px per frame
        rig.Tick(30);
        Assert.Equal(30f, rig.Follow.Progress, 2);
        rig.Tick(90);
        Assert.Equal(20f, rig.Follow.Progress, 2);
        Assert.Equal(1, loops);
    }

    [Fact]
    public void SpeedWithoutLoopStopsOrPingPongs()
    {
        var rig = new Rig(Line());
        var ends = 0;
        rig.Follow.EndReached += () => ends++;
        rig.Follow.Loop = false;
        rig.Follow.Speed = 600f; // 10 px per frame
        rig.Tick(15);
        Assert.Equal(100f, rig.Follow.Progress, 3);
        Assert.Equal(1, ends);

        rig.Follow.Progress = 80f;
        rig.Follow.PingPong = true;
        rig.Tick(3); // 90, 100, then turns back to 90
        Assert.Equal(90f, rig.Follow.Progress, 3);
        Assert.Equal(2, ends);
        rig.Tick(2);
        Assert.Equal(70f, rig.Follow.Progress, 3);
    }

    [Fact]
    public void PhysicsCallbackAdvancesOnTheFixedTick()
    {
        var rig = new Rig(Line(1000));
        rig.Follow.ProcessCallback = PathProcessCallback.Physics;
        rig.Follow.Speed = 600f;
        rig.Tick(10);
        Assert.Equal(100f, rig.Follow.Progress, 2);
    }

    [Fact]
    public void ChangingTheCurveReplacesTheFollower()
    {
        var rig = new Rig(Line());
        rig.Follow.Progress = 50f;
        rig.Path.Curve!.SetPointPosition(1, new Vector2(0, 100));
        Assert.Equal(50f, rig.Follow.Position.Y, 3);
        rig.Path.Curve = LineAndTurn();
        Assert.Equal(50f, rig.Follow.Position.X, 3);
    }

    [Fact]
    public void AFollowerOutsideAPathStaysPut()
    {
        var tree = new SceneTree(new ServerRegistry());
        var scene = new Node2D { Name = "Scene" };
        tree.ChangeScene(scene);
        var follow = new PathFollow2D { Name = "Lost", Position = new Vector2(3, 4), Speed = 100 };
        scene.AddChild(follow);
        follow.Progress = 50f;
        tree.Tick(new GameTime { DeltaTime = Step, FrameCount = 1 });
        Assert.Equal(new Vector2(3, 4), follow.Position);
    }

    [Fact]
    public void FollowersRoundTripThroughAScene()
    {
        var root = new Node2D { Name = "Root" };
        var path = new Path2D { Name = "Path", Curve = Line() };
        var follow = new PathFollow2D { Name = "Follow", Speed = 40f, Loop = false, Rotates = false, HOffset = 1.5f, PingPong = true };
        root.AddChild(path);
        path.AddChild(follow);
        path.Owner = root;
        follow.Owner = root;
        var copy = PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate().GetNode<PathFollow2D>("Path/Follow");
        Assert.Equal(40f, copy.Speed);
        Assert.False(copy.Loop);
        Assert.False(copy.Rotates);
        Assert.True(copy.PingPong);
        Assert.Equal(1.5f, copy.HOffset);
    }

    [Fact]
    public void FollowingAllocatesNothing()
    {
        var curve = new Curve2D();
        curve.SetSmoothPoints([new(0, 0), new(300, 50), new(300, 300), new(0, 250)], closed: true);
        var rig = new Rig(curve);
        for (var i = 0; i < 16; i++)
            rig.Path.AddChild(new PathFollow2D { Speed = 30 + i, Rotates = i % 2 == 0, Progress = i * 70, ProcessCallback = (PathProcessCallback)(i % 2) });
        rig.Tick(10);
        var allocated = AllocationGate.SmallestWindow(() => rig.Tick(60));
        Assert.Equal(0, allocated);
    }
}
