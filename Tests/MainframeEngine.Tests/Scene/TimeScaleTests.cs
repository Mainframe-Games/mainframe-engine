using System.Numerics;
using MainframeEngine.Tests.Physics;

namespace MainframeEngine.Tests.Scene;

/// <summary>
/// <see cref="SceneTree.TimeScale"/> (Godot's <c>Engine.time_scale</c>) and <c>--time-scale</c>: process and physics
/// deltas, timers, tweens, bodies and characters run at the scale; the number of physics steps per real second does not.
/// </summary>
[Collection(nameof(SerialBox2D))]
public sealed class TimeScaleTests : IDisposable
{
    private const float Frame = 1f / 60f;
    private readonly PhysicsHarness2D _h = new();

    public void Dispose() => _h.Dispose();

    private sealed class DeltaProbe : Node
    {
        public float Process, Physics;

        protected override void OnProcess(in GameTime gameTime) => Process = gameTime.DeltaTime;

        protected override void OnPhysicsProcess(float delta) => Physics = delta;
    }

    [Fact]
    public void ProcessAndPhysicsDeltasScaleButThePhysicsStepCountDoesNot()
    {
        var probe = _h.Add(new DeltaProbe());
        _h.Tree.TimeScale = 8;
        var steps = _h.Tree.PhysicsFrames;
        _h.Run(15, Frame); // 0.25 s of real time

        Assert.Equal(steps + 15, _h.Tree.PhysicsFrames); // still 60 steps per real second
        Assert.Equal(8 * Frame, probe.Process, 5);
        Assert.Equal(8 * Frame, probe.Physics, 5);
        Assert.Equal(8 * Frame, _h.Tree.ProcessDeltaTime, 5);
        Assert.Equal(8 * Frame, _h.Tree.PhysicsDeltaTime, 5);
    }

    [Fact]
    public void ATwoSecondTimerFiresAfterAQuarterSecondAtEightTimesAndATweenKeepsPace()
    {
        _h.Tree.TimeScale = 8;
        var fired = 0;
        _h.Tree.CreateTimer(2f).Timeout += () => fired++;
        var node = _h.Add(new Timer { WaitTime = 2f, OneShot = true });
        var nodeFired = 0;
        node.Timeout += () => nodeFired++;
        node.Start();
        var tweened = _h.Add(new Node2D());
        _h.Tree.CreateTween().TweenProperty(tweened, nameof(Node2D.Position), new Vector2(100, 0), 2f);

        _h.Run(14, Frame); // 14/60 s real = 1.87 s game
        Assert.Equal(0, fired);
        Assert.Equal(0, nodeFired);
        Assert.InRange(tweened.Position.X, 90f, 95f); // 1.87 of 2 s along a linear tween

        _h.Run(2, Frame); // just past 0.25 s real = 2 s game (the countdowns subtract floats: allow one more frame)
        Assert.Equal(1, fired);
        Assert.Equal(1, nodeFired);
        Assert.Equal(100f, tweened.Position.X, 3);
    }

    [Fact]
    public void BodiesAndCharactersMoveEightTimesAsFarPerFrame()
    {
        var falling = _h.AddBox(new Vector2(0, -100000));
        falling.LinearDamp = 0;
        falling.PhysicsInterpolation = false;
        var character = _h.Add(new CharacterBody2D { Name = "Walker", Position = new Vector2(5000, 0), MotionMode = CharacterMotionMode2D.Floating, PhysicsInterpolation = false });
        character.AddChild(new CollisionShape2D { Shape = new CircleShape2D { Radius = 10 } });
        var walker = _h.Add(new MoveEveryStep(character, new Vector2(100, 0)));
        _h.Run(2, Frame); // let both settle into the space
        var start = character.Position.X;

        _h.Tree.TimeScale = 8;
        _h.Run(15, Frame); // 0.25 s real = 2 s game
        Assert.InRange(falling.LinearVelocity.Y, 980 * 2 * 0.95f, 980 * 2.1f + 980 * 2 * Frame); // g × ~2 s of game time
        Assert.InRange(character.Position.X - start, 195f, 205f); // 100 px/s for 2 s of game time
        Assert.NotNull(walker);
    }

    private sealed class MoveEveryStep(CharacterBody2D body, Vector2 velocity) : Node
    {
        protected override void OnPhysicsProcess(float delta)
        {
            body.Velocity = velocity;
            body.MoveAndSlide();
        }
    }

    [Fact]
    public void TimeScaleZeroFreezesGameTimeAndBadValuesAreRejected()
    {
        var box = _h.AddBox(new Vector2(0, -1000));
        box.PhysicsInterpolation = false;
        var fired = 0;
        _h.Run(1, Frame);
        var held = box.Position;
        _h.Tree.TimeScale = 0;
        _h.Tree.CreateTimer(0.01f).Timeout += () => fired++;
        _h.Run(30, Frame);
        Assert.Equal(held, box.Position);
        Assert.Equal(0, fired);

        Assert.Throws<ArgumentOutOfRangeException>(() => _h.Tree.TimeScale = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => _h.Tree.TimeScale = float.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => _h.Tree.TimeScale = float.PositiveInfinity);
    }

    [Fact]
    public void TheTimeScaleFlagReachesTheEngineOptions()
    {
        var options = GameHostOptions.Parse(["--time-scale", "8", "--fixed-fps", "60"]);
        Assert.Equal(8f, options.TimeScale);
        var engine = options.Apply(new EngineOptions { GameName = "Test" });
        Assert.Equal(8f, engine.TimeScale);
        Assert.Equal(1f / 60, engine.FixedDeltaTime);
        Assert.Equal(1f, GameHostOptions.Parse([]).Apply(new EngineOptions { GameName = "Test" }).TimeScale);
        Assert.Throws<ArgumentException>(() => GameHostOptions.Parse(["--time-scale", "-2"]));
        Assert.Throws<ArgumentException>(() => GameHostOptions.Parse(["--time-scale", "fast"]));
    }
}
