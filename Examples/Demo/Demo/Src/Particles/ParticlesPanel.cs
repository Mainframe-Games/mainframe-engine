using MainframeEngine;

namespace Demo;

/// <summary>The Particles scene's controls: rain on/off, glow on/off and a burst of sparks (a one-shot emitter's restart).</summary>
public sealed class ParticlesPanel : UiDocument
{
    private GpuParticles3D? _rain;
    private GpuParticles3D? _sparks;
    private WorldEnvironment? _environment;

    public ParticlesPanel()
    {
        Source = "Assets/Particles/panel.rml";
        AutoFocus = false;
    }

    /// <summary>Whether the rain box emits (its streaks finish falling when it stops); the panel's checkbox is bound to it.</summary>
    public bool Rain
    {
        get => _rain?.Emitting ?? false;
        set
        {
            if (_rain is not null)
                _rain.Emitting = value;
        }
    }

    /// <summary>Whether the world's glow is on (the flames, embers and sparks are HDR); bound to the panel's checkbox.</summary>
    public bool Glow
    {
        get => _environment?.PostProcess?.GlowEnabled ?? false;
        set
        {
            if (_environment?.PostProcess is { } post)
                post.GlowEnabled = value;
        }
    }

    /// <summary>Fires the sparks again: every one at once (Explosiveness 1, OneShot).</summary>
    public void Burst() => _sparks?.Restart();

    protected override void OnReady()
    {
        var scene = Parent!.Parent!;
        _rain = scene.GetNode<GpuParticles3D>("Rain");
        _sparks = scene.GetNode<GpuParticles3D>("Campfire/Sparks");
        _environment = scene.GetNode<WorldEnvironment>("Environment");
        CreateDataModel("particles")
            .Bind("rain", this, static d => d.Rain, static (d, v) => d.Rain = v)
            .Bind("glow", this, static d => d.Glow, static (d, v) => d.Glow = v)
            .Event("burst", Burst);
    }

    protected override void OnExitTree()
    {
        _rain = null;
        _sparks = null;
        _environment = null;
        base.OnExitTree();
    }
}
