namespace MainframeEngine;

/// <summary>Which tonemap curve turns the HDR scene into display values (ADR 0124).</summary>
public enum Tonemapper
{
    /// <summary>The engine's curve: Stephen Hill's ACES fit, scaled by the project exposure (<c>rendering.exposure</c>).</summary>
    Engine,

    /// <summary>
    /// Godot 4.7's ACES (<c>tonemap_mode = ACES</c>): the same fit with Godot's 1.8 input bias and its output divided by
    /// the curve at <see cref="PostProcessSettings.TonemapWhite"/>, scaled by <see cref="PostProcessSettings.TonemapExposure"/>.
    /// </summary>
    GodotAces,

    /// <summary>Godot's <c>TONE_MAPPER_LINEAR</c>: exposure only, clipped at 1 (<see cref="PostProcessSettings.TonemapExposure"/>).</summary>
    Linear,

    /// <summary>Godot 4.4's extended Reinhard (<c>TONE_MAPPER_REINHARDT</c>): <c>c (1 + c / white²) / (1 + c)</c>.</summary>
    Reinhard,

    /// <summary>Godot 4.4's filmic curve (<c>TONE_MAPPER_FILMIC</c>: Hable's, with a 2× exposure bias), divided by the curve at white.</summary>
    Filmic,

    /// <summary>
    /// Godot 4.4's AgX (<c>TONE_MAPPER_AGX</c>, Blender's look from EaryChow's AgX base): a log2 encoding in an inset Rec.2020
    /// space, a sigmoid and an outset back to sRGB. Bright, saturated light desaturates towards white the way film does
    /// (the sun through leaves goes white, not ACES' saturated yellow). Ignores <see cref="PostProcessSettings.TonemapWhite"/>.
    /// </summary>
    Agx,
}

/// <summary>How the glow chain is built (engine setting; ADR 0168).</summary>
public enum GlowQuality
{
    /// <summary>Godot 4.7's glow (ADR 0124): per level, a 2 × 2 downsample and a separable 9-tap Gaussian; levels gathered bicubically.</summary>
    Standard,

    /// <summary>
    /// Jimenez 2014 (Call of Duty: Advanced Warfare): a 13-tap downsample per level with a Karis average on the first (no
    /// fireflies from sun glints), then a 3 × 3 tent upsample chain that adds each weighted level on the way up. Energy
    /// conserving, wider and smoother; one combined half-resolution image is composited.
    /// </summary>
    High,
}

/// <summary>Bokeh depth of field gather taps (engine setting; ADR 0168).</summary>
public enum DepthOfFieldQuality
{
    /// <summary>16 golden-angle taps (gameplay).</summary>
    Standard,

    /// <summary>32 taps (screenshots, photo mode).</summary>
    High,
}

/// <summary>How glow is combined with the scene (Godot's <c>Environment.GlowBlendMode</c>, same ordinals).</summary>
public enum GlowBlendMode
{
    Additive,
    Screen,
    SoftLight,
    Replace,
    Mix,
}

