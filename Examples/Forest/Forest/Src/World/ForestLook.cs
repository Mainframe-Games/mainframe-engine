using MainframeEngine;

namespace Forest;

/// <summary>
/// The Forest's post-processing look as resource files (ADR 0169), referenced by the scene's environment and tuned in the
/// editor: <see cref="ProfilePath"/> (a <see cref="PostProcessProfile"/>: tonemap, eye adaptation, glow, light shafts,
/// SSAO and the colour grade with <see cref="ForestGrade.LutPath"/>) and <see cref="LensPath"/> (a
/// <see cref="CameraAttributesPractical"/>: vignette and grain). The files are the source of truth: <c>--write-scenes</c>
/// writes them from <see cref="CreateProfile"/> and <see cref="ForestGrade.CreateLens"/> only when they are missing, so
/// values tweaked in the editor survive a scene rebuild.
/// </summary>
public static class ForestLook
{
    /// <summary>The post-processing profile, relative to the project folder.</summary>
    public const string ProfilePath = "Content/PostProcess/forest.mres";

    /// <summary>The world's lens, relative to the project folder.</summary>
    public const string LensPath = "Content/PostProcess/forest-lens.mres";

    /// <summary>
    /// The morning look's first version: eye adaptation, a little glow (only the sun, its glints and the brightest sky gaps),
    /// strong shafts through the canopy, GTAO (ADR 0165) close to the physical value with a short radius, and the
    /// forest-morning grade (ADR 0168).
    /// </summary>
    public static PostProcessProfile CreateProfile() => new()
    {
        GlowEnabled = true,
        GlowIntensity = 0.3f,
        GlowStrength = 1f,
        GlowBloom = 0f,
        GlowHdrThreshold = 4f, // only the sun, its glints and the brightest sky gaps bloom; sunlit leaves do not sparkle
        GlowHdrLuminanceCap = 3f,

        AutoExposureEnabled = true,
        AutoExposureScale = 0.16f,
        AutoExposureSpeed = 0.6f,
        AutoExposureMinLuminance = 0.03f,
        // ADR 0177: a view that is mostly canopy shade exposes for the shade; highlight protection keeps the sunlit slopes
        // and treetops (the brightest 2 % of the centre-weighted histogram) at or below 0.8 instead of washing them out.
        AutoExposureMode = AutoExposureMode.Histogram,
        AutoExposureHighlightProtection = true,
        AutoExposureHighlightWhite = 0.8f,

        // ADR 0171: the volumetric fog draws the rays from the real shadows; the screen-space shafts on top doubled the
        // streaks around the sun and made the glade milky. Off, with their values kept for a look without volumetrics.
        LightShaftsEnabled = false,
        LightShaftsIntensity = 2.3f,
        LightShaftsDecay = 0.965f,
        LightShaftsDensity = 0.85f,

        // Trunks, rocks, ferns and grass sit in the ground and the shade under the canopy deepens; near the physical value
        // (intensity and power close to 1) and a short radius, so there are no dark halos.
        SsaoEnabled = true,
        SsaoRadius = 0.8f,
        SsaoIntensity = 1.1f,
        SsaoPower = 1.2f,
        SsaoDetail = 0.4f,
        SsaoHorizon = 0.06f,
        SsaoSharpness = 0.98f,

        AdjustmentEnabled = true,
        AdjustmentColorCorrection = ResourceLoader.Exists(ForestGrade.LutPath) ? ResourceLoader.Load<Texture3D>(ForestGrade.LutPath) : null,
        AdjustmentColorCorrectionStrength = ForestGrade.Strength,
    };

    /// <summary>The profile file, or (before <c>--write-scenes</c> wrote it) a new inline copy of <see cref="CreateProfile"/>.</summary>
    public static PostProcessProfile LoadProfile() =>
        ResourceLoader.Exists(ProfilePath) ? ResourceLoader.Load<PostProcessProfile>(ProfilePath) : CreateProfile();

    /// <summary>The lens file, or a new inline <see cref="ForestGrade.CreateLens"/>.</summary>
    public static CameraAttributesPractical LoadLens() =>
        ResourceLoader.Exists(LensPath) ? ResourceLoader.Load<CameraAttributesPractical>(LensPath) : ForestGrade.CreateLens();

    /// <summary>
    /// Writes <see cref="ProfilePath"/> and <see cref="LensPath"/> when missing (the database must be rooted at the project);
    /// returns the paths written.
    /// </summary>
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
            ResourceSaver.Save(ForestGrade.CreateLens(), LensPath);
            written.Add(LensPath);
        }

        return written;
    }
}
