using System.Numerics;
using DrawingColor = System.Drawing.Color;

namespace MainframeEngine.Tests.Animation;

public enum LampState
{
    Off,
    Dim,
    On,
}

/// <summary>A node with one animatable property of each kind and [AnimationMethod]s (G1a tests).</summary>
public sealed class Lamp : Node3D
{
    [Export] public float Energy { get; set; }

    [Export] public DrawingColor Tint { get; set; } = DrawingColor.White;

    [Export] public bool Lit { get; set; }

    [Export] public int Flicks { get; set; }

    [Export] public LampState State { get; set; }

    [Export] public Vector2 Offset { get; set; }

    [Export] public string Label { get; set; } = "";

    public List<string> Calls { get; } = [];

    [AnimationMethod]
    public void Open() => Calls.Add("Open");

    [AnimationMethod]
    public void Flash(int times, string colour) => Calls.Add($"Flash {times} {colour}");

    [AnimationMethod]
    public void Hum(float level) => Calls.Add($"Hum {level.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
}

/// <summary>G1.1–G1.4: bindings, value/bezier tracks, interpolation modes, method and audio keys, blending (proposal § Testing).</summary>
public sealed class ValueTrackTests
{
    private const float Step = 1f / 60f;

    private sealed class Rig
    {
        public Rig()
        {
            Tree = new SceneTree(new ServerRegistry());
            Tree.ChangeScene(Scene);
            Scene.AddChild(Lamp);
            Scene.AddChild(Player);
            Player.CallbackModeProcess = AnimationCallbackMode.Manual;
        }

        public SceneTree Tree { get; }

        public Node3D Scene { get; } = new() { Name = "Scene" };

        public Lamp Lamp { get; } = new() { Name = "Lamp" };

        public AnimationPlayer Player { get; } = new() { Name = "Player" };

        public MainframeEngine.Animation Add(string name, float length, AnimationLoopMode loop, params AnimationTrack[] tracks)
        {
            var animation = new MainframeEngine.Animation { ResourceName = name, Length = length, LoopMode = loop };
            foreach (var t in tracks)
                animation.AddTrack(t);
            if (Player.GetAnimationLibrary("") is not { } library)
                Player.AddAnimationLibrary("", library = new AnimationLibrary());
            library.Animations.Add(animation);
            return animation;
        }
    }

    private static ValueTrack Value(string property, int components, float[] times, float[] values,
        AnimationInterpolation interpolation = AnimationInterpolation.Linear) =>
        new() { Path = "Lamp", Property = property, Components = components, Times = times, Values = values, Interpolation = interpolation };

    // ── Bindings ──

    [Fact]
    public void BindingsReadAndWriteEveryKindWithoutBoxing()
    {
        var lamp = new Lamp { Energy = 2f, Offset = new Vector2(3, 4), State = LampState.Dim, Tint = DrawingColor.FromArgb(255, 255, 0, 0) };
        Span<float> one = stackalloc float[4];

        var energy = PropertyBinding.Create(lamp, "Energy", out _)!;
        Assert.Equal(1, energy.Components);
        energy.Read(one);
        Assert.Equal(2f, one[0]);
        energy.Write([5f]);
        Assert.Equal(5f, lamp.Energy);

        var y = PropertyBinding.Create(lamp, "Offset:y", out _)!;
        Assert.Equal(1, y.Components);
        y.Write([9f]);
        Assert.Equal(new Vector2(3, 9), lamp.Offset);

        var alpha = PropertyBinding.Create(lamp, "Tint:a", out _)!;
        alpha.Write([0.5f]);
        Assert.Equal(128, lamp.Tint.A);
        Assert.Equal(255, lamp.Tint.R);

        var state = PropertyBinding.Create(lamp, "State", out _)!;
        Assert.True(state.Discrete);
        state.Read(one);
        Assert.Equal(1f, one[0]);
        state.Write([2f]);
        Assert.Equal(LampState.On, lamp.State);

        var lit = PropertyBinding.Create(lamp, "Lit", out _)!;
        Assert.True(lit.Discrete);
        lit.Write([1f]);
        Assert.True(lamp.Lit);

        Assert.Null(PropertyBinding.Create(lamp, "Label", out var problem));
        Assert.Contains("cannot be animated", problem, StringComparison.Ordinal);
        Assert.Null(PropertyBinding.Create(lamp, "Missing", out problem));
        Assert.Null(PropertyBinding.Create(lamp, "Offset:q", out problem));
        Assert.Null(PropertyBinding.Create(lamp, "Energy:z", out _)); // a float has no z

        var allocated = AllocationGate.SmallestWindow(() =>
        {
            Span<float> v = stackalloc float[1];
            for (var i = 0; i < 100; i++)
            {
                v[0] = i;
                energy.Write(v);
                y.Write(v);
                state.Write(v[..1]);
            }
        });
        Assert.Equal(0, allocated);
    }

