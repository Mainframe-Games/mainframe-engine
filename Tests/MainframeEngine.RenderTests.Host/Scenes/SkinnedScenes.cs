
using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// ADR 0207: the imported skinned character (<c>Tests/Assets/Models/Character</c>) posed and shadowed. Left: <c>swing</c> at
/// 0.3 s (the right arm raised over the head) with a white <see cref="OutlineMaterial3D"/> overlay, a hammer and a torch
/// (<see cref="OmniLight3D"/>, casting a point shadow) on its hand bone through a <see cref="BoneAttachment3D"/>. Right:
/// <c>run</c> at 0.15 s. A low sun casts cascaded shadows. Both players are in <see cref="AnimationCallbackMode.Manual"/>
/// and seeked, so every run shows the same pose. Self-checks: both instances are pre-skinned every frame, the posed hand
/// (empty space in the rest pose) picks the left character's mesh, and the torch sits on the hand bone.
/// </summary>
public sealed class SkinnedScene(HostOptions host) : ShadowSceneBase(host)
{
    public const string CharacterPath = "Assets/Models/Character/character.glb";
    private const uint PickFrame = 4, CheckFrame = 14;

    private Node3D _swinger = null!;
    private MeshInstance3D _swingerBody = null!;
    private OmniLight3D _torch = null!;
    private Task<PickResult>? _pick;
    private bool _checked;
    private int _minSkinned = int.MaxValue;

    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0.2f, 1.7f, 4.2f);
        Camera.LookAt(new Vector3(0.2f, 1.05f, 0));
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(12, 12) }, MaterialOverride = Plain(205, 200, 190) });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Wall",
            Position = new Vector3(0, 1.5f, -1.6f),
            Mesh = new BoxMesh { Size = new Vector3(8, 3, 0.2f) },
            MaterialOverride = Plain(170, 175, 185),
        });
        scene.AddChild(Sun(new Vector3(0.55f, -0.7f, -0.45f), 0.8f));

        var model = ResourceLoader.Load<PackedScene>(CharacterPath);
        _swinger = model.Instantiate<Node3D>();
        _swinger.Name = "Swinger";
        _swinger.Position = new Vector3(-0.75f, 0, 0);
        _swinger.RotationDegrees = new Vector3(0, 25, 0);
        scene.AddChild(_swinger);
        _swingerBody = _swinger.GetNode<MeshInstance3D>("Armature/Body");
        _swingerBody.MaterialOverlay = new OutlineMaterial3D { Color = System.Drawing.Color.White, Width = 3f };

        var skeleton = _swinger.GetNode<Skeleton3D>("Armature");
        var hand = new BoneAttachment3D { Name = "HandSlot", BoneName = "Hand.R" };
        hand.AddChild(new MeshInstance3D
        {
            Name = "Hammer",
            Position = new Vector3(-0.08f, 0, 0.12f),
            Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.05f, 0.32f) },
            MaterialOverride = Plain(120, 80, 45),
        });
        _torch = new OmniLight3D
        {
            Name = "Torch",
            Position = new Vector3(-0.1f, -0.12f, 0.05f),
            Color = new Vector3(1f, 0.62f, 0.25f),
            Energy = 2.2f,
            Range = 3.2f,
            CastsShadows = true,
        };
        hand.AddChild(_torch);
        skeleton.AddChild(hand);

        var runner = model.Instantiate<Node3D>();
        runner.Name = "Runner";
        runner.Position = new Vector3(0.95f, 0, 0.1f);
        runner.RotationDegrees = new Vector3(0, -35, 0);
        scene.AddChild(runner);
        model.Release();

        Pose(_swinger, "swing", 0.3);
        Pose(runner, "run", 0.15);
    }

    private static void Pose(Node3D character, string clip, double time)
    {
        var player = character.GetNode<AnimationPlayer>("AnimationPlayer");
        player.CallbackModeProcess = AnimationCallbackMode.Manual;
        player.Play(clip);
        player.Seek(time, update: true);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (Servers.Render is not { } render)
            return;
        if (gameTime.FrameCount > 2)
            _minSkinned = Math.Min(_minSkinned, render.SkinnedInstances);

        var skeleton = _swinger.GetNode<Skeleton3D>("Armature");
        var handWorld = (skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(skeleton.FindBone("Hand.R"))).Origin;
        if (gameTime.FrameCount == PickFrame && Project(handWorld) is { } pixel)
            _pick = render.PickAsync((int)pixel.X, (int)pixel.Y);

        if (gameTime.FrameCount != CheckFrame)
            return;
        _checked = true;
        if (_minSkinned != 2)
            Fail($"expected 2 skinned instances every frame, saw {_minSkinned}");
        if (_pick is not { IsCompletedSuccessfully: true } pick)
            Fail("the pick at the raised hand did not complete");
        else if (!ReferenceEquals(pick.Result.Node, _swingerBody))
            Fail($"the raised hand picked {pick.Result.Node?.Name ?? "nothing"}, not the skinned body");
        var torch = _torch.GlobalPosition;
        if (Vector3.Distance(torch, handWorld) > 0.25f)
            Fail($"the torch is at {torch}, not on the hand bone at {handWorld}");
        if (handWorld.Y < 1.8f)
            Fail($"the swing pose did not raise the hand ({handWorld})");
    }

    private Vector2? Project(Vector3 world)
    {
        var extent = Vulkan.SwapchainExtent;
        var camera = Camera.SyncRenderCamera((float)extent.Width / extent.Height);
        var clip = Vector4.Transform(new Vector4(world, 1f), camera.ViewMatrix * camera.ProjectionMatrix);
        if (clip.W <= 0f)
            return null;
        var ndc = new Vector2(clip.X, clip.Y) / clip.W;
        return new Vector2((ndc.X * 0.5f + 0.5f) * extent.Width, (0.5f - ndc.Y * 0.5f) * extent.Height);
    }

    protected override void DisposeScene()
    {
        if (!_checked)
            Fail($"the skinned self-check did not run (frame {CheckFrame})");
    }
}

