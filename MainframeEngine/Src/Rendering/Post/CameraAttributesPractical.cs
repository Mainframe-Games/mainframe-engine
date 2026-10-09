namespace MainframeEngine;

/// <summary>
/// A camera's lens (Godot's <c>CameraAttributesPractical</c>, ADR 0168): bokeh depth of field with Godot's names and
/// defaults, plus the engine's film effects (vignette, grain, chromatic aberration). Set it on
/// <see cref="WorldEnvironment.CameraAttributes"/> for the world, or on <see cref="Camera3D.Attributes"/> for one camera;
/// the current camera's wins. Only the tree's root view is post-processed. Everything is off by default; auto exposure
/// stays on <see cref="WorldEnvironment"/> (proposal G8e, open question 6).
/// </summary>
[EditorIcon("camera")]
public class CameraAttributesPractical : Resource
{
    [ExportGroup("Depth of Field")]
    /// <summary>Blur what lies beyond <see cref="DofBlurFarDistance"/> (Godot's <c>dof_blur_far_enabled</c>).</summary>
    [Export]
    public bool DofBlurFarEnabled { get; set; }

    /// <summary>Where the far blur starts, in metres (Godot's <c>dof_blur_far_distance</c>).</summary>
    [Export(Range = "0.01,8192,0.01")]
    public float DofBlurFarDistance { get; set; } = 10f;

    /// <summary>Metres past <see cref="DofBlurFarDistance"/> until the blur is full (Godot's <c>dof_blur_far_transition</c>).</summary>
    [Export(Range = "0,8192,0.01")]
    public float DofBlurFarTransition { get; set; } = 5f;

    /// <summary>Blur what is nearer than <see cref="DofBlurNearDistance"/> (Godot's <c>dof_blur_near_enabled</c>).</summary>
    [Export]
    public bool DofBlurNearEnabled { get; set; }

    /// <summary>Where the near blur starts, in metres (Godot's <c>dof_blur_near_distance</c>).</summary>
    [Export(Range = "0.01,8192,0.01")]
    public float DofBlurNearDistance { get; set; } = 2f;

    /// <summary>Metres in front of <see cref="DofBlurNearDistance"/> until the blur is full (Godot's <c>dof_blur_near_transition</c>).</summary>
    [Export(Range = "0,8192,0.01")]
    public float DofBlurNearTransition { get; set; } = 1f;

    /// <summary>The full blur's size (Godot's <c>dof_blur_amount</c>): <c>amount × 64</c> pixels of radius at 1080 lines.</summary>
    [Export(Range = "0,1,0.001")]
    public float DofBlurAmount { get; set; } = 0.1f;

    /// <summary>Gather taps: 16, or 32 for screenshots (engine).</summary>
    [Export]
    public DepthOfFieldQuality DofQuality { get; set; }

    [ExportGroup("Film")]
    /// <summary>Darkens towards the corners (0 = off).</summary>
    [Export(Range = "0,1,0.001")]
    public float VignetteIntensity { get; set; }

    /// <summary>1: a circle; 0: an ellipse that follows the screen.</summary>
    [Export(Range = "0,1,0.01")]
    public float VignetteRoundness { get; set; } = 1f;

    /// <summary>Film grain in display values (0 = off).</summary>
    [Export(Range = "0,0.2,0.001")]
    public float FilmGrainIntensity { get; set; }

    /// <summary>Grain size in pixels.</summary>
    [Export(Range = "0.5,8,0.01")]
    public float FilmGrainSize { get; set; } = 1.5f;

    /// <summary>Red and blue sampled this many pixels apart at the corners (0 = off).</summary>
    [Export(Range = "0,16,0.01")]
    public float ChromaticAberrationIntensity { get; set; }

    /// <summary><paramref name="settings"/> with this lens's depth of field and film effects (a struct copy: no allocation).</summary>
    public PostProcessSettings ApplyTo(in PostProcessSettings settings) => settings with
    {
        DofBlurFarEnabled = DofBlurFarEnabled,
        DofBlurFarDistance = DofBlurFarDistance,
        DofBlurFarTransition = DofBlurFarTransition,
        DofBlurNearEnabled = DofBlurNearEnabled,
        DofBlurNearDistance = DofBlurNearDistance,
        DofBlurNearTransition = DofBlurNearTransition,
        DofBlurAmount = DofBlurAmount,
        DofQuality = DofQuality,
        VignetteIntensity = VignetteIntensity,
        VignetteRoundness = VignetteRoundness,
        FilmGrainIntensity = FilmGrainIntensity,
        FilmGrainSize = FilmGrainSize,
        ChromaticAberrationIntensity = ChromaticAberrationIntensity,
    };
}