    // ── Tracks ──

    [Fact]
    public void ValueTracksInterpolateNearestLinearCubicAndWithTransitions()
    {
        var times = new[] { 0f, 1f, 2f, 3f };
        var values = new[] { 0f, 10f, 0f, 10f };
        Assert.Equal(5f, Value("Energy", 1, times, values).Sample(0.5)[0], 3);
        Assert.Equal(0f, Value("Energy", 1, times, values, AnimationInterpolation.Nearest).Sample(0.9)[0]);
        var cubic = Value("Energy", 1, times, values, AnimationInterpolation.Cubic).Sample(1.5)[0];
        Assert.Equal(5f, cubic, 3); // symmetric segment: halfway
        var eased = Value("Energy", 1, times, values);
        eased.Transitions = [2f, 1f, 1f, 1f]; // ease in
        Assert.True(eased.Sample(0.5)[0] < 5f);
        Assert.Equal(10f, Value("Energy", 1, times, values).Sample(9)[0]); // past the last key: held
    }

    [Fact]
    public void QuaternionCubicPassesThroughTheKeys()
    {
        var track = new Rotation3DTrack
        {
            Times = [0, 1, 2],
            Values = [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 2f)],
            Interpolation = AnimationInterpolation.Cubic,
        };
        var half = track.Sample(0.5);
        Assert.Equal(0.5f, 2 * MathF.Acos(Math.Clamp(half.W, -1f, 1f)), 2);
        Assert.Equal(1f, 2 * MathF.Acos(Math.Clamp(track.Sample(1.0).W, -1f, 1f)), 3);
    }

    [Fact]
    public void LoopWrapInterpolatesFromTheLastKeyToTheFirst()
    {
        var rig = new Rig();
        var track = Value("Energy", 1, [0f, 1f], [0f, 10f]);
        rig.Add("loop", 2f, AnimationLoopMode.Linear, track);
        rig.Player.Play("loop");
        rig.Player.Seek(1.5, update: true);
        Assert.Equal(5f, rig.Lamp.Energy, 3); // halfway from 10 (at 1 s) back to 0 (at 2 s = 0 s)
        var hold = Value("Energy", 1, [0f, 1f], [0f, 10f]);
        hold.LoopWrap = false;
        rig.Add("hold", 2f, AnimationLoopMode.Linear, hold);
        rig.Player.Play("hold");
        rig.Player.Seek(1.5, update: true);
        Assert.Equal(10f, rig.Lamp.Energy, 3);
    }

    [Fact]
    public void DiscreteValuesChangeOnlyAtKeys()
    {
        var rig = new Rig();
        rig.Add("states", 3f, AnimationLoopMode.None,
            Value("State", 1, [0f, 1f, 2f], [0f, 2f, 1f]),
            Value("Flicks", 1, [0f, 2f], [0f, 10f]),
            new ValueTrack { Path = "Lamp", Property = "Energy", Times = [0f, 2f], Values = [0f, 10f], Update = AnimationUpdateMode.Discrete });
        rig.Player.Play("states");
        rig.Player.Seek(0.9, update: true);
        Assert.Equal(LampState.Off, rig.Lamp.State);
        Assert.Equal(0, rig.Lamp.Flicks);
        Assert.Equal(0f, rig.Lamp.Energy);
        rig.Player.Seek(1.2, update: true);
        Assert.Equal(LampState.On, rig.Lamp.State);
        rig.Player.Seek(2.1, update: true);
        Assert.Equal(10, rig.Lamp.Flicks);
        Assert.Equal(10f, rig.Lamp.Energy);
    }

