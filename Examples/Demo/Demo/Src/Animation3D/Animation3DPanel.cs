using MainframeEngine;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>
/// The Animation 3D scene's controls (ADR 0207): the clip (played with the chosen crossfade), the player's speed scale,
/// the last event a clip raised (the swing's <c>HammerFist</c>, the run's <c>Step</c>, the craft's <c>Tap</c>), and the
/// ragdoll (ADR 0212): drop the character as it moves, then stand it up again.
/// </summary>
public sealed class Animation3DPanel : UiDocument
{
    private RmlDataModel? _model;
    private AnimationPlayer? _player;
    private string _clip = "run";
    private float _blend = 0.15f;
    private string _lastEvent = "";
    private int _events;

    public Animation3DPanel()
    {
        Source = "Assets/Animation3D/panel.rml";
        AutoFocus = false;
    }

    /// <summary>The clip playing; setting it crossfades over <see cref="Blend"/> seconds.</summary>
    public string Clip
    {
        get => _clip;
        set
        {
            _clip = value;
            _player?.Play(value, customBlend: _blend);
        }
    }

    /// <summary>The crossfade, in seconds, when the clip changes.</summary>
    public float Blend
    {
        get => _blend;
        set => _blend = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>The player's speed scale.</summary>
    public float Speed
    {
        get => _player?.SpeedScale ?? 1f;
        set
        {
            if (_player is { } player)
                player.SpeedScale = value;
        }
    }

    /// <summary>The last event raised, with how many have fired ("HammerFist (3)"), or empty.</summary>
    public string LastEvent => _lastEvent;

    /// <summary>The character's player (the imported one).</summary>
    public AnimationPlayer? Player => _player;

    /// <summary>The character's physical skeleton.</summary>
    public PhysicalBoneSimulator3D? Ragdoll { get; private set; }

    /// <summary>Drops the character as a ragdoll (it keeps the motion of its animation), or stands it up and plays the clip again.</summary>
    public void ToggleRagdoll()
    {
        if (Ragdoll is not { } ragdoll)
            return;
        if (ragdoll.IsSimulating)
        {
            ragdoll.PhysicalBonesStopSimulation();
            ragdoll.Skeleton?.ResetBonePoses();
            _player?.Play(_clip, customBlend: 0);
        }
        else
        {
            ragdoll.PhysicalBonesStartSimulation();
        }

        _model?.Dirty("ragdoll");
    }

    protected override void OnReady()
    {
        _player = Parent!.Parent!.GetNode<AnimationPlayer>("Crew/AnimationPlayer");
        Ragdoll = Parent!.Parent!.GetNodeOrNull<PhysicalBoneSimulator3D>("Crew/Armature/PhysicalBoneSimulator3D");
        _player.AnimationEvent += OnAnimationEvent;
        _player.AnimationFinished += OnAnimationFinished;
        _player.Play(_clip);
        _model = CreateDataModel("animation3d")
            .Bind("clip", this, static d => d._clip, static (d, v) => d.Clip = v)
            .Bind("blend", this, static d => d._blend, static (d, v) => d.Blend = v)
            .Bind("speed", this, static d => d.Speed, static (d, v) => d.Speed = v)
            .Bind("lastEvent", this, static d => d._lastEvent)
            .Bind("ragdoll", this, static d => d.Ragdoll?.IsSimulating ?? false)
            .Event("toggleRagdoll", ToggleRagdoll);
    }

    private void OnAnimationEvent(string animation, string name)
    {
        _events++;
        _lastEvent = $"{name} ({_events})";
        _model?.Dirty("lastEvent");
    }

    // One-shot clips (roll, swing, hit, down) go back to idle when they end.
    private void OnAnimationFinished(string animation)
    {
        _clip = "idle";
        _player?.Play("idle", customBlend: _blend);
        _model?.Dirty("clip");
    }

    protected override void OnExitTree()
    {
        if (_player is { } player)
        {
            player.AnimationEvent -= OnAnimationEvent;
            player.AnimationFinished -= OnAnimationFinished;
        }

        _player = null;
        base.OnExitTree();
    }
}
