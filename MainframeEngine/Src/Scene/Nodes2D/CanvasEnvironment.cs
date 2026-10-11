using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Post-processing for its viewport's 2D canvas (ADR 0185): glow, colour adjustments, lift/gamma/gain, tint, vignette,
/// chromatic aberration and film grain over the canvas layers up to <see cref="MaxLayer"/>; higher layers draw on top
/// untouched, and the game UI is never affected. Godot does this with a <c>WorldEnvironment</c> whose environment uses the
/// Canvas background mode; the engine's <see cref="WorldEnvironment"/> is 3D-only (its <see cref="PostProcessProfile"/>
/// runs on the HDR scene), so a 2D scene gets this node with the same groups. Only the root viewport's first
/// <see cref="CanvasEnvironment"/> is used; its properties are read every frame, so they can be animated.
/// </summary>
[EditorIcon("brightness-half", Family = EditorIconFamily.Space2D)]
public class CanvasEnvironment : Node
{
    private SceneViewport? _viewport;

    /// <summary>Turns the whole effect on or off.</summary>
    [Export]
    public bool Enabled { get; set; } = true;

    /// <summary>Canvas layers up to this number are post-processed (Godot's <c>background_canvas_max_layer</c>).</summary>
    [Export]
    public int MaxLayer { get; set; }

    [ExportGroup("Glow")]
    [Export(Range = "0,4,0.01")]
    public float GlowIntensity { get; set; }

    /// <summary>Gamma-space luma where glow starts (pixels above it bloom).</summary>
    [Export(Range = "0,1,0.01")]
    public float GlowThreshold { get; set; } = 0.8f;

    [Export(Range = "0,1,0.01")]
    public float GlowSoftKnee { get; set; } = 0.5f;

    /// <summary>Weight of each wider glow level: higher spreads the glow further.</summary>
    [Export(Range = "0,1,0.01")]
    public float GlowSpread { get; set; } = 0.7f;

    [ExportGroup("Adjustments")]
    [Export(Range = "0,4,0.01")]
    public float Brightness { get; set; } = 1f;

    [Export(Range = "0,4,0.01")]
    public float Contrast { get; set; } = 1f;

    [Export(Range = "0,4,0.01")]
    public float Saturation { get; set; } = 1f;

    /// <summary>Raises the shadows, per channel (0 = none).</summary>
    [Export]
    public Vector3 Lift { get; set; }

    /// <summary>Midtone power, per channel (1 = none, above 1 brightens).</summary>
    [Export]
    public Vector3 Gamma { get; set; } = Vector3.One;

    /// <summary>Scales the highlights, per channel (1 = none).</summary>
    [Export]
    public Vector3 Gain { get; set; } = Vector3.One;

    /// <summary>Multiplies the colour by <c>rgb</c> with strength <c>a</c>.</summary>
    [Export]
    public Vector4 Tint { get; set; } = new(1f, 1f, 1f, 0f);

    [ExportGroup("Vignette")]
    [Export(Range = "0,1,0.01")]
    public float VignetteStrength { get; set; }

    [Export(Range = "0,1,0.01")]
    public float VignetteRadius { get; set; } = 0.45f;

    [Export(Range = "0,1,0.01")]
    public float VignetteSoftness { get; set; } = 0.45f;

    [Export]
    public Vector3 VignetteColor { get; set; }

    [ExportGroup("Film")]
    [Export(Range = "0,0.5,0.001")]
    public float GrainAmount { get; set; }

    [Export(Range = "1,8,0.1")]
    public float GrainSize { get; set; } = 1.5f;

    [Export(Range = "0,0.02,0.0001")]
    public float ChromaticAberration { get; set; }

    /// <summary>The frame's settings as the renderer reads them.</summary>
    public CanvasPostSettings Settings => new()
    {
        Enabled = Enabled,
        MaxLayer = MaxLayer,
        GlowIntensity = GlowIntensity,
        GlowThreshold = GlowThreshold,
        GlowSoftKnee = GlowSoftKnee,
        GlowSpread = GlowSpread,
        Brightness = Brightness,
        Contrast = Contrast,
        Saturation = Saturation,
        Lift = Lift,
        Gamma = Gamma,
        Gain = Gain,
        Tint = Tint,
        VignetteStrength = VignetteStrength,
        VignetteRadius = VignetteRadius,
        VignetteSoftness = VignetteSoftness,
        VignetteColor = VignetteColor,
        GrainAmount = GrainAmount,
        GrainSize = GrainSize,
        ChromaticAberration = ChromaticAberration,
    };

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _viewport = GetViewport();
        _viewport?.AddCanvasEnvironment(this);
    }

    protected override void OnExitTree()
    {
        _viewport?.RemoveCanvasEnvironment(this);
        _viewport = null;
        base.OnExitTree();
    }
}