    [Fact]
    public void ColourAndSubComponentTracksAnimate()
    {
        var rig = new Rig();
        rig.Add("fade", 1f, AnimationLoopMode.None,
            Value("Tint", 4, [0f, 1f], [1f, 0f, 0f, 1f, 0f, 0f, 1f, 1f]),
            Value("Offset:x", 1, [0f, 1f], [0f, 8f]));
        rig.Player.Play("fade");
        rig.Player.Seek(0.5, update: true);
        Assert.InRange(rig.Lamp.Tint.R, 126, 129);
        Assert.InRange(rig.Lamp.Tint.B, 126, 129);
        Assert.Equal(4f, rig.Lamp.Offset.X, 3);
    }

    [Fact]
    public void BezierTracksFollowTheirHandles()
    {
        var track = new BezierTrack
        {
            Path = "Lamp",
            Property = "Energy",
            Times = [0f, 1f],
            Values = [0f, 10f],
            OutHandles = [new Vector2(0.3f, 0f), Vector2.Zero],
            InHandles = [Vector2.Zero, new Vector2(-0.3f, 0f)]
        };
        Assert.Equal(0f, track.Sample(0), 3);
        Assert.Equal(10f, track.Sample(1), 3);
        Assert.Equal(5f, track.Sample(0.5), 2); // symmetric handles: halfway in time is halfway in value
        Assert.True(track.Sample(0.1) < 1f); // flat handles ease in
        var rig = new Rig();
        rig.Add("bez", 1f, AnimationLoopMode.None, track);
        rig.Player.Play("bez");
        rig.Player.Seek(1, update: true);
        Assert.Equal(10f, rig.Lamp.Energy, 3);
    }

    [Fact]
    public void UnsupportedPropertiesWarnAndAreSkipped()
    {
        var rig = new Rig();
        rig.Add("bad", 1f, AnimationLoopMode.None, Value("Label", 1, [0f], [1f]), Value("Energy", 1, [0f, 1f], [0f, 4f]));
        rig.Player.Play("bad");
        rig.Player.Seek(1, update: true);
        Assert.Equal(4f, rig.Lamp.Energy, 3);
    }

    // ── Method and audio keys ──

    private static MethodTrack Methods(float[] times, string[] methods, string[] arguments) =>
        new() { Path = "Lamp", Times = times, Methods = methods, Arguments = arguments };

    [Fact]
    public void MethodKeysCallTheirMethodsWithParsedArgumentsInPlayDirection()
    {
        var rig = new Rig();
        rig.Add("door", 2f, AnimationLoopMode.None,
            Methods([0.5f, 1f, 1.5f], ["Open", "Flash", "Hum"], ["[]", "[3, \"red\"]", "[0.25]"]));
        rig.Player.Play("door");
        rig.Player.Advance(1.2);
        Assert.Equal(["Open", "Flash 3 red"], rig.Lamp.Calls);
        rig.Player.Advance(1);
        Assert.Equal(["Open", "Flash 3 red", "Hum 0.25"], rig.Lamp.Calls);

        rig.Lamp.Calls.Clear();
        rig.Player.PlayBackwards("door");
        rig.Player.Advance(2);
        Assert.Equal(["Hum 0.25", "Flash 3 red", "Open"], rig.Lamp.Calls);
    }

    [Fact]
    public void MethodKeysFireAcrossLoopsAndPingPongOncePerPass()
    {
        var rig = new Rig();
        rig.Add("loop", 1f, AnimationLoopMode.Linear, Methods([0.25f], ["Open"], ["[]"]));
        rig.Player.Play("loop");
        rig.Player.Advance(2.5); // passes 0.25, 1.25, 2.25
        Assert.Equal(3, rig.Lamp.Calls.Count);

        rig.Lamp.Calls.Clear();
        rig.Add("pong", 1f, AnimationLoopMode.PingPong, Methods([0.75f], ["Open"], ["[]"]));
        rig.Player.Play("pong");
        rig.Player.Advance(2); // forward past 0.75, back past 0.75
        Assert.Equal(2, rig.Lamp.Calls.Count);
    }

