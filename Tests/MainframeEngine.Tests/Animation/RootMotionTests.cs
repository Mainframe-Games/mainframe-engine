using System.Numerics;

namespace MainframeEngine.Tests.Animation;

/// <summary>
/// ADR 0210: <see cref="AnimationPlayer"/> root motion on a code-built rig (a hips bone at rest 1 m up with a spine): the
/// pose keeps the hips at their rest horizontally (and vertically with <see cref="AnimationPlayer.RootMotionVertical"/>)
/// and facing their rest yaw, while the movement and the turn come out per update; loop wraps add the clip's whole
/// displacement, turns carry the movement into the character's facing, crossfades mix both clips' motion.
/// </summary>
public sealed class RootMotionTests
{
    private const float Epsilon = 1e-4f;

    private static Quaternion Yaw(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, float.DegreesToRadians(degrees));

    private static (Node3D Root, Skeleton3D Skeleton, AnimationPlayer Player) BuildRig()
    {
        var root = new Node3D { Name = "Rig" };
        var skeleton = new Skeleton3D { Name = "Skeleton" };
        skeleton.AddBone("Hips", -1, Transform3D.FromTrs(new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One));
        skeleton.AddBone("Spine", 0, Transform3D.FromTrs(new Vector3(0, 0.2f, 0), Quaternion.Identity, Vector3.One));
        root.AddChild(skeleton);

        // walk: 2 m/s forward (+Z), the hips bobbing down 0.1 m mid-stride.
        var walk = new MainframeEngine.Animation { Length = 1f, LoopMode = AnimationLoopMode.Linear };
        walk.AddTrack(new Position3DTrack
        {
            Path = "Skeleton",
            Bone = "Hips",
            Times = [0, 0.5f, 1],
            Values = [new Vector3(0, 1, 0), new Vector3(0, 0.9f, 1), new Vector3(0, 1, 2)],
        });
        walk.AddTrack(new Rotation3DTrack { Path = "Skeleton", Bone = "Spine", Times = [0, 1], Values = [Quaternion.Identity, Quaternion.Identity] });
        // strafe: 1 m/s to the left (+X).
        var strafe = new MainframeEngine.Animation { Length = 1f, LoopMode = AnimationLoopMode.Linear };
        strafe.AddTrack(new Position3DTrack { Path = "Skeleton", Bone = "Hips", Times = [0, 1], Values = [new Vector3(0, 1, 0), new Vector3(1, 1, 0)] });
        // circle: a quarter turn left per loop on a 1 m arc, the hips' yaw following the path.
        var circle = new MainframeEngine.Animation { Length = 1f, LoopMode = AnimationLoopMode.Linear };
        var times = new float[9];
        var positions = new Vector3[9];
        var rotations = new Quaternion[9];
        for (var k = 0; k < times.Length; k++)
        {
            var t = k / 8f;
            var angle = MathF.PI / 2 * t;
            times[k] = t;
            positions[k] = new Vector3(1 - MathF.Cos(angle), 1, MathF.Sin(angle));
            rotations[k] = Yaw(90 * t);
        }

        circle.AddTrack(new Position3DTrack { Path = "Skeleton", Bone = "Hips", Times = times, Values = positions });
        circle.AddTrack(new Rotation3DTrack { Path = "Skeleton", Bone = "Hips", Times = times, Values = rotations });
        // spin: a whole turn in place per loop (more than half a circle between its ends).
        var spin = new MainframeEngine.Animation { Length = 1f, LoopMode = AnimationLoopMode.Linear };
        spin.AddTrack(new Rotation3DTrack { Path = "Skeleton", Bone = "Hips", Times = [0, 0.25f, 0.5f, 0.75f, 1], Values = [Yaw(0), Yaw(90), Yaw(180), Yaw(270), Yaw(360)] });
        // still: no root track.
        var still = new MainframeEngine.Animation { Length = 1f, LoopMode = AnimationLoopMode.Linear };
        still.AddTrack(new Rotation3DTrack { Path = "Skeleton", Bone = "Spine", Times = [0], Values = [Quaternion.Identity] });

        var library = new AnimationLibrary();
        library.Add("walk", walk);
        library.Add("strafe", strafe);
        library.Add("circle", circle);
        library.Add("spin", spin);
        library.Add("still", still);
        var player = new AnimationPlayer
        {
            Name = "AnimationPlayer",
            CallbackModeProcess = AnimationCallbackMode.Manual,
            RootMotionTrack = "Skeleton",
            RootMotionBone = "Hips",
        };
        player.AddAnimationLibrary("", library);
        root.AddChild(player);
        return (root, skeleton, player);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, float epsilon = Epsilon) =>
        Assert.True(Vector3.Distance(expected, actual) < epsilon, $"expected {expected}, got {actual}");