/// <summary>
/// The tonemap, glow, auto exposure, light shafts and screen-space ambient occlusion of a world (Godot 4.7's <c>Environment</c> tonemap and glow properties, ADR 0124), set on its
/// <see cref="WorldEnvironment"/> and applied by the renderer's tonemap pass. <see cref="Default"/> is the engine's
/// behaviour before ADR 0124: its own curve and no glow.
/// </summary>
public readonly record struct PostProcessSettings
{
    public const int GlowLevelCount = 7;

    public static PostProcessSettings Default { get; } = new();

    public PostProcessSettings()
    {
    }

    public Tonemapper Tonemapper { get; init; } = Tonemapper.Engine;

    /// <summary>Exposure for <see cref="MainframeEngine.Tonemapper.GodotAces"/> (Godot's <c>tonemap_exposure</c>, default 1).</summary>
    public float TonemapExposure { get; init; } = 1f;

    /// <summary>White point for <see cref="MainframeEngine.Tonemapper.GodotAces"/> (Godot's <c>tonemap_white</c>; ACES uses at least 1).</summary>
    public float TonemapWhite { get; init; } = 1f;

    public bool GlowEnabled { get; init; }

    // Godot's glow_levels/1..7 defaults (4.7): 0, 0.8, 0.4, 0.1, 0, 0, 0.
    public float GlowLevel1 { get; init; }
    public float GlowLevel2 { get; init; } = 0.8f;
    public float GlowLevel3 { get; init; } = 0.4f;
    public float GlowLevel4 { get; init; } = 0.1f;
    public float GlowLevel5 { get; init; }
    public float GlowLevel6 { get; init; }
    public float GlowLevel7 { get; init; }

    /// <summary>Divide the levels by their sum (Godot's <c>glow_normalized</c>).</summary>
    public bool GlowNormalized { get; init; }

    public float GlowIntensity { get; init; } = 0.3f;

    /// <summary>Multiplies every blur pass (Godot's <c>glow_strength</c>): level k carries <c>strength^(k+1)</c>.</summary>
    public float GlowStrength { get; init; } = 1f;

    /// <summary>The glow share in <see cref="GlowBlendMode.Mix"/> (Godot's <c>glow_mix</c>).</summary>
    public float GlowMix { get; init; } = 0.05f;

    /// <summary>Minimum glow feedback below the threshold (Godot's <c>glow_bloom</c>).</summary>
    public float GlowBloom { get; init; }

    public GlowBlendMode GlowBlendMode { get; init; } = GlowBlendMode.Screen;

    public float GlowHdrThreshold { get; init; } = 1f;
    public float GlowHdrScale { get; init; } = 2f;
    public float GlowHdrLuminanceCap { get; init; } = 12f;

    /// <summary>
    /// How glow is built (engine, ADR 0168): <see cref="MainframeEngine.GlowQuality.Standard"/> is Godot's chain, unchanged;
    /// <see cref="MainframeEngine.GlowQuality.High"/> the 13-tap / tent chain with an anti-firefly first level.
    /// </summary>
    public GlowQuality GlowQuality { get; init; } = GlowQuality.Standard;

    /// <summary>
    /// Colour adjustments after the tonemap (Godot's <c>adjustment_enabled</c>): brightness, contrast, saturation and the
    /// colour-correction LUT, applied to the display-encoded image (ADR 0168).
    /// </summary>
    public bool AdjustmentEnabled { get; init; }

    /// <summary>Godot's <c>adjustment_brightness</c>: multiplies the display value (1 = unchanged).</summary>
    public float AdjustmentBrightness { get; init; } = 1f;

    /// <summary>Godot's <c>adjustment_contrast</c>: scales the display value around 0.5 (1 = unchanged).</summary>
    public float AdjustmentContrast { get; init; } = 1f;

    /// <summary>Godot's <c>adjustment_saturation</c>: scales the distance from the channel mean (1 = unchanged, 0 = grey).</summary>
    public float AdjustmentSaturation { get; init; } = 1f;

    /// <summary>
    /// Godot's <c>adjustment_color_correction</c> as a 3D LUT (a <see cref="Texture3D"/>, usually a <c>.cube</c> file through
    /// <see cref="CubeLutImporter"/>): the display value is looked up with trilinear filtering after brightness, contrast
    /// and saturation. Null: no lookup.
    /// </summary>
    public Texture3D? AdjustmentColorCorrection { get; init; }

    /// <summary>How much of the LUT's result replaces the input (engine; 0–1, 1 = the LUT alone).</summary>
    public float AdjustmentColorCorrectionStrength { get; init; } = 1f;

    /// <summary>Blur what lies beyond <see cref="DofBlurFarDistance"/> (Godot's <c>CameraAttributesPractical.dof_blur_far_enabled</c>).</summary>
    public bool DofBlurFarEnabled { get; init; }

    /// <summary>Where the far blur starts, in metres from the camera (Godot's <c>dof_blur_far_distance</c>, 10).</summary>
    public float DofBlurFarDistance { get; init; } = 10f;

    /// <summary>Over how many metres past <see cref="DofBlurFarDistance"/> the far blur reaches its full size (5).</summary>
    public float DofBlurFarTransition { get; init; } = 5f;

    /// <summary>Blur what is nearer than <see cref="DofBlurNearDistance"/> (Godot's <c>dof_blur_near_enabled</c>).</summary>
    public bool DofBlurNearEnabled { get; init; }

    /// <summary>Where the near blur starts, in metres (Godot's <c>dof_blur_near_distance</c>, 2): sharp from here on out.</summary>
    public float DofBlurNearDistance { get; init; } = 2f;

    /// <summary>Over how many metres in front of <see cref="DofBlurNearDistance"/> the near blur reaches its full size (1).</summary>
    public float DofBlurNearTransition { get; init; } = 1f;

    /// <summary>
    /// The full blur's size (Godot's <c>dof_blur_amount</c>, 0.1; 0–1): the bokeh radius is <c>amount × 64</c> pixels at
    /// 1080 lines, scaled with the image height (<see cref="DofMaxRadius"/>).
    /// </summary>
    public float DofBlurAmount { get; init; } = 0.1f;

    /// <summary>Gather taps (engine): 16, or 32 for screenshots.</summary>
    public DepthOfFieldQuality DofQuality { get; init; } = DepthOfFieldQuality.Standard;

    /// <summary>Darkens the image towards its corners (engine; 0 = off; at 1 the corners are black).</summary>
    public float VignetteIntensity { get; init; }

    /// <summary>The vignette's shape: 1 a circle, 0 an ellipse that follows the screen's aspect.</summary>
    public float VignetteRoundness { get; init; } = 1f;

    /// <summary>Film grain's strength in display values (engine; 0 = off; 0.015 is about ±4 of 255 in the mid-tones).</summary>
    public float FilmGrainIntensity { get; init; }

    /// <summary>Grain size in pixels (engine, 1.5).</summary>
    public float FilmGrainSize { get; init; } = 1.5f;

    /// <summary>
    /// Lateral chromatic aberration (engine; 0 = off): red and blue are sampled this many pixels apart, radially, at the
    /// corners (less towards the centre).
    /// </summary>
    public float ChromaticAberrationIntensity { get; init; }

    /// <summary>
    /// Eye adaptation (ADR 0154): the scene's average log luminance, measured on the GPU each frame, adapts over time and
    /// sets the exposure to <see cref="AutoExposureScale"/> / luminance. The manual exposure (<c>rendering.exposure</c> or
    /// <see cref="TonemapExposure"/>) still multiplies it, as compensation.
    /// </summary>
    public bool AutoExposureEnabled { get; init; }

    /// <summary>The luminance an average scene is exposed to (middle grey; Godot's <c>auto_exposure_scale</c>, 0.4).</summary>
    public float AutoExposureScale { get; init; } = 0.4f;

    /// <summary>Adaptation rate per second (Godot's <c>auto_exposure_speed</c>, 0.5): each frame closes <c>1 − e^(−dt·speed)</c> of the gap.</summary>
    public float AutoExposureSpeed { get; init; } = 0.5f;

    /// <summary>The darkest average luminance adapted to: darker scenes stay darker (the most exposure is Scale / this).</summary>
    public float AutoExposureMinLuminance { get; init; } = 0.05f;

    /// <summary>The brightest average luminance adapted to: brighter scenes stay brighter (the least exposure is Scale / this).</summary>
    public float AutoExposureMaxLuminance { get; init; } = 2f;

    /// <summary>
    /// Screen-space light shafts (ADR 0160): sky pixels around the sun (the world's first <see cref="DirectionalLight3D"/>),
    /// blurred radially towards its screen position and added to the HDR scene before the tonemap, alongside glow. They
    /// need the sun on screen or near it, and fade out as it leaves.
    /// </summary>
    public bool LightShaftsEnabled { get; init; }

    /// <summary>Multiplies the shafts added to the scene (in scene radiance, before exposure).</summary>
    public float LightShaftsIntensity { get; init; } = 1f;

    /// <summary>
    /// The weight left after each sixteenth of a ray (GPU Gems 3's per-sample decay at 16 samples, independent of
    /// <see cref="LightShaftsSamples"/>): a shaft at the far end of its ray keeps <c>decay^16</c>. Lower is shorter.
    /// </summary>
    public float LightShaftsDecay { get; init; } = 0.96f;

    /// <summary>The fraction of the way to the sun each pixel's ray covers (GPU Gems 3's density, 0–1): longer shafts, coarser taps.</summary>
    public float LightShaftsDensity { get; init; } = 0.8f;

    /// <summary>Taps per blur pass (clamped to 4–64); the two passes give <c>samples²</c> effective taps per ray.</summary>
    public int LightShaftsSamples { get; init; } = 16;

    /// <summary>
    /// Screen-space ambient occlusion (ADR 0165): ground-truth ambient occlusion (GTAO, Jimenez 2016) at half resolution
    /// from the depth prepass, denoised and upsampled to full resolution, darkening the ambient and reflected light of
    /// lit surfaces in creases and contacts (Godot's <c>ssao_enabled</c>). Turns the depth prepass on. Does not change
    /// the tonemap (<see cref="PostEffectSettings.PostTonemap"/> ignores the SSAO settings).
    /// </summary>
    public bool SsaoEnabled { get; init; }

    /// <summary>How far (in metres) occluders reach (Godot's <c>ssao_radius</c>); on screen at most a fifth of the image height.</summary>
    public float SsaoRadius { get; init; } = 1f;

    /// <summary>
    /// Multiplies the occlusion (Godot's <c>ssao_intensity</c>): <c>ao = (1 − intensity · (1 − visibility))^power</c>,
    /// where visibility is GTAO's cosine-weighted visible fraction. 1 (with power 1) is the physical value.
    /// </summary>
    public float SsaoIntensity { get; init; } = 2f;

    /// <summary>Exponent of the AO after the intensity (Godot's <c>ssao_power</c>): higher is darker with a sharper falloff.</summary>
    public float SsaoPower { get; init; } = 1.5f;

    /// <summary>
    /// How much a second, near-field term (occluders within a quarter of the radius, the same samples) adds (Godot's
    /// <c>ssao_detail</c>, 0–5): small creases and contacts darken more. 0 turns it off.
    /// </summary>
    public float SsaoDetail { get; init; } = 0.5f;

    /// <summary>
    /// The horizon threshold (Godot's <c>ssao_horizon</c>, 0–1): occluders rising less than this fraction of 90° above a
    /// surface's tangent plane do not occlude it (1: nothing does). Hides the self-occlusion of slightly bumpy surfaces.
    /// </summary>
    public float SsaoHorizon { get; init; } = 0.06f;

    /// <summary>
    /// How strictly the denoise and upsample stop at depth edges (Godot's <c>ssao_sharpness</c>, 0–1): lower blurs the AO
    /// across object edges, higher keeps it to its own surface.
    /// </summary>
    public float SsaoSharpness { get; init; } = 0.98f;

    /// <summary>
    /// How much the AO also darkens direct light (Godot's <c>ssao_light_affect</c>, 0–1). Ambient occlusion is physically an
    /// indirect-light effect: 0 keeps sunlit surfaces as they are.
    /// </summary>
    public float SsaoLightAffect { get; init; }

    /// <summary>
    /// How SSAO combines with a material's own AO (ORM red, foliage vertex AO; Godot's <c>ssao_ao_channel_affect</c>, 0–1):
    /// 0 takes the darker of the two (SSAO adds nothing where the material is already darker), 1 multiplies them.
    /// </summary>
    public float SsaoAoChannelAffect { get; init; }

    /// <summary>These settings with every SSAO setting at its default (what the tonemap decision compares).</summary>
    public PostProcessSettings WithoutSsao() => this with
    {
        SsaoEnabled = false,
        SsaoRadius = Default.SsaoRadius,
        SsaoIntensity = Default.SsaoIntensity,
        SsaoPower = Default.SsaoPower,
        SsaoDetail = Default.SsaoDetail,
        SsaoHorizon = Default.SsaoHorizon,
        SsaoSharpness = Default.SsaoSharpness,
        SsaoLightAffect = Default.SsaoLightAffect,
        SsaoAoChannelAffect = Default.SsaoAoChannelAffect,
    };

    /// <summary>Taps per light-shaft blur pass: <see cref="LightShaftsSamples"/> clamped to [4, 64].</summary>
    public int LightShaftsTapsPerPass => Math.Clamp(LightShaftsSamples, 4, 64);

    /// <summary>
    /// The step and per-tap decay of light-shaft blur pass <paramref name="pass"/> (0: fine, 1: coarse). Each pixel's ray
    /// towards the sun is <see cref="LightShaftsDensity"/> of the way there, covered by <c>n²</c> effective taps for
    /// <c>n</c> = <see cref="LightShaftsTapsPerPass"/>: the fine pass steps <c>density / n²</c> with decay
    /// <c>decay^(16 / n²)</c>, the coarse pass <c>density / n</c> with <c>decay^(16 / n)</c>, so tap <c>i·n + j</c> of the
    /// combined ray weighs <c>decay^(16 (i·n + j) / n²)</c>.
    /// </summary>
    public (float Step, float TapDecay) LightShaftsPass(int pass)
    {
        var n = (float)LightShaftsTapsPerPass;
        var taps = pass == 0 ? n * n : n;
        var density = Math.Clamp(LightShaftsDensity, 0f, 1f);
        var decay = Math.Clamp(LightShaftsDecay, 0f, 1f);
        return (density / taps, MathF.Pow(decay, 16f / taps));
    }

    /// <summary>True when depth of field blurs anything (a plane enabled and a non-zero amount).</summary>
    public bool DofEnabled => (DofBlurFarEnabled || DofBlurNearEnabled) && DofBlurAmount > 0f;

    /// <summary>The full bokeh radius in pixels for an image <paramref name="height"/> pixels tall (<c>amount × 64</c> at 1080).</summary>
    public float DofMaxRadius(float height) => Math.Clamp(DofBlurAmount, 0f, 1f) * 64f * height / 1080f;

    /// <summary>
    /// The blur at <paramref name="distance"/> metres from the camera, 0 (sharp) to 1 (the full radius): linear over each
    /// plane's transition (a transition of 0 is a hard edge), the larger of the near and far terms.
    /// </summary>
    public float DofBlur(float distance)
    {
        var far = DofBlurFarEnabled ? Ramp(distance - DofBlurFarDistance, DofBlurFarTransition) : 0f;
        var near = DofBlurNearEnabled ? Ramp(DofBlurNearDistance - distance, DofBlurNearTransition) : 0f;
        return MathF.Max(far, near);

        static float Ramp(float beyond, float transition) =>
            transition > 0f ? Math.Clamp(beyond / transition, 0f, 1f) : beyond > 0f ? 1f : 0f;
    }

    /// <summary>True when the colour-grade pass has something to do: adjustments, a LUT, vignette, grain or aberration.</summary>
    public bool ColorGradeEnabled =>
        (AdjustmentEnabled && (AdjustmentBrightness != 1f || AdjustmentContrast != 1f || AdjustmentSaturation != 1f ||
                               (AdjustmentColorCorrection is { IsEmpty: false } && AdjustmentColorCorrectionStrength > 0f))) ||
        VignetteIntensity > 0f || FilmGrainIntensity > 0f || ChromaticAberrationIntensity > 0f;

    /// <summary>The fraction of the gap to the measured luminance that auto exposure closes over <paramref name="deltaTime"/> seconds.</summary>
    public float AutoExposureBlend(float deltaTime) =>
        Math.Clamp(1f - MathF.Exp(-MathF.Max(deltaTime, 0f) * MathF.Max(AutoExposureSpeed, 0f)), 0f, 1f);

    /// <summary>The exposure auto exposure applies for an adapted average <paramref name="luminance"/> (before compensation).</summary>
    public float AutoExposureFor(float luminance) =>
        AutoExposureScale / Math.Clamp(luminance, MathF.Max(AutoExposureMinLuminance, 1e-6f), MathF.Max(AutoExposureMaxLuminance, AutoExposureMinLuminance));

    /// <summary>The level weights the tonemap uses: as set, or divided by their sum when <see cref="GlowNormalized"/> (Godot's <c>_update_glow</c>).</summary>
    public void GetGlowWeights(Span<float> weights)
    {
        if (weights.Length < GlowLevelCount)
            throw new ArgumentException($"needs {GlowLevelCount} entries", nameof(weights));
        weights[0] = GlowLevel1;
        weights[1] = GlowLevel2;
        weights[2] = GlowLevel3;
        weights[3] = GlowLevel4;
        weights[4] = GlowLevel5;
        weights[5] = GlowLevel6;
        weights[6] = GlowLevel7;
        if (!GlowNormalized)
            return;
        var sum = 0f;
        for (var i = 0; i < GlowLevelCount; i++)
            sum += weights[i];
        if (sum > 0f)
            for (var i = 0; i < GlowLevelCount; i++)
                weights[i] /= sum;
    }

    /// <summary>The highest level with a weight above 0.01 (Godot's <c>max_glow_index</c>), or -1 when glow draws nothing.</summary>
    public int GlowMaxLevel
    {
        get
        {
            if (!GlowEnabled)
                return -1;
            Span<float> w = stackalloc float[GlowLevelCount];
            GetGlowWeights(w);
            for (var i = GlowLevelCount - 1; i >= 0; i--)
                if (w[i] > 0.01f)
                    return i;
            return -1;
        }
    }

    /// <summary>Godot's ACES <c>white_tonemapped</c>: the curve at <c>1.8 · max(1, white)</c>, which output is divided by.</summary>
    public float GodotAcesWhiteTonemapped
    {
        get
        {
            var white = MathF.Max(1f, TonemapWhite) * 1.8f;
            return (white * (white + 0.0245786f) - 0.000090537f) / (white * (0.983729f * white + 0.432951f) + 0.238081f);
        }
    }

    /// <summary>
    /// The white point the glow blend uses (Godot's <c>environment_get_white</c>: at least 1 for ACES, Reinhard and filmic; 1
    /// for the engine curve, linear and AgX), which is also the white Reinhard and filmic divide by.
    /// </summary>
    public float GlowWhite => Tonemapper is Tonemapper.GodotAces or Tonemapper.Reinhard or Tonemapper.Filmic ? MathF.Max(1f, TonemapWhite) : 1f;

    /// <summary>Godot 4.4's filmic <c>white_tonemapped</c>: the curve at <see cref="GlowWhite"/>, which output is divided by.</summary>
    public float FilmicWhiteTonemapped
    {
        get
        {
            const float bias = 2f, a = 0.22f * bias * bias, b = 0.30f * bias, c = 0.10f, d = 0.20f, e = 0.01f, f = 0.30f;
            var w = GlowWhite;
            return (w * (a * w + c * b) + d * e) / (w * (a * w + b) + d * f) - e / f;
        }
    }

    /// <summary>The exposure the tonemap uses: the project's (<c>rendering.exposure</c>) for the engine curve, else <see cref="TonemapExposure"/>.</summary>
    public float ExposureFor(float projectExposure) => Tonemapper == Tonemapper.Engine ? projectExposure : TonemapExposure;
}
