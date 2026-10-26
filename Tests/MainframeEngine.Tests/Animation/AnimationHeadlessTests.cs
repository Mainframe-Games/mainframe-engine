using System.Numerics;
using MainframeEngine.Tests.TestAssets;

namespace MainframeEngine.Tests.Animation;

/// <summary>
/// ADR 0207 on a dedicated server: the imported character's clips evaluate and its events fire under
/// <see cref="HeadlessHost"/> (no renderer), on the same frames every run; steady playback with crossfades, events and the
/// skin palette allocates nothing.
/// </summary>
[Collection(nameof(MainframeEngine.Tests.Localization.LocalizationState))] // the host configures the catalogs
public sealed class AnimationHeadlessTests
{
    private static string CharacterPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "Character", SkinnedCharacter.File);

    private static ProjectSettings Settings()
    {
        var settings = new ProjectSettings { Name = "AnimationHeadlessTest" };
        settings.Audio.Enabled = false;
        return settings;
    }

    private static (uint EventFrame, uint FinishedFrame) RunSwing()
    {
        using var host = new HeadlessHost(Settings(), new GameHostOptions { MaxFrames = 60, FixedFps = 60 });
        var character = ModelImporter.ImportNodes(CharacterPath);
        host.Tree.Root.AddChild(character);
        var player = character.GetNode<AnimationPlayer>("AnimationPlayer");
        uint eventFrame = 0, finishedFrame = 0;
        player.AnimationEvent += (animation, name) =>
        {
            if (animation == "swing" && name == "HammerFist")
                eventFrame = (uint)host.Tree.ProcessFrames + 1;
        };
        player.AnimationFinished += _ => finishedFrame = (uint)host.Tree.ProcessFrames + 1;
        player.Play("swing");

        Assert.Equal(ExitCode.Ok, host.Run());
        Assert.Null(host.Tree.Servers.Render);
        return (eventFrame, finishedFrame);
    }

    [Fact]
    public void ClipsEvaluateAndEventsFireHeadlessOnTheSameFramesEveryRun()
    {
        var first = RunSwing();
        var second = RunSwing();

        Assert.InRange(first.EventFrame, 24u, 25u); // HammerFist at 0.4 s of a 60 fps run
        Assert.InRange(first.FinishedFrame, 48u, 49u); // the 0.8 s clip
        Assert.Equal(first, second);
    }

    // ADR 0210: a body walking the circle clip by root motion on a dedicated server, for two seconds.
    private static (Vector3 Position, Quaternion Rotation) RunRootMotion()
    {
        using var host = new HeadlessHost(Settings(), new GameHostOptions { MaxFrames = 120, FixedFps = 60 });
        var floor = new StaticBody3D { Name = "Floor", Position = new Vector3(0, -0.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(40, 1, 40) } });
        host.Tree.Root.AddChild(floor);
        var (body, player) = RootMotionCharacterTests.AddWalker(host.Tree.Root, new Vector3(0, 0.05f, 0), 0);
        player.Play("circle", customBlend: 0.1);

        Assert.Equal(ExitCode.Ok, host.Run());
        return (body.GlobalPosition, body.GlobalRotation);
    }

    [Fact]
    public void RootMotionMovesABodyHeadlessTheSameWayEveryRun()
    {
        var first = RunRootMotion();
        var second = RunRootMotion();

        // Half the circle, turning left (+X): across it, a diameter from the start.
        Assert.True(Vector3.Distance(new Vector3(2 * SkinnedCharacter.CircleRadius, 0, 0), first.Position) < 0.1f, $"at {first.Position}");
        Assert.Equal(first, second);
    }

    [Fact]
    public void SteadyPlaybackCrossfadesEventsAndTheSkinPaletteAllocateNothing()
    {
        var tree = new SceneTree();
        var character = ModelImporter.ImportNodes(CharacterPath);
        tree.Root.AddChild(character);
        var player = character.GetNode<AnimationPlayer>("AnimationPlayer");
        var body = character.GetNode<MeshInstance3D>("Armature/Body");
        var events = 0;
        player.AnimationEvent += (_, _) => events++;
        player.Play("run");
        player.Play("walk", customBlend: 0.15);
        var frame = 0ul;

        void Frames(int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (frame % 30 == 0)
                    player.Play(frame % 60 == 0 ? "run" : "walk", customBlend: 0.15);
                tree.Tick(new GameTime { DeltaTime = 1f / 60 });
                Assert.NotNull(body.PrepareSkin(++frame));
            }
        }

        Frames(240); // warm up: every binding, slot and deferred-call list has grown
        var allocated = AllocationGate.SmallestWindow(() => Frames(120));

        Assert.Equal(0, allocated);
        Assert.True(events > 0, "the run's steps fired");
        tree.Shutdown();
    }
}