    // Follows the motion the way a body would: move by the delta in the current facing, then turn.
    private static (Vector3 Position, float Yaw) Integrate(AnimationPlayer player, int steps, double step, Vector3 position = default, float yaw = 0)
    {
        for (var i = 0; i < steps; i++)
        {
            player.Advance(step);
            position += Vector3.Transform(player.GetRootMotionPosition(), Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
            yaw += AnimationPlayer.Yaw(player.GetRootMotionRotation());
        }

        return (position, yaw);
    }

    [Fact]
    public void TheHorizontalMovementLeavesThePoseAndComesOutPerUpdate()
    {
        var (root, skeleton, player) = BuildRig();
        player.Play("walk");
        AssertNear(new Vector3(0, 1, 0), skeleton.GetBonePosePosition(0));
        Assert.Equal(Vector3.Zero, player.GetRootMotionPosition()); // Play applies the pose, it does not move

        player.Advance(0.25);
        AssertNear(new Vector3(0, 0, 0.5f), player.GetRootMotionPosition());
        AssertNear(new Vector3(0, 0.95f, 0), skeleton.GetBonePosePosition(0)); // the bob stays, the stride goes
        player.Advance(0.25);
        AssertNear(new Vector3(0, 0, 0.5f), player.GetRootMotionPosition()); // each update replaces the last one's
        Assert.Equal(Quaternion.Identity, player.GetRootMotionRotation());

        player.Pause();
        player.Advance(0.25);
        Assert.Equal(Vector3.Zero, player.GetRootMotionPosition()); // consumed: nothing moved in this update
        root.Dispose();
    }

    [Fact]
    public void VerticalRootMotionMovesTheCharacterInsteadOfTheHips()
    {
        var (root, skeleton, player) = BuildRig();
        player.RootMotionVertical = true;
        player.Play("walk");
        player.Advance(0.25);
        AssertNear(new Vector3(0, -0.05f, 0.5f), player.GetRootMotionPosition());
        AssertNear(new Vector3(0, 1, 0), skeleton.GetBonePosePosition(0));
        root.Dispose();
    }

    [Fact]
    public void LoopWrapsAddTheClipsWholeDisplacement()
    {
        var (root, _, player) = BuildRig();
        player.Play("walk");
        player.Advance(0.75);
        player.Advance(0.5); // 0.75 → 1 → 0 → 0.25
        AssertNear(new Vector3(0, 0, 1), player.GetRootMotionPosition());
        player.Advance(2.5); // two and a half loops in one update
        AssertNear(new Vector3(0, 0, 5), player.GetRootMotionPosition());

        var (position, _) = Integrate(player, 180, 1.0 / 60);
        AssertNear(new Vector3(0, 0, 6), position, 1e-3f);

        player.SpeedScale = -1f; // backwards walks back
        player.Advance(0.5);
        AssertNear(new Vector3(0, 0, -1), player.GetRootMotionPosition());
        root.Dispose();
    }

    [Fact]
    public void TurnsComeOutOfThePoseAndCarryTheMovementRound()
    {
        var (root, skeleton, player) = BuildRig();
        player.Play("circle");
        player.Advance(0.5);
        Assert.Equal(float.DegreesToRadians(45), AnimationPlayer.Yaw(player.GetRootMotionRotation()), 3);
        Assert.Equal(0f, AnimationPlayer.Yaw(skeleton.GetBonePoseRotation(0)), 4); // the hips keep facing forward
        AssertNear(new Vector3(0, 1, 0), skeleton.GetBonePosePosition(0));

        // Four loops close the circle: back at the start, a whole turn later, in small steps or in big ones.
        player.Stop();
        player.Play("circle");
        var (position, yaw) = Integrate(player, 240, 1.0 / 60);
        AssertNear(Vector3.Zero, position, 2e-3f);
        Assert.Equal(MathF.Tau, yaw, 3);
        // After one loop the character is a quarter circle on, facing +X.
        player.Stop();
        player.Play("circle");
        (position, yaw) = Integrate(player, 4, 0.25);
        AssertNear(new Vector3(1, 0, 1), position, 2e-3f);
        Assert.Equal(MathF.PI / 2, yaw, 3);
        player.Stop();
        player.Play("circle");
        (position, yaw) = Integrate(player, 3, 0.7);
        player.Stop();
        player.Play("circle");
        var (expected, expectedYaw) = Integrate(player, 126, 1.0 / 60);
        AssertNear(expected, position, 3e-2f); // big steps chord the arc a little
        Assert.Equal(expectedYaw, yaw, 3);
        root.Dispose();
    }

    [Fact]
    public void AClipTurningMoreThanHalfACircleLoopsByItsWholeTurn()
    {
        var (root, _, player) = BuildRig();
        player.Play("spin");
        player.Advance(1.0); // exactly one loop in one update: 0 → 1
        Assert.Equal(MathF.Tau, player.RootMotionYaw, 3);
        var (_, yaw) = Integrate(player, 60, 1.0 / 60);
        Assert.Equal(MathF.Tau, yaw, 3);
        root.Dispose();
    }

    [Fact]
    public void CrossfadesMixBothClipsMotionByTheBlendWeight()
    {
        var (root, _, player) = BuildRig();
        player.Play("walk");
        player.Advance(0.1);
        player.Play("strafe", customBlend: 0.5);
        player.Advance(0.25); // half way: walk 0.5 m forward, strafe 0.25 m left, mixed 50/50
        AssertNear(new Vector3(0.125f, 0, 0.25f), player.GetRootMotionPosition());
        player.Advance(0.5); // the blend ends: strafe only
        AssertNear(new Vector3(0.5f, 0, 0), player.GetRootMotionPosition());

        player.Play("still", customBlend: 0.5); // a clip without root tracks blends the motion out
        player.Advance(0.25);
        AssertNear(new Vector3(0.125f, 0, 0), player.GetRootMotionPosition());
        root.Dispose();
    }

    [Fact]
    public void WithoutARootMotionTrackTheBoneMovesAsKeyed()
    {
        var (root, skeleton, player) = BuildRig();
        player.RootMotionTrack = "";
        player.Play("walk");
        player.Advance(0.25);
        Assert.Equal(Vector3.Zero, player.GetRootMotionPosition());
        AssertNear(new Vector3(0, 0.95f, 0.5f), skeleton.GetBonePosePosition(0));

        // An empty RootMotionBone means the skeleton's first root bone; a setting change applies on the next update.
        player.RootMotionTrack = "Skeleton";
        player.RootMotionBone = "";
        player.Advance(0.25);
        AssertNear(new Vector3(0, 0, 0.5f), player.GetRootMotionPosition());
        AssertNear(new Vector3(0, 0.9f, 0), skeleton.GetBonePosePosition(0));
        root.Dispose();
    }

    [Fact]
    public void YawIsTheTwistAboutUpWhicheverSideTheSwingIsOn()
    {
        var swing = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.6f);
        var twist = Yaw(30);
        Assert.Equal(float.DegreesToRadians(30), AnimationPlayer.Yaw(twist * swing), 4);
        Assert.Equal(float.DegreesToRadians(30), AnimationPlayer.Yaw(swing * twist), 4);
        Assert.Equal(float.DegreesToRadians(30), AnimationPlayer.Yaw(-twist), 4); // the same rotation
        Assert.Equal(0f, AnimationPlayer.Yaw(Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI)), 4);
    }
}