/// <summary>
/// ADR 0207's budget scene: <see cref="HostOptions.Count"/> (default 12) skinned characters of about 6 000 triangles, each
/// playing a looping clip with its own offset (crossfading between run and walk every second), under a shadowing sun, in
/// two rows. <c>--perf</c> measures the frame; <c>--alloc</c> the steady state (0 B).
/// </summary>
public sealed class SkinnedCrowdScene(HostOptions host) : ShadowSceneBase(host)
{
    private readonly List<AnimationPlayer> _players = [];
    private int Count => Host.Count > 0 ? Host.Count : 12;

    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0, 3.2f, 7.5f);
        Camera.LookAt(new Vector3(0, 0.9f, 0));
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(20, 20) }, MaterialOverride = Plain(190, 195, 185) });
        scene.AddChild(Sun(new Vector3(0.4f, -0.8f, -0.45f), 0.85f));
        var model = ResourceLoader.Load<PackedScene>(SkinnedScene.CharacterPath);
        string[] clips = ["run", "walk", "idle", "craft"];
        var perRow = (Count + 1) / 2;
        for (var i = 0; i < Count; i++)
        {
            var character = model.Instantiate<Node3D>();
            character.Name = $"Crew{i}";
            var row = i / perRow;
            var column = i % perRow;
            character.Position = new Vector3((column - (perRow - 1) / 2f) * 1.1f, 0, -row * 1.6f);
            scene.AddChild(character);
            var player = character.GetNode<AnimationPlayer>("AnimationPlayer");
            player.Play(clips[i % clips.Length]);
            player.Seek(i * 0.13, update: true);
            _players.Add(player);
        }

        model.Release();
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        // Every second, the runners and walkers trade clips with a 0.15 s crossfade (steady state includes blending).
        if (gameTime.FrameCount % 60 != 0)
            return;
        for (var i = 0; i < _players.Count; i++)
        {
            var player = _players[i];
            if (i % 4 < 2)
                player.Play(player.CurrentAnimation == "run" ? "walk" : "run", customBlend: 0.15);
        }
    }

    protected override void DisposeScene()
    {
    }
}
