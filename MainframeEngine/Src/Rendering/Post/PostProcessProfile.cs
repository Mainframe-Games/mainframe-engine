namespace MainframeEngine;

/// <summary>
/// A world's post-processing look as a resource (ADR 0169): tonemap, auto exposure, glow, light shafts, SSAO and the colour
/// adjustments with their LUT — the screen-space half of Godot's <c>Environment</c>, which a
/// <see cref="WorldEnvironment"/> references through <see cref="WorldEnvironment.PostProcess"/>. Save it as a
/// <c>.mres</c> to share one look between scenes and tweak it in the editor, or keep it inline in a scene. Sky, ambient
/// and reflected light, fog and wind stay on the <see cref="WorldEnvironment"/>; the lens (depth of field, vignette,
/// grain, aberration) stays on <see cref="CameraAttributesPractical"/>, as in Godot.
/// </summary>
/// <remarks>
/// The renderer reads the packed <see cref="Settings"/> struct, which every setter keeps up to date (a struct copy per
/// frame, no allocation). A setter that changes a value raises <see cref="Resource.Changed"/>. Everything defaults to
/// the engine's behaviour before ADR 0124 (<see cref="PostProcessSettings.Default"/>): its own tonemap curve, no effects.
/// </remarks>
[EditorIcon("sparkles")]
public class PostProcessProfile : Resource
{
    private PostProcessSettings _settings = PostProcessSettings.Default;

    /// <summary>
    /// The packed settings the renderer uses: every property of this profile, with the lens fields (depth of field,
    /// film effects) at their defaults; <see cref="CameraAttributesPractical.ApplyTo"/> fills those in.
    /// </summary>
    public PostProcessSettings Settings => _settings;

    /// <summary>
    /// A profile holding <paramref name="settings"/>' tonemap, auto exposure, glow, light shafts, SSAO and adjustments (its
    /// lens fields are not part of a profile: they stay at their defaults; see <see cref="CameraAttributesPractical"/>).
    /// </summary>
    public static PostProcessProfile FromSettings(in PostProcessSettings settings)
    {
        var lens = new CameraAttributesPractical(); // every lens field at its default
        return new PostProcessProfile { _settings = lens.ApplyTo(settings) };
    }

    // Stores the new settings and raises Changed when they differ (record struct equality: no allocation).
    private void Set(in PostProcessSettings settings)
    {
        if (settings == _settings)
            return;
        _settings = settings;
        EmitChanged();
    }

    // ── Tonemap ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tonemap curve (ADR 0124, ADR 0168): the engine's ACES fit by default, Godot 4.7's ACES, or Godot 4.4's linear,
    /// Reinhard, filmic and AgX (Godot's <c>tonemap_mode</c>).
    /// </summary>
    [ExportGroup("Tonemap")]
    [Export]
    public Tonemapper Tonemapper
    {
        get => _settings.Tonemapper;
        set => Set(_settings with { Tonemapper = value });
    }

    /// <summary>Exposure for the Godot curves (Godot's <c>tonemap_exposure</c>); the engine curve uses the project exposure.</summary>
    [Export(Range = "0,16,0.01")]
    public float TonemapExposure
    {
        get => _settings.TonemapExposure;
        set => Set(_settings with { TonemapExposure = value });
    }

    /// <summary>White point for the Godot curves (Godot's <c>tonemap_white</c>; ACES, Reinhard and filmic use at least 1; AgX ignores it).</summary>
    [Export(Range = "0,16,0.01")]
    public float TonemapWhite
    {
        get => _settings.TonemapWhite;
        set => Set(_settings with { TonemapWhite = value });
    }

    // ── Auto exposure ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Eye adaptation (ADR 0154): the exposure follows the scene's average luminance over time.</summary>
    [ExportGroup("Auto Exposure")]
    [Export]
    public bool AutoExposureEnabled
    {
        get => _settings.AutoExposureEnabled;
        set => Set(_settings with { AutoExposureEnabled = value });
    }

    /// <summary>The luminance an average scene is exposed to (middle grey; Godot's <c>auto_exposure_scale</c>).</summary>
    [Export(Range = "0.01,16,0.01")]
    public float AutoExposureScale
    {
        get => _settings.AutoExposureScale;
        set => Set(_settings with { AutoExposureScale = value });
    }

    /// <summary>How fast the exposure adapts, per second (Godot's <c>auto_exposure_speed</c>).</summary>
    [Export(Range = "0.01,64,0.01")]
    public float AutoExposureSpeed
    {
        get => _settings.AutoExposureSpeed;
        set => Set(_settings with { AutoExposureSpeed = value });
    }

