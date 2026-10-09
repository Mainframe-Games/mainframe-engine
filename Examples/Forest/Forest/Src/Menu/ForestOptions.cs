using MainframeEngine;

namespace Forest;

/// <summary>
/// Every setting of the pause menu (ADR 0180), in display order: the Graphics page (display, shadows and light,
/// atmosphere, camera and post, vegetation), Audio and Controls. Each is applied live through the engine's runtime
/// properties: the renderer's anti-aliasing, upscaler, render scale and VSync; the sun's shadow settings; the
/// environment's fog and wind; the <see cref="PostProcessProfile"/> and the lens; the terrain's foliage scales.
/// </summary>
public static class ForestOptions
{
    public const string Graphics = "graphics";
    public const string Audio = "audio";
    public const string Controls = "controls";

    private const string Display = "Display";
    private const string Shadows = "Shadows and light";
    private const string Atmosphere = "Atmosphere";
    private const string Sun = "Sun (lighting bake approximate)";
    private const string Lens = "Camera and post-processing";
    private const string Vegetation = "Vegetation";

    /// <summary>Shadow map sizes of the <c>shadow_res</c> choices.</summary>
    public static readonly int[] ShadowResolutions = [512, 1024, 2048, 4096];

    /// <summary>The tonemappers of the <c>tonemapper</c> choices.</summary>
    public static readonly Tonemapper[] Tonemappers = [Tonemapper.Agx, Tonemapper.GodotAces, Tonemapper.Filmic, Tonemapper.Reinhard];

    /// <summary>Anti-aliasing of the <c>aa</c> choices.</summary>
    public static readonly AntiAliasing[] AntiAliasingModes = [AntiAliasing.Taa, AntiAliasing.Fxaa, AntiAliasing.None];

    /// <summary>The film grain at 100 % (display values).</summary>
    public const float MaxGrain = 0.1f;

    /// <summary>The chromatic aberration at 100 % (pixels at the corners).</summary>
    public const float MaxAberration = 4f;

