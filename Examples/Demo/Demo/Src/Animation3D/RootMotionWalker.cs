using MainframeEngine;

namespace Demo;

/// <summary>
/// A character body moved only by its clips' root motion (ADR 0210): it walks the <c>circle</c> clip half way round,
/// leaps forward with the <c>jump</c>, and repeats, so it traces a closed loop (two half circles joined by two jumps).
/// Every physics step it calls <see cref="CharacterBody3D.ApplyRootMotion"/>, adds gravity and moves and slides; the
/// character's <see cref="AnimationPlayer"/> runs in the physics callback mode with <c>RootMotionTrack</c> on its skeleton.
/// </summary>
public sealed class RootMotionWalker : CharacterBody3D
{
    private AnimationPlayer? _player;
    private double _walked;

    /// <summary>The character's player, relative to this body.</summary>
    [Export(NodeType = typeof(AnimationPlayer))]
    public NodePath Player { get; set; } = "Model/AnimationPlayer";

    /// <summary>How long it walks the circle (one loop is a quarter turn) before each jump.</summary>
    [Export(Range = "0.1,10,0.1")]
    public float WalkSeconds { get; set; } = 2f;

    /// <summary>The crossfade between the walk and the jump, in seconds.</summary>
    [Export(Range = "0,1,0.01")]
    public float Blend { get; set; } = 0.15f;

    /// <summary>The jumps made so far.</summary>
    public int Jumps { get; private set; }

    protected override void OnReady()
    {
        base.OnReady();
        if (Tree is { EditMode: true } || GetNodeOrNull<AnimationPlayer>(Player) is not { } player)
            return;
        _player = player;
        player.AnimationFinished += OnAnimationFinished;
        player.Play("circle");
        _walked = Blend * 0.5f; // the first walk has no blend in to lose: it stops that much earlier
    }

    protected override void OnPhysicsProcess(float delta)
    {
        if (_player is not { } player)
            return;
        // The body processes before its player (a child), so the player has walked one step less than counted here.
        if (player.CurrentAnimation == "circle" && (_walked += delta) >= WalkSeconds + delta * 0.5f)
        {
            // The blend out keeps half its time of walking, the blend in loses as much: each walk turns half a circle.
            player.Play("jump", customBlend: Blend);
            Jumps++;
        }

        ApplyRootMotion(player);
        Velocity -= UpDirection * (9.81f * delta);
        MoveAndSlide();
    }

    private void OnAnimationFinished(string animation)
    {
        if (animation != "jump" || _player is not { } player)
            return;
        _walked = 0;
        player.Play("circle", customBlend: Blend);
    }

    protected override void OnExitTree()
    {
        if (_player is { } player)
            player.AnimationFinished -= OnAnimationFinished;
        _player = null;
        base.OnExitTree();
    }
}