    /// <summary>The darkest average luminance adapted to (caps how much a dark scene is brightened).</summary>
    [Export(Range = "0.0001,64,0.0001")]
    public float AutoExposureMinLuminance
    {
        get => _settings.AutoExposureMinLuminance;
        set => Set(_settings with { AutoExposureMinLuminance = value });
    }

    /// <summary>The brightest average luminance adapted to (caps how much a bright scene is darkened).</summary>
    [Export(Range = "0.0001,1024,0.01")]
    public float AutoExposureMaxLuminance
    {
        get => _settings.AutoExposureMaxLuminance;
        set => Set(_settings with { AutoExposureMaxLuminance = value });
    }

    /// <summary>
    /// What is measured (ADR 0177): the image's mean log luminance (Average, the default) or a log-luminance histogram
    /// averaged between two percentiles (Histogram, Unreal's Auto Exposure Histogram).
    /// </summary>
    [Export]
    public AutoExposureMode AutoExposureMode
    {
        get => _settings.AutoExposureMode;
        set => Set(_settings with { AutoExposureMode = value });
    }

    /// <summary>Histogram: the darkest percent of the image left out of the average (Unreal's <c>AutoExposureLowPercent</c>).</summary>
    [Export(Range = "0,100,0.1")]
    public float AutoExposureLowPercent
    {
        get => _settings.AutoExposureLowPercent;
        set => Set(_settings with { AutoExposureLowPercent = value });
    }

    /// <summary>Histogram: the percentile where the average stops; brighter pixels are left out (Unreal's <c>AutoExposureHighPercent</c>).</summary>
    [Export(Range = "0,100,0.1")]
    public float AutoExposureHighPercent
    {
        get => _settings.AutoExposureHighPercent;
        set => Set(_settings with { AutoExposureHighPercent = value });
    }

    /// <summary>Histogram: log2 luminance of the darkest bin (Unreal's <c>HistogramLogMin</c>).</summary>
    [Export(Range = "-16,16,0.1")]
    public float AutoExposureHistogramLogMin
    {
        get => _settings.AutoExposureHistogramLogMin;
        set => Set(_settings with { AutoExposureHistogramLogMin = value });
    }

    /// <summary>Histogram: log2 luminance of the brightest bin (Unreal's <c>HistogramLogMax</c>).</summary>
    [Export(Range = "-16,16,0.1")]
    public float AutoExposureHistogramLogMax
    {
        get => _settings.AutoExposureHistogramLogMax;
        set => Set(_settings with { AutoExposureHistogramLogMax = value });
    }

    /// <summary>Histogram: how the image is weighted (uniformly, or towards the centre).</summary>
    [Export]
    public AutoExposureMetering AutoExposureMetering
    {
        get => _settings.AutoExposureMetering;
        set => Set(_settings with { AutoExposureMetering = value });
    }

    /// <summary>
    /// Histogram: cap the exposure so the highlight percentile stays at or below the white target (sunlit patches in a
    /// shaded view keep their colour).
    /// </summary>
    [Export]
    public bool AutoExposureHighlightProtection
    {
        get => _settings.AutoExposureHighlightProtection;
        set => Set(_settings with { AutoExposureHighlightProtection = value });
    }

    /// <summary>Histogram: the percentile highlight protection keeps in range.</summary>
    [Export(Range = "50,100,0.1")]
    public float AutoExposureHighlightPercent
    {
        get => _settings.AutoExposureHighlightPercent;
        set => Set(_settings with { AutoExposureHighlightPercent = value });
    }

    /// <summary>Histogram: the most the highlight percentile is exposed to (tonemap input; the engine curve's shoulder starts near 1).</summary>
    [Export(Range = "0.1,16,0.01")]
    public float AutoExposureHighlightWhite
    {
        get => _settings.AutoExposureHighlightWhite;
        set => Set(_settings with { AutoExposureHighlightWhite = value });
    }

    // ── Glow ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Godot 4.7's glow (ADR 0124): blurred bright areas added back before the tonemap (Godot's <c>glow_enabled</c>).</summary>
    [ExportGroup("Glow")]
    [Export]
    public bool GlowEnabled
    {
        get => _settings.GlowEnabled;
        set => Set(_settings with { GlowEnabled = value });
    }