    public static readonly ForestOption[] All =
    [
        // ── Graphics: display ────────────────────────────────────────────────────────────────────────────────
        new()
        {
            Key = "upscaling", Label = "Upscaling", Page = Graphics, Group = Display, Kind = ForestOptionKind.Choice,
            Choices = ["TAAU", "FSR 1", "Native"],
            Hint = "Render the 3D scene at a lower resolution and upscale it: TAAU (temporal), FSR 1 (spatial) or native resolution.",
            Get = static w => w.Upscaler,
            Set = static (w, v) => { w.Upscaler = (int)v; w.ApplyScaling(); },
        },
        new()
        {
            Key = "render_scale", Label = "Render scale", Page = Graphics, Group = Display, Kind = ForestOptionKind.Slider,
            Min = 0.5f, Max = 1f, Step = 0.05f, Format = ForestOption.Percent, DimWhen = "upscaling == 2",
            Hint = "The 3D resolution before upscaling.",
            Get = static w => w.RenderScale,
            Set = static (w, v) => { w.RenderScale = v; w.ApplyScaling(); },
        },
        new()
        {
            Key = "aa", Label = "Anti-aliasing", Page = Graphics, Group = Display, Kind = ForestOptionKind.Choice,
            Choices = ["TAA", "FXAA", "Off"],
            Hint = "TAA smooths leaves and grass in motion; FXAA only geometric edges.",
            Get = static w => w.Display is { } d ? Math.Max(Array.IndexOf(AntiAliasingModes, d.AntiAliasing), 0) : 0,
            Set = static (w, v) => { if (w.Display is { } d) d.AntiAliasing = AntiAliasingModes[(int)v]; },
        },
        new()
        {
            Key = "vsync", Label = "VSync", Page = Graphics, Group = Display, Kind = ForestOptionKind.Toggle,
            Get = static w => w.Display is { VSync: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.Display is { } d) d.VSync = v > 0f; },
        },
        new()
        {
            Key = "fov", Label = "Field of view", Page = Graphics, Group = Display, Kind = ForestOptionKind.Slider,
            Store = ForestOptionStore.Settings, Min = 60f, Max = 110f, Step = 1f, Format = ForestOption.Degrees,
            Hint = "Horizontal field of view while walking.",
            Get = static w => w.Settings.HorizontalFov,
            Set = static (w, v) => { w.Settings.HorizontalFov = v; w.Player?.ApplySettings(w.Settings); },
        },
        new()
        {
            Key = "fps", Label = "Frame rate readout", Page = Graphics, Group = Display, Kind = ForestOptionKind.Toggle,
            Hint = "FPS, the average and the slowest frame, top right (F3).",
            Get = static w => w.Fps is { Visible: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.Fps is { } fps) fps.Visible = v > 0f; },
        },

        // ── Graphics: shadows and light ──────────────────────────────────────────────────────────────────────
        new()
        {
            Key = "shadow_res", Label = "Shadow resolution", Page = Graphics, Group = Shadows, Kind = ForestOptionKind.Choice,
            Choices = ["512", "1024", "2048", "4096"],
            Hint = "The sun's cascade size in texels. 2048 and 4096 cost several milliseconds under the leaves.",
            Get = static w => w.Sun is { } sun ? Math.Max(Array.IndexOf(ShadowResolutions, sun.ShadowResolution), 0) : 1,
            Set = static (w, v) => { if (w.Sun is { } sun) sun.ShadowResolution = ShadowResolutions[(int)v]; },
        },
        new()
        {
            Key = "shadow_distance", Label = "Shadow distance", Page = Graphics, Group = Shadows, Kind = ForestOptionKind.Slider,
            Min = 40f, Max = 300f, Step = 10f, Format = ForestOption.Metres,
            Hint = "How far the sun's cascades reach (the far shadow covers the rest).",
            Get = static w => w.Sun?.ShadowMaxDistance ?? 140f,
            Set = static (w, v) => { if (w.Sun is { } sun) sun.ShadowMaxDistance = v; },
        },
        new()
        {
            Key = "shadow_cascades", Label = "Shadow cascades", Page = Graphics, Group = Shadows, Kind = ForestOptionKind.Slider,
            Min = 1f, Max = 4f, Step = 1f, Format = ForestOption.Whole,
            Get = static w => w.Sun?.ShadowCascades ?? 4,
            Set = static (w, v) => { if (w.Sun is { } sun) sun.ShadowCascades = (int)v; },
        },
        new()
        {
            Key = "pcss", Label = "Soft shadows (PCSS)", Page = Graphics, Group = Shadows, Kind = ForestOptionKind.Toggle,
            Hint = "Penumbrae that widen with the distance to the caster, from the sun's 0.5° disc.",
            Get = static w => w.Sun is { LightAngularDistance: > 0f } ? 1f : 0f,
            Set = static (w, v) => { if (w.Sun is { } sun) sun.LightAngularDistance = v > 0f ? w.BaseAngularDistance : 0f; },
        },
        new()
        {
            Key = "contact_shadows", Label = "Contact shadows", Page = Graphics, Group = Shadows, Kind = ForestOptionKind.Toggle,
            Hint = "Screen-space shadows for the small things the cascades miss (grass, pebbles).",
            Get = static w => w.Sun is { ContactShadows: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.Sun is { } sun) sun.ContactShadows = v > 0f; },
        },
        new()
        {
            Key = "ssao", Label = "Ambient occlusion", Page = Graphics, Group = Shadows, Kind = ForestOptionKind.Toggle,
            Hint = "GTAO: contact darkening in creases, under rocks and between roots.",
            Get = static w => w.Profile is { SsaoEnabled: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.Profile is { } p) p.SsaoEnabled = v > 0f; },
        },
        new()
        {
            Key = "ssao_intensity", Label = "Occlusion strength", Page = Graphics, Group = Shadows, Kind = ForestOptionKind.Slider,
            Min = 0f, Max = 3f, Step = 0.1f, DimWhen = "!ssao",
            Get = static w => w.Profile?.SsaoIntensity ?? 1f,
            Set = static (w, v) => { if (w.Profile is { } p) p.SsaoIntensity = v; },
        },

        // ── Graphics: atmosphere ─────────────────────────────────────────────────────────────────────────────
        new()
        {
            Key = "haze", Label = "Haze", Page = Graphics, Group = Atmosphere, Kind = ForestOptionKind.Slider,
            Min = 0f, Max = 4f, Step = 0.05f, Format = ForestOption.Percent,
            Hint = "The distance fog's density (aerial perspective), relative to the Forest's look.",
            Get = static w => w.Environment is { } e && w.BaseFogDensity > 0f ? e.FogDensity / w.BaseFogDensity : 1f,
            Set = static (w, v) => { if (w.Environment is { } e) e.FogDensity = w.BaseFogDensity * v; },
        },
        new()
        {
            Key = "volumetric_fog", Label = "Volumetric fog", Page = Graphics, Group = Atmosphere, Kind = ForestOptionKind.Toggle,
            Hint = "Lit, shadowed air: the rays between the trees.",
            Get = static w => w.Environment is { VolumetricFogEnabled: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.Environment is { } e) e.VolumetricFogEnabled = v > 0f; },
        },
        new()
        {
            Key = "volumetric_density", Label = "Fog density", Page = Graphics, Group = Atmosphere, Kind = ForestOptionKind.Slider,
            Min = 0f, Max = 4f, Step = 0.05f, Format = ForestOption.Percent, DimWhen = "!volumetric_fog",
            Get = static w => w.Environment is { } e && w.BaseVolumetricFogDensity > 0f ? e.VolumetricFogDensity / w.BaseVolumetricFogDensity : 1f,
            Set = static (w, v) => { if (w.Environment is { } e) e.VolumetricFogDensity = w.BaseVolumetricFogDensity * v; },
        },
        new()
        {
            Key = "light_shafts", Label = "Screen-space light shafts", Page = Graphics, Group = Atmosphere, Kind = ForestOptionKind.Toggle,
            Hint = "Radial shafts from the sun on screen, on top of the volumetric fog (off in the Forest's look).",
            Get = static w => w.Profile is { LightShaftsEnabled: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.Profile is { } p) p.LightShaftsEnabled = v > 0f; },
        },
        new()
        {
            Key = "wind", Label = "Wind", Page = Graphics, Group = Atmosphere, Kind = ForestOptionKind.Slider,
            Min = 0f, Max = 4f, Step = 0.05f, Format = ForestOption.Percent,
            Hint = "The trees', grass's and wind sound's strength.",
            Get = static w => w.Environment is { } e && w.BaseWindStrength > 0f ? e.WindStrength / w.BaseWindStrength : 1f,
            Set = static (w, v) => { if (w.Environment is { } e) e.WindStrength = (w.BaseWindStrength > 0f ? w.BaseWindStrength : 0.35f) * v; },
        },
        new()
        {
            Key = "sun_elevation", Label = "Sun height", Page = Graphics, Group = Sun, Kind = ForestOptionKind.Slider,
            Min = 2f, Max = 85f, Step = 1f, Format = ForestOption.Degrees,
            Hint = "The light probes stay baked for the morning sun, so the bounce light is approximate elsewhere.",
            Get = static w => w.SunAngles().Elevation,
            Set = static (w, v) => w.SetSunAngles(v, w.SunAngles().Azimuth),
        },
        new()
        {
            Key = "sun_azimuth", Label = "Sun direction", Page = Graphics, Group = Sun, Kind = ForestOptionKind.Slider,
            Min = 0f, Max = 355f, Step = 5f, Format = ForestOption.Degrees,
            Hint = "Clockwise from north. The morning sun stands in the north-north-east, ahead of the walk.",
            Get = static w => w.SunAngles().Azimuth,
            Set = static (w, v) => w.SetSunAngles(w.SunAngles().Elevation, v),
        },

        // ── Graphics: camera and post-processing ─────────────────────────────────────────────────────────────
        new()
        {
            Key = "tonemapper", Label = "Tonemapper", Page = Graphics, Group = Lens, Kind = ForestOptionKind.Choice,
            Choices = ["AgX", "ACES", "Filmic", "Reinhard"],
            Get = static w => w.Profile is { } p ? Math.Max(Array.IndexOf(Tonemappers, p.Tonemapper), 0) : 0,
            Set = static (w, v) => { if (w.Profile is { } p) p.Tonemapper = Tonemappers[(int)v]; },
        },
        new()
        {
            Key = "exposure", Label = "Exposure", Page = Graphics, Group = Lens, Kind = ForestOptionKind.Slider,
            Min = -2f, Max = 2f, Step = 0.1f, Format = ForestOption.Ev,
            Hint = "Exposure compensation in stops, with or without auto exposure.",
            Get = static w => w.Profile is { TonemapExposure: > 0f } p ? MathF.Log2(p.TonemapExposure / w.BaseTonemapExposure) : 0f,
            Set = static (w, v) => { if (w.Profile is { } p) p.TonemapExposure = w.BaseTonemapExposure * MathF.Pow(2f, v); },
        },
        new()
        {
            Key = "auto_exposure", Label = "Auto exposure", Page = Graphics, Group = Lens, Kind = ForestOptionKind.Toggle,
            Hint = "Eye adaptation between the sunlit glade and the shade under the pines.",
            Get = static w => w.Profile is { AutoExposureEnabled: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.Profile is { } p) p.AutoExposureEnabled = v > 0f; },
        },
        new()
        {
            Key = "glow", Label = "Glow", Page = Graphics, Group = Lens, Kind = ForestOptionKind.Slider,
            Min = 0f, Max = 4f, Step = 0.05f, Format = ForestOption.Percent,
            Hint = "Bloom around the sun and its glints.",
            Get = static w => w.Profile is { GlowEnabled: true } p ? p.GlowIntensity / w.BaseGlowIntensity : 0f,
            Set = static (w, v) =>
            {
                if (w.Profile is not { } p)
                    return;
                p.GlowEnabled = v > 0f;
                if (v > 0f)
                    p.GlowIntensity = w.BaseGlowIntensity * v;
            },
        },
        new()
        {
            Key = "grade", Label = "Colour grade", Page = Graphics, Group = Lens, Kind = ForestOptionKind.Toggle,
            Hint = "The Forest's morning LUT, fitted to Unreal forest references.",
            Get = static w => w.Profile is { AdjustmentEnabled: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.Profile is { } p) p.AdjustmentEnabled = v > 0f; },
        },
        new()
        {
            Key = "dof", Label = "Depth of field", Page = Graphics, Group = Lens, Kind = ForestOptionKind.Toggle,
            Hint = "Blurs the distance beyond the focus distance.",
            Get = static w => w.Lens is { DofBlurFarEnabled: true } ? 1f : 0f,
            Set = static (w, v) =>
            {
                if (w.Lens is not { } lens)
                    return;
                lens.DofBlurFarEnabled = v > 0f;
                lens.DofQuality = DepthOfFieldQuality.High;
            },
        },
        new()
        {
            Key = "dof_distance", Label = "Focus distance", Page = Graphics, Group = Lens, Kind = ForestOptionKind.Slider,
            Min = 2f, Max = 80f, Step = 1f, Format = ForestOption.Metres, DimWhen = "!dof",
            Get = static w => w.Lens?.DofBlurFarDistance ?? 10f,
            Set = static (w, v) =>
            {
                if (w.Lens is not { } lens)
                    return;
                lens.DofBlurFarDistance = v;
                lens.DofBlurFarTransition = v * 1.5f;
            },
        },
        new()
        {
            Key = "grain", Label = "Film grain", Page = Graphics, Group = Lens, Kind = ForestOptionKind.Slider,
            Min = 0f, Max = 1f, Step = 0.05f, Format = ForestOption.Percent,
            Get = static w => (w.Lens?.FilmGrainIntensity ?? 0f) / MaxGrain,
            Set = static (w, v) => { if (w.Lens is { } lens) lens.FilmGrainIntensity = v * MaxGrain; },
        },
        new()
        {
            Key = "vignette", Label = "Vignette", Page = Graphics, Group = Lens, Kind = ForestOptionKind.Slider,
            Min = 0f, Max = 1f, Step = 0.05f, Format = ForestOption.Percent,
            Get = static w => w.Lens?.VignetteIntensity ?? 0f,
            Set = static (w, v) => { if (w.Lens is { } lens) lens.VignetteIntensity = v; },
        },
        new()
        {
            Key = "aberration", Label = "Chromatic aberration", Page = Graphics, Group = Lens, Kind = ForestOptionKind.Slider,
            Min = 0f, Max = 1f, Step = 0.05f, Format = ForestOption.Percent,
            Get = static w => (w.Lens?.ChromaticAberrationIntensity ?? 0f) / MaxAberration,
            Set = static (w, v) => { if (w.Lens is { } lens) lens.ChromaticAberrationIntensity = v * MaxAberration; },
        },

        // ── Graphics: vegetation ─────────────────────────────────────────────────────────────────────────────
        new()
        {
            Key = "grass_density", Label = "Ground cover density", Page = Graphics, Group = Vegetation, Kind = ForestOptionKind.Slider,
            Min = 0.1f, Max = 1f, Step = 0.05f, Format = ForestOption.Percent,
            Hint = "Grass, ferns, flowers and clutter: the share drawn (fewer is faster).",
            Get = static w => w.Terrain?.FoliageDensityScale ?? 1f,
            Set = static (w, v) => { if (w.Terrain is { } t) t.FoliageDensityScale = v; },
        },
        new()
        {
            Key = "grass_distance", Label = "Ground cover distance", Page = Graphics, Group = Vegetation, Kind = ForestOptionKind.Slider,
            Min = 0.25f, Max = 1f, Step = 0.05f, Format = ForestOption.Percent,
            Hint = "How far the ground cover is drawn (grass reaches 36 m at 100 %).",
            Get = static w => w.Terrain?.FoliageDistanceScale ?? 1f,
            Set = static (w, v) => { if (w.Terrain is { } t) t.FoliageDistanceScale = v; },
        },

        // ── Audio ────────────────────────────────────────────────────────────────────────────────────────────
        Volume("master", "Master", "Everything.", static a => a.MasterVolume, static (a, v) => a.MasterVolume = v),
        Volume("ambience", "Ambience", "Wind, rustling leaves and birdsong.", static a => a.AmbienceVolume, static (a, v) => a.AmbienceVolume = v),
        Volume("water", "Water", "The brook and the falls.", static a => a.WaterVolume, static (a, v) => a.WaterVolume = v),
        Volume("footsteps", "Footsteps", "Steps on grass, leaves, rock, gravel, the log bridge and in the water.", static a => a.FoleyVolume, static (a, v) => a.FoleyVolume = v),
        new()
        {
            Key = "mute", Label = "Mute", Page = Audio, Group = "Volume", Kind = ForestOptionKind.Toggle, Store = ForestOptionStore.Settings,
            Get = static w => w.Settings.Audio.Muted ? 1f : 0f,
            Set = static (w, v) => { w.Settings.Audio.Muted = v > 0f; w.Audio?.ApplySettings(w.Settings.Audio); },
        },

        // ── Controls ─────────────────────────────────────────────────────────────────────────────────────────
        new()
        {
            Key = "sensitivity", Label = "Mouse sensitivity", Page = Controls, Group = "Look", Kind = ForestOptionKind.Slider,
            Store = ForestOptionStore.Settings, Min = 0.02f, Max = 0.5f, Step = 0.01f,
            Hint = "Degrees per mouse count.",
            Get = static w => w.Settings.MouseSensitivity,
            Set = static (w, v) => { w.Settings.MouseSensitivity = v; w.Player?.ApplySettings(w.Settings); },
        },
        new()
        {
            Key = "invert_y", Label = "Invert look", Page = Controls, Group = "Look", Kind = ForestOptionKind.Toggle,
            Store = ForestOptionStore.Settings,
            Get = static w => w.Settings.InvertY ? 1f : 0f,
            Set = static (w, v) => { w.Settings.InvertY = v > 0f; w.Player?.ApplySettings(w.Settings); },
        },
        new()
        {
            Key = "pad_look", Label = "Gamepad look speed", Page = Controls, Group = "Look", Kind = ForestOptionKind.Slider,
            Store = ForestOptionStore.Settings, Min = 40f, Max = 400f, Step = 10f, Format = static v => ForestOption.Whole(v) + "°/s",
            Get = static w => w.Settings.GamepadLookSpeed,
            Set = static (w, v) => { w.Settings.GamepadLookSpeed = v; w.Player?.ApplySettings(w.Settings); },
        },
        new()
        {
            Key = "head_bob", Label = "Head bob", Page = Controls, Group = "Look", Kind = ForestOptionKind.Toggle,
            Store = ForestOptionStore.Settings,
            Get = static w => w.Settings.HeadBob ? 1f : 0f,
            Set = static (w, v) => { w.Settings.HeadBob = v > 0f; w.Player?.ApplySettings(w.Settings); },
        },
    ];

    private static readonly Dictionary<string, ForestOption> ByKey = All.ToDictionary(o => o.Key, StringComparer.Ordinal);

    /// <summary>The option named <paramref name="key"/>, or null.</summary>
    public static ForestOption? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>The options of a page, in order.</summary>
    public static IEnumerable<ForestOption> OnPage(string page) => All.Where(o => o.Page == page);

    private static ForestOption Volume(string key, string label, string hint, Func<ForestAudioSettings, float> get, Action<ForestAudioSettings, float> set) => new()
    {
        Key = key,
        Label = label,
        Page = Audio,
        Group = "Volume",
        Kind = ForestOptionKind.Slider,
        Store = ForestOptionStore.Settings,
        Min = 0f,
        Max = 1f,
        Step = 0.05f,
        Format = ForestOption.Percent,
        Hint = hint,
        DimWhen = "mute",
        Get = w => get(w.Settings.Audio),
        Set = (w, v) => { set(w.Settings.Audio, v); w.Audio?.ApplySettings(w.Settings.Audio); },
    };
}
