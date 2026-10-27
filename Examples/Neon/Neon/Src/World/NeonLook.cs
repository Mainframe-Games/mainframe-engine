using MainframeEngine;

namespace Neon;

/// <summary>
/// Neon's post-processing look as resource files, as the Forest's <c>ForestLook</c>: <see cref="ProfilePath"/> (a
/// <see cref="PostProcessProfile"/>: AgX, a strong, wide glow from a low threshold so every window, sign and kerb blooms in
/// the wet air, GTAO for the apartment) and <see cref="LensPath"/> (a <see cref="CameraAttributesPractical"/>: a mild
/// vignette, grain and a touch of chromatic aberration). The files are the source of truth: <c>--write-scenes</c> writes
/// them from <see cref="CreateProfile"/> and <see cref="CreateLens"/> only when they are missing, so values tuned in the
/// editor survive a scene rebuild.
/// </summary>
public static class NeonLook
{
    public const string ProfilePath = "Assets/PostProcess/neon.mres";

    public const string LensPath = "Assets/PostProcess/neon-lens.mres";

    public static PostProcessProfile CreateProfile() => new()
    {
        Tonemapper = Tonemapper.Agx,
        TonemapExposure = 1.1f,

        // Neon: the glow is the light. A low threshold (the windows and signs are 1.5–6 in HDR), wide levels for the haze
        // round the signs, additive so the halos tint the dark air instead of washing it out.
        GlowEnabled = true,
        GlowIntensity = 0.85f,
        GlowStrength = 1.1f,
        GlowBloom = 0.02f,
        GlowBlendMode = GlowBlendMode.Additive,
        GlowHdrThreshold = 0.7f,
        GlowHdrScale = 2f,
        GlowHdrLuminanceCap = 10f,
        GlowLevel1 = 0.2f,
        GlowLevel2 = 0.5f,
        GlowLevel3 = 0.8f,
        GlowLevel4 = 0.7f,
        GlowLevel5 = 0.5f,
        GlowLevel6 = 0.3f,

        SsaoEnabled = true,
        SsaoRadius = 0.7f,
        SsaoIntensity = 1.2f,
        SsaoPower = 1.3f,

        AdjustmentEnabled = true,
        AdjustmentContrast = 1.06f,
        AdjustmentSaturation = 1.15f,
    };

    public static CameraAttributesPractical CreateLens() => new()
    {
        VignetteIntensity = 0.3f,
        FilmGrainIntensity = 0.025f,
        ChromaticAberrationIntensity = 0.12f,
    };

    /// <summary>The profile file, or (before <c>--write-scenes</c> wrote it) a new inline copy of <see cref="CreateProfile"/>.</summary>
    public static PostProcessProfile LoadProfile() =>
        ResourceLoader.Exists(ProfilePath) ? ResourceLoader.Load<PostProcessProfile>(ProfilePath) : CreateProfile();

    /// <summary>The lens file, or a new inline <see cref="CreateLens"/>.</summary>
    public static CameraAttributesPractical LoadLens() =>
        ResourceLoader.Exists(LensPath) ? ResourceLoader.Load<CameraAttributesPractical>(LensPath) : CreateLens();

    /// <summary>Writes the files that are missing (the database must be rooted at the project); returns the paths written.</summary>
    public static IReadOnlyList<string> WriteMissing()
    {
        var written = new List<string>();
        if (!File.Exists(AssetDatabase.Current.ToAbsolutePath(ProfilePath)))
        {
            ResourceSaver.Save(CreateProfile(), ProfilePath);
            written.Add(ProfilePath);
        }

        if (!File.Exists(AssetDatabase.Current.ToAbsolutePath(LensPath)))
        {
            ResourceSaver.Save(CreateLens(), LensPath);
            written.Add(LensPath);
        }

        return written;
    }
}