    /// <summary>How glow is built (ADR 0168): Godot's chain, or the 13-tap / tent chain with an anti-firefly first level.</summary>
    [Export]
    public GlowQuality GlowQuality
    {
        get => _settings.GlowQuality;
        set => Set(_settings with { GlowQuality = value });
    }

    /// <summary>Weight of the half-resolution blur level (Godot's <c>glow_levels/1</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float GlowLevel1
    {
        get => _settings.GlowLevel1;
        set => Set(_settings with { GlowLevel1 = value });
    }

    /// <summary>Weight of the 1/4-resolution level (<c>glow_levels/2</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float GlowLevel2
    {
        get => _settings.GlowLevel2;
        set => Set(_settings with { GlowLevel2 = value });
    }

    /// <summary>Weight of the 1/8-resolution level (<c>glow_levels/3</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float GlowLevel3
    {
        get => _settings.GlowLevel3;
        set => Set(_settings with { GlowLevel3 = value });
    }

    /// <summary>Weight of the 1/16-resolution level (<c>glow_levels/4</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float GlowLevel4
    {
        get => _settings.GlowLevel4;
        set => Set(_settings with { GlowLevel4 = value });
    }

    /// <summary>Weight of the 1/32-resolution level (<c>glow_levels/5</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float GlowLevel5
    {
        get => _settings.GlowLevel5;
        set => Set(_settings with { GlowLevel5 = value });
    }

    /// <summary>Weight of the 1/64-resolution level (<c>glow_levels/6</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float GlowLevel6
    {
        get => _settings.GlowLevel6;
        set => Set(_settings with { GlowLevel6 = value });
    }

    /// <summary>Weight of the 1/128-resolution level (<c>glow_levels/7</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float GlowLevel7
    {
        get => _settings.GlowLevel7;
        set => Set(_settings with { GlowLevel7 = value });
    }

    /// <summary>Divide the level weights by their sum (Godot's <c>glow_normalized</c>).</summary>
    [Export]
    public bool GlowNormalized
    {
        get => _settings.GlowNormalized;
        set => Set(_settings with { GlowNormalized = value });
    }

    /// <summary>Godot's <c>glow_intensity</c>.</summary>
    [Export(Range = "0,8,0.01")]
    public float GlowIntensity
    {
        get => _settings.GlowIntensity;
        set => Set(_settings with { GlowIntensity = value });
    }

    /// <summary>Multiplies every blur level (Godot's <c>glow_strength</c>).</summary>
    [Export(Range = "0,2,0.01")]
    public float GlowStrength
    {
        get => _settings.GlowStrength;
        set => Set(_settings with { GlowStrength = value });
    }

    /// <summary>The glow share in Mix mode (Godot's <c>glow_mix</c>).</summary>
    [Export(Range = "0,1,0.001")]
    public float GlowMix
    {
        get => _settings.GlowMix;
        set => Set(_settings with { GlowMix = value });
    }

    /// <summary>Minimum glow below the threshold (Godot's <c>glow_bloom</c>).</summary>
    [Export(Range = "0,1,0.01")]
    public float GlowBloom
    {
        get => _settings.GlowBloom;
        set => Set(_settings with { GlowBloom = value });
    }

    /// <summary>How glow combines with the scene (Godot's <c>glow_blend_mode</c>, default Screen).</summary>
    [Export]
    public GlowBlendMode GlowBlendMode
    {
        get => _settings.GlowBlendMode;
        set => Set(_settings with { GlowBlendMode = value });
    }

    /// <summary>Brightness where glow starts (Godot's <c>glow_hdr_threshold</c>).</summary>
    [Export(Range = "0,4,0.01")]
    public float GlowHdrThreshold
    {
        get => _settings.GlowHdrThreshold;
        set => Set(_settings with { GlowHdrThreshold = value });
    }

    /// <summary>Range over which glow fades in above the threshold (Godot's <c>glow_hdr_scale</c>).</summary>
    [Export(Range = "0,4,0.01")]
    public float GlowHdrScale
    {
        get => _settings.GlowHdrScale;
        set => Set(_settings with { GlowHdrScale = value });
    }

    /// <summary>Brightest a glow sample can be (Godot's <c>glow_hdr_luminance_cap</c>).</summary>
    [Export(Range = "0,256,0.01")]
    public float GlowHdrLuminanceCap
    {
        get => _settings.GlowHdrLuminanceCap;
        set => Set(_settings with { GlowHdrLuminanceCap = value });
    }

