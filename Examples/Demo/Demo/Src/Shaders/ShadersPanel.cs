using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>
/// The Shaders scene's controls, all through <see cref="ShaderMaterial.SetShaderParameter"/>: the wind's strength (the
/// stalks' <c>sway</c>), the props' dissolve (cycling, or held by the slider) and a hit flash that fades out.
/// </summary>
public sealed class ShadersPanel : UiDocument
{
    /// <summary>Seconds of one dissolve cycle (solid → gone → solid).</summary>
    public const float CycleSeconds = 6f;

    /// <summary>Seconds a hit flash takes to fade.</summary>
    public const float FlashSeconds = 0.4f;

    private ShaderMaterial? _sway;
    private readonly List<(ShaderMaterial Material, float Phase)> _props = [];
    private float _time;
    private float _flash;

    public ShadersPanel()
    {
        Source = "Assets/Shaders/panel.rml";
        AutoFocus = false;
    }

    /// <summary>The props dissolve and come back on their own; off, <see cref="Dissolve"/> holds them.</summary>
    public bool Animate { get; set; } = true;

    /// <summary>The dissolve progress while <see cref="Animate"/> is off (0 solid, 1 gone).</summary>
    public float Dissolve { get; set; } = 0.4f;

    /// <summary>The stalks' sway amplitude (the shader's <c>sway</c> uniform).</summary>
    public float Wind
    {
        get => _sway?.GetShaderParameter("sway") is float sway ? sway : 0.4f;
        set => _sway?.SetShaderParameter("sway", value);
    }

    /// <summary>Flashes every prop (the shader's <c>hit_flash</c> alpha), fading over <see cref="FlashSeconds"/>.</summary>
    public void Hit() => _flash = 1f;

    protected override void OnReady()
    {
        var scene = Parent!.Parent!;
        _sway = scene.GetNode<MultiMeshInstance3D>("Stalks").MaterialOverride as ShaderMaterial;
        foreach (var prop in ShadersScene.Props)
            if (scene.GetNode<MeshInstance3D>("Props/" + prop.Name).MaterialOverride is ShaderMaterial material)
                _props.Add((material, prop.Phase));
        CreateDataModel("shaders")
            .Bind("animate", this, static d => d.Animate, static (d, v) => d.Animate = v)
            .Bind("dissolve", this, static d => d.Dissolve, static (d, v) => d.Dissolve = v)
            .Bind("wind", this, static d => d.Wind, static (d, v) => d.Wind = v)
            .Event("hit", Hit);
        Update(0f);
    }

    protected override void OnProcess(in GameTime gameTime) => Update(gameTime.DeltaTime);

    private void Update(float delta)
    {
        _time += delta;
        _flash = MathF.Max(0f, _flash - delta / FlashSeconds);
        foreach (var (material, phase) in _props)
        {
            // Smooth in and out: most of the cycle solid or nearly gone, the edge sweeping through between.
            var progress = Animate ? 0.5f - 0.5f * MathF.Cos((_time / CycleSeconds + phase) * MathF.Tau) : Dissolve;
            material.SetShaderParameter("progress", progress * 1.02f);
            material.SetShaderParameter("hit_flash", new Vector4(1f, 0.35f, 0.3f, _flash * 0.8f));
        }
    }
}
