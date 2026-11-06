using MainframeEngine;

namespace Neon;

/// <summary>
/// Neon's post-processing look as resource files, as the Forest's <c>ForestLook</c>: <see cref="ProfilePath"/> (a
/// <see cref="PostProcessProfile"/>: AgX, a wide glow over the neon only so the facades stay sharp, GTAO for the apartment)
/// and <see cref="LensPath"/> (a <see cref="CameraAttributesPractical"/>: a mild vignette and a touch of chromatic
/// aberration). The files are the source of truth: <c>--write-scenes</c> writes them from <see cref="CreateProfile"/> and
/// <see cref="CreateLens"/> only when they are missing, so values tuned in the editor survive a scene rebuild.
/// </summary>
public static class NeonLook
{
    public const string ProfilePath = "Assets/PostProcess/neon.mfres";

    public const string LensPath = "Assets/PostProcess/neon-lens.mfres";

    public static PostProcessProfile CreateProfile() => new()
    {
        Tonemapper = Tonemapper.Agx,
        TonemapExposure = 1.1f,

        // Neon: the glow is the signs' haze, not a veil over the city. The threshold sits above the windows (0.3–1.5 in
        // HDR) and under the trims and signs (3–6), so a facade stays sharp and only real neon blooms; additive so the
        // halos tint the dark air instead of washing it out.
        GlowEnabled = true,
        GlowIntensity = 0.55f,
        GlowStrength = 1f,
        GlowBloom = 0f,
        GlowBlendMode = GlowBlendMode.Additive,
        GlowHdrThreshold = 1.6f,
        GlowHdrScale = 2f,
        GlowHdrLuminanceCap = 10f,
        GlowLevel1 = 0.3f,
        GlowLevel2 = 0.5f,
        GlowLevel3 = 0.65f,
        GlowLevel4 = 0.55f,
        GlowLevel5 = 0.4f,
        GlowLevel6 = 0.25f,

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