    // ── Light shafts ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Screen-space light shafts (ADR 0160): the sky around the sun, the world's first <see cref="DirectionalLight3D"/>,
    /// streaked towards it where leaves, trunks and buildings leave gaps.
    /// </summary>
    [ExportGroup("Light Shafts")]
    [Export]
    public bool LightShaftsEnabled
    {
        get => _settings.LightShaftsEnabled;
        set => Set(_settings with { LightShaftsEnabled = value });
    }

    /// <summary>How much of the shafts is added to the scene.</summary>
    [Export(Range = "0,16,0.01")]
    public float LightShaftsIntensity
    {
        get => _settings.LightShaftsIntensity;
        set => Set(_settings with { LightShaftsIntensity = value });
    }

    /// <summary>The weight left after each sixteenth of a shaft (lower is shorter).</summary>
    [Export(Range = "0,1,0.001")]
    public float LightShaftsDecay
    {
        get => _settings.LightShaftsDecay;
        set => Set(_settings with { LightShaftsDecay = value });
    }

    /// <summary>The fraction of the way to the sun each shaft reaches.</summary>
    [Export(Range = "0,1,0.01")]
    public float LightShaftsDensity
    {
        get => _settings.LightShaftsDensity;
        set => Set(_settings with { LightShaftsDensity = value });
    }

    /// <summary>Taps per blur pass (4–64; two passes, so samples² per shaft).</summary>
    [Export(Range = "4,64,1")]
    public int LightShaftsSamples
    {
        get => _settings.LightShaftsSamples;
        set => Set(_settings with { LightShaftsSamples = value });
    }

    // ── SSAO ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Screen-space ambient occlusion (ADR 0165; GTAO from the depth prepass): creases, contacts and the ground under
    /// objects lose ambient and reflected light (Godot's <c>ssao_enabled</c>). It turns the depth prepass on.
    /// </summary>
    [ExportGroup("SSAO")]
    [Export]
    public bool SsaoEnabled
    {
        get => _settings.SsaoEnabled;
        set => Set(_settings with { SsaoEnabled = value });
    }

    /// <summary>How far occluders reach, in metres (Godot's <c>ssao_radius</c>).</summary>
    [Export(Range = "0.01,16,0.01")]
    public float SsaoRadius
    {
        get => _settings.SsaoRadius;
        set => Set(_settings with { SsaoRadius = value });
    }

    /// <summary>Multiplies the occlusion (Godot's <c>ssao_intensity</c>; 1 with power 1 is the physical value).</summary>
    [Export(Range = "0,16,0.01")]
    public float SsaoIntensity
    {
        get => _settings.SsaoIntensity;
        set => Set(_settings with { SsaoIntensity = value });
    }

    /// <summary>Exponent of the occlusion: darker, with a sharper falloff (Godot's <c>ssao_power</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float SsaoPower
    {
        get => _settings.SsaoPower;
        set => Set(_settings with { SsaoPower = value });
    }

    /// <summary>Strength of the near-field detail term: small creases darken more (Godot's <c>ssao_detail</c>).</summary>
    [Export(Range = "0,5,0.01")]
    public float SsaoDetail
    {
        get => _settings.SsaoDetail;
        set => Set(_settings with { SsaoDetail = value });
    }

    /// <summary>Occluders lower than this fraction of 90° above the surface do not count (Godot's <c>ssao_horizon</c>).</summary>
    [Export(Range = "0,1,0.01")]
    public float SsaoHorizon
    {
        get => _settings.SsaoHorizon;
        set => Set(_settings with { SsaoHorizon = value });
    }

    /// <summary>How strictly the blur stops at depth edges (Godot's <c>ssao_sharpness</c>).</summary>
    [Export(Range = "0,1,0.01")]
    public float SsaoSharpness
    {
        get => _settings.SsaoSharpness;
        set => Set(_settings with { SsaoSharpness = value });
    }

    /// <summary>How much the occlusion also darkens direct light (Godot's <c>ssao_light_affect</c>; 0 = only ambient).</summary>
    [Export(Range = "0,1,0.01")]
    public float SsaoLightAffect
    {
        get => _settings.SsaoLightAffect;
        set => Set(_settings with { SsaoLightAffect = value });
    }

    /// <summary>0: the darker of SSAO and the material's AO; 1: their product (Godot's <c>ssao_ao_channel_affect</c>).</summary>
    [Export(Range = "0,1,0.01")]
    public float SsaoAoChannelAffect
    {
        get => _settings.SsaoAoChannelAffect;
        set => Set(_settings with { SsaoAoChannelAffect = value });
    }