    [Fact]
    public void ImmediateCallsHappenBeforeValuesApplyDeferredAfter()
    {
        var rig = new Rig();
        var seen = new List<float>();
        var lamp = rig.Lamp;
        rig.Add("both", 1f, AnimationLoopMode.None,
            Value("Energy", 1, [0f, 1f], [0f, 10f]),
            Methods([0.5f], ["Open"], ["[]"]));
        lamp.Calls.Clear();
        rig.Player.CallbackModeMethod = AnimationMethodCallMode.Deferred;
        rig.Player.Play("both");
        rig.Player.Advance(0.6);
        Assert.Single(lamp.Calls);
        Assert.Equal(6f, lamp.Energy, 3);
        rig.Player.CallbackModeMethod = AnimationMethodCallMode.Immediate;
        rig.Player.Stop();
        rig.Player.Play("both");
        rig.Player.Advance(0.6);
        Assert.Equal(2, lamp.Calls.Count);
        Assert.Empty(seen);
    }

    [Fact]
    public void BadMethodKeysWarnAndAreSkipped()
    {
        var rig = new Rig();
        rig.Add("bad", 1f, AnimationLoopMode.None,
            Methods([0.1f, 0.2f, 0.3f], ["Nope", "Flash", "Open"], ["[]", "[1]", "[]"]));
        rig.Player.Play("bad");
        rig.Player.Advance(1);
        Assert.Equal(["Open"], rig.Lamp.Calls); // unknown method and wrong argument count skipped
    }

    [Fact]
    public void AudioKeysPlayTheirStreamOnTheTarget()
    {
        var rig = new Rig();
        var speaker = new AudioPlayer3D { Name = "Speaker" };
        rig.Scene.AddChild(speaker);
        var chime = new AudioStream();
        rig.Add("chime", 1f, AnimationLoopMode.None,
            new AudioTrack { Path = "Speaker", Times = [0.5f], Streams = [chime], StartOffsets = [0.1f] });
        rig.Player.Play("chime");
        rig.Player.Advance(0.6);
        Assert.Same(chime, speaker.Stream);
    }

    // ── Player ──

    [Fact]
    public void ValueCrossfadesMixAndBlendTimesPick()
    {
        var rig = new Rig();
        rig.Add("low", 1f, AnimationLoopMode.Linear, Value("Energy", 1, [0f], [0f]));
        rig.Add("high", 1f, AnimationLoopMode.Linear, Value("Energy", 1, [0f], [10f]));
        rig.Player.BlendTimes.Add(new AnimationBlendTime { From = "low", To = "high", Seconds = 1f });
        rig.Player.Play("low");
        rig.Player.Advance(0.1);
        rig.Player.Play("high");
        rig.Player.Advance(0.5);
        Assert.Equal(5f, rig.Lamp.Energy, 2);
        rig.Player.Advance(0.6);
        Assert.Equal(10f, rig.Lamp.Energy, 3);

        rig.Player.Play("low"); // no entry for high → low: the default (0) switches at once
        Assert.Equal(0f, rig.Lamp.Energy, 3);
    }

    [Fact]
    public void SyncStateRestoresAnimationAndPosition()
    {
        var a = new Rig();
        a.Add("glow", 4f, AnimationLoopMode.None, Value("Energy", 1, [0f, 4f], [0f, 8f]));
        a.Player.Play("glow");
        a.Player.Advance(1.5);
        var state = a.Player.GetSyncState();
        Assert.Equal(new AnimationSyncState("glow", 1.5, true), state);

        var b = new Rig();
        b.Add("glow", 4f, AnimationLoopMode.None, Value("Energy", 1, [0f, 4f], [0f, 8f]));
        b.Player.ApplySyncState(state);
        Assert.Equal(3f, b.Lamp.Energy, 3);
        Assert.True(b.Player.IsPlaying());
    }

