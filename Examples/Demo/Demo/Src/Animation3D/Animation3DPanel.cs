using System.Numerics;
using MainframeEngine;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>
/// The Animation 3D scene's controls (ADR 0207): the clip (played with the chosen crossfade), the player's speed scale,
/// the last event a clip raised (the swing's <c>HammerFist</c>, the run's <c>Step</c>, the craft's <c>Tap</c>), the
/// ragdoll (ADR 0212): drop the character as it moves, then stand it up again, and the active ragdoll (G9.10c): Active
/// fades the drives and holds in so the character keeps its pose under physics, Push shoves its chest, and Let go fades
/// them out so it falls.
/// </summary>
public sealed class Animation3DPanel : UiDocument
{
    private RmlDataModel? _model;
    private AnimationPlayer? _player;
    private string _clip = "run";
    private float _blend = 0.15f;
    private string _lastEvent = "";
    private int _events;
    private float _activeTarget;
    private bool _fading;

    /// <summary>How long Active and Let go take to fade the drives in or out (seconds).</summary>
    public const float ActiveFade = 0.3f;

    /// <summary>The impulse Push gives the chest (N·s), sideways.</summary>
    public static readonly Vector3 PushImpulse = new(40f, 0f, 0f);

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

    /// <summary>The character is an active ragdoll (its drives are on or fading in).</summary>
    public bool IsActive => _activeTarget > 0f;

    /// <summary>
    /// Makes the character an active ragdoll (simulated, its drives and holds fading in over <see cref="ActiveFade"/>), or
    /// lets go of an active one (the drives fade out and it falls).
    /// </summary>
    public void ToggleActive()
    {
        if (Ragdoll is not { } ragdoll)
            return;
        if (IsActive)
        {
            _activeTarget = 0f;
        }
        else
        {
            if (!ragdoll.IsSimulating)
                ragdoll.PhysicalBonesStartSimulation();
            _activeTarget = 1f;
        }

        _fading = true;
        _model?.Dirty("active");
        _model?.Dirty("ragdoll");
    }

    /// <summary>Shoves the character's chest with <see cref="PushImpulse"/> (while it is simulated).</summary>
    public void Push()
    {
        if (Ragdoll is not { IsSimulating: true } ragdoll)
            return;
        foreach (var body in ragdoll.PhysicalBones)
            if (body.BoneName == "Chest")
                body.ApplyImpulse(PushImpulse);
    }

    /// <summary>Drops the character as a ragdoll (it keeps the motion of its animation), or stands it up and plays the clip again.</summary>
    public void ToggleRagdoll()
    {
        if (Ragdoll is not { } ragdoll)
            return;
        _activeTarget = 0f;
        _fading = false;
        ragdoll.DriveStrength = ragdoll.HoldStrength = 0f;
        _model?.Dirty("active");
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
            .Bind("active", this, static d => d.IsActive)
            .Event("toggleRagdoll", ToggleRagdoll)
            .Event("toggleActive", ToggleActive)
            .Event("push", Push);
    }

    // Fades the active ragdoll's drives and holds towards the target strength after Active or Let go; it leaves strengths
    // set any other way alone.
    protected override void OnProcess(in GameTime gameTime)
    {
        if (!_fading || Ragdoll is not { } ragdoll)
            return;
        var step = gameTime.DeltaTime / ActiveFade;
        var strength = ragdoll.DriveStrength < _activeTarget ? MathF.Min(ragdoll.DriveStrength + step, _activeTarget) : MathF.Max(ragdoll.DriveStrength - step, _activeTarget);
        ragdoll.DriveStrength = ragdoll.HoldStrength = strength;
        _fading = strength != _activeTarget;
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