    // ── Adjustments ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Colour adjustments after the tonemap (Godot's <c>adjustment_enabled</c>, ADR 0168).</summary>
    [ExportGroup("Adjustments")]
    [Export]
    public bool AdjustmentEnabled
    {
        get => _settings.AdjustmentEnabled;
        set => Set(_settings with { AdjustmentEnabled = value });
    }

    /// <summary>Godot's <c>adjustment_brightness</c> (1 = unchanged).</summary>
    [Export(Range = "0.01,8,0.01")]
    public float AdjustmentBrightness
    {
        get => _settings.AdjustmentBrightness;
        set => Set(_settings with { AdjustmentBrightness = value });
    }

    /// <summary>Godot's <c>adjustment_contrast</c> (1 = unchanged).</summary>
    [Export(Range = "0.01,8,0.01")]
    public float AdjustmentContrast
    {
        get => _settings.AdjustmentContrast;
        set => Set(_settings with { AdjustmentContrast = value });
    }

    /// <summary>Godot's <c>adjustment_saturation</c> (1 = unchanged).</summary>
    [Export(Range = "0.01,8,0.01")]
    public float AdjustmentSaturation
    {
        get => _settings.AdjustmentSaturation;
        set => Set(_settings with { AdjustmentSaturation = value });
    }

    /// <summary>Godot's <c>adjustment_color_correction</c>: a 3D LUT (a <c>.cube</c> file) the display colour is looked up in.</summary>
    [Export]
    public Texture3D? AdjustmentColorCorrection
    {
        get => _settings.AdjustmentColorCorrection;
        set => Set(_settings with { AdjustmentColorCorrection = value });
    }

    /// <summary>How much of the LUT's result is used (engine; 1 = all of it).</summary>
    [Export(Range = "0,1,0.01")]
    public float AdjustmentColorCorrectionStrength
    {
        get => _settings.AdjustmentColorCorrectionStrength;
        set => Set(_settings with { AdjustmentColorCorrectionStrength = value });
    }

    /// <summary>
    /// The names of the <see cref="WorldEnvironment"/> properties a scene saved before ADR 0169 held, which load into an
    /// inline profile (<see cref="WorldEnvironment"/>'s version 1 → 2 migration): exactly this type's exported members.
    /// </summary>
    internal static readonly string[] MovedPropertyNames =
    [
        nameof(Tonemapper), nameof(TonemapExposure), nameof(TonemapWhite),
        nameof(AutoExposureEnabled), nameof(AutoExposureScale), nameof(AutoExposureSpeed), nameof(AutoExposureMinLuminance),
        nameof(AutoExposureMaxLuminance),
        // ADR 0177: no version-1 scene has these, but the list stays exactly the exported members.
        nameof(AutoExposureMode), nameof(AutoExposureLowPercent), nameof(AutoExposureHighPercent), nameof(AutoExposureHistogramLogMin),
        nameof(AutoExposureHistogramLogMax), nameof(AutoExposureMetering), nameof(AutoExposureHighlightProtection),
        nameof(AutoExposureHighlightPercent), nameof(AutoExposureHighlightWhite),
        nameof(GlowEnabled), nameof(GlowQuality), nameof(GlowLevel1), nameof(GlowLevel2), nameof(GlowLevel3), nameof(GlowLevel4),
        nameof(GlowLevel5), nameof(GlowLevel6), nameof(GlowLevel7), nameof(GlowNormalized), nameof(GlowIntensity), nameof(GlowStrength),
        nameof(GlowMix), nameof(GlowBloom), nameof(GlowBlendMode), nameof(GlowHdrThreshold), nameof(GlowHdrScale), nameof(GlowHdrLuminanceCap),
        nameof(LightShaftsEnabled), nameof(LightShaftsIntensity), nameof(LightShaftsDecay), nameof(LightShaftsDensity), nameof(LightShaftsSamples),
        nameof(SsaoEnabled), nameof(SsaoRadius), nameof(SsaoIntensity), nameof(SsaoPower), nameof(SsaoDetail), nameof(SsaoHorizon),
        nameof(SsaoSharpness), nameof(SsaoLightAffect), nameof(SsaoAoChannelAffect),
        nameof(AdjustmentEnabled), nameof(AdjustmentBrightness), nameof(AdjustmentContrast), nameof(AdjustmentSaturation),
        nameof(AdjustmentColorCorrection), nameof(AdjustmentColorCorrectionStrength),
    ];
}