    [Fact]
    public void AnimationsRoundTripThroughAResourceFile()
    {
        var animation = new MainframeEngine.Animation { ResourceName = "all", Length = 3f, LoopMode = AnimationLoopMode.PingPong };
        animation.AddTrack(Value("Tint", 4, [0f, 1f], [1, 0, 0, 1, 0, 0, 1, 1], AnimationInterpolation.Cubic));
        animation.AddTrack(new BezierTrack { Path = "Lamp", Property = "Energy", Times = [0f, 1f], Values = [0f, 1f], InHandles = [new(-0.1f, 0f), new(-0.2f, 0.5f)], OutHandles = [new(0.2f, 0f), Vector2.Zero] });
        animation.AddTrack(Methods([1f], ["Flash"], ["[2, \"blue\"]"]));
        animation.AddTrack(new Position3DTrack { Path = "Lamp", Times = [0f, 1f], Values = [Vector3.Zero, Vector3.One], Transitions = [0.5f, 1f], LoopWrap = false });
        var root = new Node3D { Name = "Root" };
        var player = new AnimationPlayer { Name = "Player" };
        var library = new AnimationLibrary();
        library.Animations.Add(animation);
        player.AddAnimationLibrary("", library);
        root.AddChild(player);
        player.Owner = root;
        var copy = PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate().GetNode<AnimationPlayer>("Player").GetAnimation("all")!;
        Assert.Equal(4, copy.Tracks.Count);
        var value = Assert.IsType<ValueTrack>(copy.Tracks[0]);
        Assert.Equal(AnimationInterpolation.Cubic, value.Interpolation);
        Assert.Equal(4, value.Components);
        Assert.Equal(new Vector2(-0.2f, 0.5f), Assert.IsType<BezierTrack>(copy.Tracks[1]).InHandles[1]);
        Assert.Equal("[2, \"blue\"]", Assert.IsType<MethodTrack>(copy.Tracks[2]).Arguments[0]);
        var position = Assert.IsType<Position3DTrack>(copy.Tracks[3]);
        Assert.False(position.LoopWrap);
        Assert.Equal([0.5f, 1f], position.Transitions);
    }

    [Fact]
    public void AThousandAnimatedNodesAllocateNothingPerFrame()
    {
        var tree = new SceneTree(new ServerRegistry());
        var scene = new Node3D { Name = "Scene" };
        tree.ChangeScene(scene);
        var walk = new MainframeEngine.Animation { ResourceName = "walk", Length = 2f, LoopMode = AnimationLoopMode.Linear };
        var glow = new MainframeEngine.Animation { ResourceName = "glow", Length = 2f, LoopMode = AnimationLoopMode.Linear };
        for (var i = 0; i < 1000; i++)
        {
            var path = $"Lamp{i}";
            scene.AddChild(new Lamp { Name = path });
            walk.AddTrack(new Position3DTrack { Path = path, Times = [0f, 1f], Values = [Vector3.Zero, Vector3.One] });
            walk.AddTrack(new Rotation3DTrack { Path = path, Times = [0f, 1f], Values = [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f)] });
            walk.AddTrack(new ValueTrack { Path = path, Property = "Tint", Components = 4, Times = [0f, 1f], Values = [1, 0, 0, 1, 0, 1, 0, 1] });
            walk.AddTrack(new ValueTrack { Path = path, Property = "Offset:y", Times = [0f, 1f], Values = [0f, 5f] });
            glow.AddTrack(new ValueTrack { Path = path, Property = "Energy", Times = [0f, 1f], Values = [0f, 5f], Interpolation = AnimationInterpolation.Cubic });
            if (i % 50 == 0)
                walk.AddTrack(new MethodTrack { Path = path, Times = [0.5f], Methods = ["Hum"], Arguments = ["[1]"] });
        }

        var player = new AnimationPlayer { Name = "Player" };
        var library = new AnimationLibrary();
        library.Animations.Add(walk);
        library.Animations.Add(glow);
        player.AddAnimationLibrary("", library);
        scene.AddChild(player);
        player.Play("walk");
        player.Play("glow", customBlend: 100); // a long crossfade: both animate for the whole window
        player.Play("walk", customBlend: 100);
        uint frame = 0;
        void Tick(int frames)
        {
            for (var i = 0; i < frames; i++)
                tree.Tick(new GameTime { DeltaTime = Step, FrameCount = ++frame });
        }

        Tick(130); // warm up: every method key has fired once (their call lists grew)
        foreach (var lamp in scene.Children.OfType<Lamp>())
        {
            lamp.Calls.Clear();
            lamp.Calls.Capacity = 64;
        }

        var allocated = AllocationGate.SmallestWindow(() => Tick(60));
        Assert.Equal(0, allocated);
        Assert.True(player.IsBlending);
    }
}
