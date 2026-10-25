using System.Numerics;

namespace MainframeEngine.Tests.Animation;

/// <summary>
/// ADR 0207: <see cref="AnimationPlayer"/> playback (loops, crossfades, queue, speed, seek, stop, signals, events across
/// loops and directions, process modes), <see cref="Skeleton3D"/> poses, <see cref="BoneAttachment3D"/>, and
/// serialization of the new types. A small code-built rig: a two-bone skeleton with a hand attachment and a box node.
/// </summary>
public sealed class AnimationPlayerTests
{
    private const float Epsilon = 1e-4f;

    private static readonly Quaternion Quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
    private static readonly Quaternion Tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 4);

    private sealed class Rig
    {
        public required Node3D Root;
        public required Skeleton3D Skeleton;
        public required Node3D Box;
        public required AnimationPlayer Player;
        public required BoneAttachment3D Hand;
        public required MeshInstance3D Tool;
        public required OmniLight3D Torch;
        public readonly List<string> Events = [];
        public readonly List<string> Finished = [];
        public readonly List<(string, string)> Changed = [];
        public readonly List<string> Started = [];
    }

    private static Rig BuildRig(AnimationCallbackMode mode = AnimationCallbackMode.Manual)
    {
        var root = new Node3D { Name = "Rig" };
        var skeleton = new Skeleton3D { Name = "Skeleton" };
        skeleton.AddBone("Root", -1, Transform3D.Identity);
        skeleton.AddBone("Arm", 0, Transform3D.FromTrs(new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One));
        skeleton.AddBone("Hand", 1, Transform3D.FromTrs(new Vector3(1, 0, 0), Quaternion.Identity, Vector3.One));
        root.AddChild(skeleton);
        var hand = new BoneAttachment3D { Name = "HandAttachment", BoneName = "Hand" };
        var tool = new MeshInstance3D { Name = "Tool", Mesh = new BoxMesh { Size = new Vector3(0.1f) }, Position = new Vector3(0.2f, 0, 0) };
        var torch = new OmniLight3D { Name = "Torch" };
        hand.AddChild(tool);
        hand.AddChild(torch);
        skeleton.AddChild(hand);
        var box = new Node3D { Name = "Box" };
        root.AddChild(box);

        var run = new MainframeEngine.Animation { Length = 1f, LoopMode = AnimationLoopMode.Linear };
        run.AddTrack(new Rotation3DTrack { Path = "Skeleton", Bone = "Arm", Times = [0, 0.5f, 1], Values = [Quaternion.Identity, Quarter, Quaternion.Identity] });
        run.AddTrack(new Position3DTrack { Path = "Box", Times = [0, 1], Values = [Vector3.Zero, Vector3.UnitX] });
        run.AddEvent("Step", 0.25f);
        run.AddEvent("Step", 0.75f);
        var idle = new MainframeEngine.Animation { Length = 2f, LoopMode = AnimationLoopMode.Linear };
        idle.AddTrack(new Rotation3DTrack { Path = "Skeleton", Bone = "Arm", Times = [0], Values = [Tilt] });
        var once = new MainframeEngine.Animation { Length = 0.5f };
        once.AddTrack(new Position3DTrack { Path = "Box", Times = [0, 0.5f], Values = [Vector3.Zero, new Vector3(0, 2, 0)] });
        once.AddEvent("Mid", 0.25f);
        once.AddEvent("End", 0.5f);
        var bounce = new MainframeEngine.Animation { Length = 1f, LoopMode = AnimationLoopMode.PingPong };
        bounce.AddTrack(new Position3DTrack { Path = "Box", Times = [0, 1], Values = [Vector3.Zero, Vector3.UnitY] });
        bounce.AddEvent("Top", 0.9f);
        var library = new AnimationLibrary();
        library.Add("run", run);
        library.Add("idle", idle);
        library.Add("once", once);
        library.Add("bounce", bounce);

        var player = new AnimationPlayer { Name = "AnimationPlayer", CallbackModeProcess = mode };
        player.AddAnimationLibrary("", library);
        root.AddChild(player);
        var rig = new Rig { Root = root, Skeleton = skeleton, Box = box, Player = player, Hand = hand, Tool = tool, Torch = torch };
        player.AnimationEvent += (_, name) => rig.Events.Add(name);
        player.AnimationFinished += name => rig.Finished.Add(name);
        player.AnimationChanged += (from, to) => rig.Changed.Add((from, to));
        player.AnimationStarted += name => rig.Started.Add(name);
        return rig;
    }

    private static void Advance(AnimationPlayer player, double seconds, double step = 0.05)
    {
        for (var t = 0.0; t < seconds - 1e-9; t += step)
            player.Advance(Math.Min(step, seconds - t));
    }

    [Fact]
    public void ALoopingAnimationLoopsAndRaisesItsEventsEveryLoop()
    {
        var rig = BuildRig();
        rig.Player.Play("run");
        Assert.Equal("run", rig.Player.CurrentAnimation);
        Assert.Equal(["run"], rig.Started);

        Advance(rig.Player, 2.5, 0.1);

        Assert.True(rig.Player.IsPlaying());
        Assert.Equal(0.5, rig.Player.CurrentAnimationPosition, 4);
        Assert.Equal(5, rig.Events.Count(e => e == "Step")); // 0.25, 0.75, 1.25, 1.75, 2.25
        Assert.Empty(rig.Finished); // looping animations never finish
        AssertNear(Quarter, rig.Skeleton.GetBonePoseRotation(1));
        AssertNear(new Vector3(0.5f, 0, 0), rig.Box.Position);
    }

    [Fact]
    public void SwitchingCrossfadesOverTheBlendTime()
    {
        var rig = BuildRig();
        rig.Player.Play("run");
        Advance(rig.Player, 0.5, 0.1);
        AssertNear(Quarter, rig.Skeleton.GetBonePoseRotation(1));

        rig.Player.Play("idle", customBlend: 0.2);
        Assert.True(rig.Player.IsBlending);
        rig.Player.Advance(0.1);

        // Half way: run (at 0.6 s) and idle mix; the box, which only run animates, blends toward where it was at the switch.
        var run = Quaternion.Slerp(Quarter, Quaternion.Identity, 0.2f);
        AssertNear(Quaternion.Slerp(run, Tilt, 0.5f), rig.Skeleton.GetBonePoseRotation(1));
        AssertNear(new Vector3(0.55f, 0, 0), rig.Box.Position);

        rig.Player.Advance(0.15);
        Assert.False(rig.Player.IsBlending);
        AssertNear(Tilt, rig.Skeleton.GetBonePoseRotation(1));
        AssertNear(new Vector3(0.5f, 0, 0), rig.Box.Position);
        Assert.Equal("idle", rig.Player.CurrentAnimation);
    }

    [Fact]
    public void APlayDuringACrossfadeBlendsFromTheCurrentMix()
    {
        var rig = BuildRig();
        rig.Player.Play("run");
        Advance(rig.Player, 0.5, 0.1);
        rig.Player.Play("idle", customBlend: 0.4);
        rig.Player.Advance(0.2); // half way between run and idle
        var mix = rig.Skeleton.GetBonePoseRotation(1);

        rig.Player.Play("once", customBlend: 0.4); // once does not animate the arm: it blends from the mix to the rest
        rig.Player.Advance(0.001);
        Assert.True(MathF.Abs(Quaternion.Dot(mix, rig.Skeleton.GetBonePoseRotation(1))) > 0.9999f, "the pose jumped at the third play");
        rig.Player.Advance(0.2);
        AssertNear(Quaternion.Slerp(mix, Quaternion.Identity, 0.5f + 0.001f / 0.4f), rig.Skeleton.GetBonePoseRotation(1));
        rig.Player.Advance(0.25);
        Assert.False(rig.Player.IsBlending);
        AssertNear(Quaternion.Identity, rig.Skeleton.GetBonePoseRotation(1));
    }

    [Fact]
    public void AClipFitsADurationThroughItsLengthAndTheSpeed()
    {
        var rig = BuildRig();
        const double duration = 2.0;
        rig.Player.Play("once");
        var length = rig.Player.CurrentAnimationLength;
        Assert.Equal(0.5, length, 6);
        rig.Player.Stop();

        rig.Player.Play("once", customSpeed: (float)(length / duration));
        Advance(rig.Player, 1.9);
        Assert.Empty(rig.Finished);
        Assert.Equal(["Mid"], rig.Events);
        Advance(rig.Player, 0.2);
        Assert.Equal(["once"], rig.Finished);
        Assert.Equal(["Mid", "End"], rig.Events);
        Assert.False(rig.Player.IsPlaying());
        Assert.Equal("", rig.Player.CurrentAnimation);
        Assert.Equal("once", rig.Player.AssignedAnimation);
        AssertNear(new Vector3(0, 2, 0), rig.Box.Position); // the last frame holds

        // SpeedScale does the same for every animation.
        rig.Finished.Clear();
        rig.Player.SpeedScale = 0.25f;
        rig.Player.Play("once");
        Advance(rig.Player, 1.95);
        Assert.Empty(rig.Finished);
        Advance(rig.Player, 0.1);
        Assert.Equal(["once"], rig.Finished);
    }

    [Fact]
    public void AQueuedAnimationTakesOverWhenTheCurrentOneFinishes()
    {
        var rig = BuildRig();
        rig.Player.Play("once");
        rig.Player.Queue("run");
        Advance(rig.Player, 0.6);
        Assert.Equal(["once"], rig.Finished);
        Assert.Equal([("once", "run")], rig.Changed);
        Assert.Equal("run", rig.Player.CurrentAnimation);
        Assert.Equal(["once", "run"], rig.Started);
    }

    [Fact]
    public void SeekDoesNotRaiseEventsAndStopReturnsToTheStart()
    {
        var rig = BuildRig();
        rig.Player.Play("run");
        rig.Player.Seek(0.8, update: true);
        Assert.Empty(rig.Events);
        AssertNear(new Vector3(0.8f, 0, 0), rig.Box.Position);
        rig.Player.Advance(0.1);
        Assert.Empty(rig.Events); // 0.8 → 0.9 passes nothing
        rig.Player.Advance(0.4); // 0.9 → 1.3 (wraps): the 0.25 step
        Assert.Equal(["Step"], rig.Events);

        rig.Player.Stop();
        Assert.False(rig.Player.IsPlaying());
        Assert.Equal(0, rig.Player.CurrentAnimationPosition);
        AssertNear(Vector3.Zero, rig.Box.Position);

        rig.Player.Pause();
        rig.Player.Play("run");
        rig.Player.Seek(0.3, update: true);
        rig.Player.Pause();
        rig.Player.Advance(1);
        Assert.Equal(0.3, rig.Player.CurrentAnimationPosition, 6); // paused: nothing advances
        rig.Player.Play(); // resumes the assigned animation
        rig.Player.Advance(0.1);
        Assert.Equal(0.4, rig.Player.CurrentAnimationPosition, 6);
    }

    [Fact]
    public void EventsFireBackwardsAndOncePerPingPongPass()
    {
        var rig = BuildRig();
        rig.Player.PlayBackwards("run");
        Assert.Equal(1.0, rig.Player.CurrentAnimationPosition, 6);
        Advance(rig.Player, 1.0, 0.1);
        Assert.Equal(["Step", "Step"], rig.Events); // 0.75 then 0.25

        rig.Events.Clear();
        rig.Player.Play("bounce");
        Advance(rig.Player, 2.0, 0.1); // up (0.9 passed), down (0.9 passed again)
        Assert.Equal(["Top", "Top"], rig.Events);
        AssertNear(Vector3.Zero, rig.Box.Position, 1e-3f);
    }

    [Fact]
    public void TheFirstFrameRaisesEventsAtTimeZero()
    {
        var rig = BuildRig();
        rig.Player.GetAnimation("once")!.AddEvent("Start", 0f);
        rig.Player.Play("once");
        rig.Player.Advance(0.01);
        Assert.Equal(["Start"], rig.Events);
    }

    [Fact]
    public void PlayersAdvanceInIdleOrPhysicsProcessAndAutoplay()
    {
        var idle = BuildRig(AnimationCallbackMode.Idle);
        idle.Player.Autoplay = "run";
        var physics = BuildRig(AnimationCallbackMode.Physics);
        physics.Player.Autoplay = "run";
        var tree = new SceneTree();
        tree.Root.AddChild(idle.Root);
        tree.Root.AddChild(physics.Root);
        for (var i = 0; i < 6; i++)
            tree.Tick(new GameTime { DeltaTime = 1f / 60 });

        Assert.Equal(0.1, idle.Player.CurrentAnimationPosition, 4);
        Assert.Equal(0.1, physics.Player.CurrentAnimationPosition, 4);
        AssertNear(new Vector3(0.1f, 0, 0), physics.Box.Position, 1e-3f);
        tree.Shutdown();
    }

    [Fact]
    public void ABoneAttachmentKeepsAMeshAndALightOnTheBone()
    {
        var rig = BuildRig(AnimationCallbackMode.Idle);
        var tree = new SceneTree();
        rig.Root.Position = new Vector3(5, 0, 0);
        tree.Root.AddChild(rig.Root);
        Assert.Equal(2, rig.Hand.BoneIdx);
        AssertNear(new Vector3(6, 1, 0), rig.Hand.GlobalPosition); // at rest: the hand is 1 to the right of the arm

        rig.Player.Play("run");
        for (var i = 0; i < 30; i++) // half a second: the arm turns a quarter about Z, the hand goes up
            tree.Tick(new GameTime { DeltaTime = 1f / 60 });
        var expected = rig.Skeleton.GlobalTransform * rig.Skeleton.GetBoneGlobalPose(2);
        AssertNear(new Vector3(5, 2, 0), expected.Origin, 1e-3f);
        AssertNear(expected.Origin, rig.Hand.GlobalPosition, 1e-4f);
        AssertNear(expected.TransformPoint(new Vector3(0.2f, 0, 0)), rig.Tool.GlobalPosition, 1e-4f);
        AssertNear(expected.Origin, rig.Torch.GlobalPosition, 1e-4f);
        tree.Shutdown();
    }

    [Fact]
    public void SkeletonGlobalPosesChainParentsFirst()
    {
        var skeleton = new Skeleton3D();
        skeleton.AddBone("A", -1, Transform3D.FromTrs(new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One));
        skeleton.AddBone("B", 0, Transform3D.FromTrs(new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One));
        AssertNear(new Vector3(0, 2, 0), skeleton.GetBoneGlobalPose(1).Origin);
        skeleton.SetBonePoseRotation(0, Quarter); // +Y turns to −X
        AssertNear(new Vector3(-1, 1, 0), skeleton.GetBoneGlobalPose(1).Origin);
        skeleton.SetBonePoseScale(0, new Vector3(2));
        AssertNear(new Vector3(-2, 1, 0), skeleton.GetBoneGlobalPose(1).Origin);
        skeleton.ResetBonePoses();
        AssertNear(new Vector3(0, 2, 0), skeleton.GetBoneGlobalPose(1).Origin);
        Assert.Equal(-1, skeleton.FindBone("C"));
        skeleton.Dispose();
    }

    [Fact]
    public void TheNewTypesRoundTripThroughAScene()
    {
        var rig = BuildRig();
        rig.Skeleton.AddChild(new MeshInstance3D { Name = "Body", Skin = new Skin(), Mesh = SkinnedQuad() });
        Own(rig.Root, rig.Root);
        var json = SceneSaver.ToJson(rig.Root);
        var copy = (Node3D)PackedScene.Parse(json).Instantiate();

        var skeleton = copy.GetNode<Skeleton3D>("Skeleton");
        Assert.Equal(["Root", "Arm", "Hand"], skeleton.BoneNames);
        Assert.Equal([-1, 0, 1], skeleton.BoneParents);
        Assert.Equal(new Vector3(0, 1, 0), skeleton.GetBoneRest(1).Origin);
        Assert.Equal("Hand", copy.GetNode<BoneAttachment3D>("Skeleton/HandAttachment").BoneName);
        var body = copy.GetNode<MeshInstance3D>("Skeleton/Body");
        Assert.True(body.Mesh!.GetSurface(0).IsSkinned);
        Assert.Equal([0, 1, 0, 0, 1, 2, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0], body.Mesh.GetSurface(0).Joints[..16]);
        var player = copy.GetNode<AnimationPlayer>("AnimationPlayer");
        Assert.Equal(AnimationCallbackMode.Manual, player.CallbackModeProcess);
        var run = player.GetAnimation("run")!;
        Assert.Equal(AnimationLoopMode.Linear, run.LoopMode);
        Assert.Equal([("Step", 0.25f), ("Step", 0.75f)], run.GetEvents().ToArray());
        Assert.Equal(Quarter, Assert.IsType<Rotation3DTrack>(run.Tracks[0]).Values[1]);
        Assert.Equal(SceneSaver.ToJson(rig.Root), SceneSaver.ToJson(copy)); // byte-identical re-save
        rig.Root.Dispose();
        copy.Dispose();
    }

    [Fact]
    public void AStaticSurfaceOfASkinnedMeshIsNotDeformed()
    {
        var mesh = SkinnedQuad();
        var skeleton = new Skeleton3D();
        skeleton.AddBone("Root", -1, Transform3D.Identity);
        skeleton.AddBone("Arm", 0, Transform3D.FromTrs(new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One));
        skeleton.AddBone("Hand", 1, Transform3D.FromTrs(new Vector3(1, 0, 0), Quaternion.Identity, Vector3.One));
        var state = new SkinState();
        Assert.True(state.Update(mesh, null, skeleton, Matrix4x4.Identity)); // no skin: joint i is bone i at its rest
        skeleton.SetBonePosePosition(0, new Vector3(0, 3, 0));
        Assert.True(state.Update(mesh, null, skeleton, Matrix4x4.Identity));
        var surface = mesh.GetSurface(0);
        AssertNear(surface.Positions[0] + new Vector3(0, 3, 0), state.SkinPoint(surface.Positions[0], surface.Joints.AsSpan(0, 4), surface.Weights[0]));
        AssertNear(surface.Positions[3], state.SkinPoint(surface.Positions[3], surface.Joints.AsSpan(12, 4), surface.Weights[3])); // zero weights
        Assert.True(state.Bounds.Max.Y >= 3f);
        skeleton.Dispose();
    }

    private static void Own(Node owner, Node node)
    {
        foreach (var child in node.Children)
        {
            child.Owner = owner;
            Own(owner, child);
        }
    }

    // Four vertices: two on bone 0, one shared by bones 1 and 2, one with no weights.
    private static ArrayMesh SkinnedQuad()
    {
        var surface = new MeshSurface([new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)], [], [], [0, 1, 2, 0, 2, 3])
        {
            Joints = [0, 1, 0, 0, 1, 2, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0],
            Weights = [new Vector4(1, 0, 0, 0), new Vector4(0.5f, 0.5f, 0, 0), new Vector4(1, 0, 0, 0), Vector4.Zero],
        };
        var mesh = new ArrayMesh();
        mesh.AddSurface(surface);
        return mesh;
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, float epsilon = Epsilon) =>
        Assert.True(Vector3.Distance(expected, actual) < epsilon, $"expected {expected}, got {actual}");

    private static void AssertNear(Quaternion expected, Quaternion actual) =>
        Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual))) > 1 - 1e-5f, $"expected {expected}, got {actual}");
}
