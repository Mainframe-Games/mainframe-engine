using System.Numerics;
using MainframeEngine.Tests.Physics;
using MainframeEngine.Tests.TestAssets;

namespace MainframeEngine.Tests.Animation;

/// <summary>
/// A body applies root motion each physics step before it moves and slides; gravity still applies (the
/// <see cref="AnimationPlayer.RootMotionVertical"/> default).
/// </summary>
internal sealed class RootMotionWalker : CharacterBody3D
{
    public AnimationPlayer? Player { get; set; }

    protected override void OnPhysicsProcess(float delta)
    {
        if (Player is { } player)
            ApplyRootMotion(player);
        Velocity -= UpDirection * (9.81f * delta);
        MoveAndSlide();
    }
}

/// <summary>
/// ADR 0210 with physics: the generated character's <c>jump</c> and <c>circle</c> clips move a
/// <see cref="CharacterBody3D"/> through <see cref="CharacterBody3D.ApplyRootMotion"/> (in the body's facing, its skeleton
/// turned with it), and steady root motion with crossfades, <c>MoveAndSlide</c> and the skin palette allocates nothing.
/// Runs alone: its setup allocates heavily (the importer, the physics world).
/// </summary>
[Collection(nameof(SerialAllocationGates))]
public sealed class RootMotionCharacterTests
{
    private static string CharacterPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "Character", SkinnedCharacter.File);

    internal static (RootMotionWalker Body, AnimationPlayer Player) AddWalker(Node parent, Vector3 position, float yawDegrees)
    {
        var body = new RootMotionWalker { Name = "Walker", Position = position, RotationDegrees = new Vector3(0, yawDegrees, 0) };
        body.AddChild(new CollisionShape3D { Name = "Shape", Position = new Vector3(0, 0.9f, 0), Shape = new CapsuleShape3D { Radius = 0.3f, Height = 1.8f } });
        var character = ModelImporter.ImportNodes(CharacterPath);
        body.AddChild(character);
        var player = character.GetNode<AnimationPlayer>("AnimationPlayer");
        player.CallbackModeProcess = AnimationCallbackMode.Physics;
        player.RootMotionTrack = "Armature";
        player.RootMotionBone = "Hips";
        body.Player = player;
        parent.AddChild(body);
        return (body, player);
    }

    private static float Facing(Node3D body) => MathF.Atan2(body.GlobalTransform.Basis.Z.X, body.GlobalTransform.Basis.Z.Z);

    [Fact]
    public void TheJumpCarriesTheBodyForwardAndTheCircleBringsItBack()
    {
        using var h = new PhysicsHarness3D();
        h.AddFloor();
        var (body, player) = AddWalker(h.Scene, new Vector3(2, 0.05f, -1), 90); // facing +X
        h.Run(10); // settle onto the floor
        var start = body.GlobalPosition;

        player.Play("jump");
        h.RunSeconds((float)SkinnedCharacter.JumpLength + 0.2f);
        var moved = body.GlobalPosition - start;
        Near(SkinnedCharacter.JumpDistance, moved.X, 0.05f);
        Near(0f, moved.Z, 0.01f);
        Near(0f, moved.Y, 0.02f); // on the floor again: the arc stays in the pose
        Assert.True(body.IsOnFloor());
        var skeleton = player.GetNode<Skeleton3D>("../Armature");
        Assert.Equal(new Vector3(0, skeleton.GetBonePosePosition(0).Y, 0), skeleton.GetBonePosePosition(0)); // hips over the body

        // Four loops of the circle: a whole turn on a 0.8 m circle, back where it started.
        var before = body.GlobalPosition;
        player.Play("circle");
        h.RunSeconds(1f);
        // A step or two behind the clip: the body processes before its player, so it moves by the previous step's motion.
        Near(0f, WrapAngle(Facing(body) - MathF.PI), 0.08f); // a quarter turn left: facing +X → now −Z
        var quarter = body.GlobalPosition - before;
        Near(SkinnedCharacter.CircleRadius, quarter.X, 0.08f); // forward (+X) one radius …
        Near(-SkinnedCharacter.CircleRadius, quarter.Z, 0.08f); // … and one radius to its left (−Z)
        h.RunSeconds(3f);
        Assert.True(Vector3.Distance(before, body.GlobalPosition) < 0.1f, $"back at {body.GlobalPosition}, started at {before}");
        Near(0f, WrapAngle(Facing(body) - MathF.PI / 2), 0.08f);
    }

    private static float WrapAngle(float radians) => MathF.IEEERemainder(radians, MathF.Tau);

    private static void Near(float expected, float actual, float tolerance) =>
        Assert.True(MathF.Abs(expected - actual) <= tolerance, $"expected {expected} ± {tolerance}, got {actual}");

    [Fact]
    public void SteadyRootMotionWithCrossfadesAndMoveAndSlideAllocatesNothing()
    {
        using var h = new PhysicsHarness3D();
        h.AddFloor(size: 100);
        var walkers = new List<(RootMotionWalker Body, AnimationPlayer Player)>();
        for (var i = 0; i < 4; i++)
            walkers.Add(AddWalker(h.Scene, new Vector3(i * 4 - 6, 0.05f, 0), i * 90));
        var bodies = new MeshInstance3D[walkers.Count];
        for (var i = 0; i < walkers.Count; i++)
        {
            walkers[i].Player.Play("circle");
            bodies[i] = walkers[i].Player.GetNode<MeshInstance3D>("../Armature/Body");
        }

        var frame = 0ul;

        void Frames(int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (frame % 45 == 0)
                    foreach (var (_, player) in walkers)
                        player.Play(frame % 90 == 0 ? "jump" : "circle", customBlend: 0.2);
                h.Run(1);
                frame++;
                foreach (var mesh in bodies)
                    Assert.NotNull(mesh.PrepareSkin(frame));
            }
        }

        Frames(270); // warm up: every binding, slot and list has grown
        var allocated = AllocationGate.SmallestWindow(() => Frames(180));

        Assert.Equal(0, allocated);
        Assert.True(walkers[0].Body.GlobalPosition.LengthSquared() > 1f, "the walkers moved");
    }
}
