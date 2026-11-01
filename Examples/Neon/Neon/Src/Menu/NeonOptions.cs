using MainframeEngine;

namespace Neon;

/// <summary>
/// Every setting of the pause menu, in display order: Display (the window and the image), Graphics (the quality preset,
/// shadows, the city, the weather), Camera (the tonemap, glow and lens), Audio and Controls. Each applies live.
/// </summary>
public static class NeonOptions
{
    public const string Display = "display";
    public const string Graphics = "graphics";
    public const string Camera = "camera";
    public const string Audio = "audio";
    public const string Controls = "controls";

    private const string Window = "Window";
    private const string Image = "Image";
    private const string Quality = "Quality";
    private const string Shadows = "Shadows and light";
    private const string City = "City";
    private const string Weather = "Weather";
    private const string Tone = "Tone";
    private const string Lens = "Lens";
    private const string Volume = "Volume";
    private const string Look = "Look";

    /// <summary>The window modes of the <c>window_mode</c> choices.</summary>
    public static readonly WindowMode[] WindowModes = [WindowMode.Windowed, WindowMode.Maximized, WindowMode.Fullscreen];

    /// <summary>The limits of the <c>max_fps</c> stepper (0: none).</summary>
    public static readonly int[] FrameLimits = [0, 30, 60, 90, 120, 144, 165, 240];

    /// <summary>The levels of the <c>shadows</c> choices.</summary>
    public static readonly ShadowQuality[] ShadowLevels = [ShadowQuality.Off, ShadowQuality.Low, ShadowQuality.Medium, ShadowQuality.High];

    /// <summary>The tonemappers of the <c>tonemapper</c> choices.</summary>
    public static readonly Tonemapper[] Tonemappers = [Tonemapper.Agx, Tonemapper.GodotAces, Tonemapper.Filmic, Tonemapper.Reinhard];

    /// <summary>Anti-aliasing of the <c>aa</c> choices.</summary>
    public static readonly AntiAliasing[] AntiAliasingModes = [AntiAliasing.Taa, AntiAliasing.Fxaa, AntiAliasing.None];

    /// <summary>The film grain at 100 % (display values). Off in Neon's look; it is here for those who want it.</summary>
    public const float MaxGrain = 0.1f;

    /// <summary>The chromatic aberration at 100 % (pixels at the corners).</summary>
    public const float MaxAberration = 4f;

    /// <summary>The quality presets, lowest first, and what each sets (option keys → values).</summary>
    public static readonly (string Name, (string Key, float Value)[] Values)[] Presets =
    [
        ("Low", [("upscaling", 1f), ("render_scale", 0.6f), ("aa", 1f), ("ssao", 0f), ("room_shadows", 0f), ("city_detail", 0f), ("reflections", 0.4f), ("crowd", 0f), ("puddles", 0f), ("volumetric_fog", 0f)]),
        ("Medium", [("upscaling", 1f), ("render_scale", 0.8f), ("aa", 0f), ("ssao", 1f), ("room_shadows", 0f), ("city_detail", 1f), ("reflections", 0.7f), ("crowd", 1f), ("puddles", 0f), ("volumetric_fog", 0f)]),
        ("High", [("upscaling", 2f), ("render_scale", 0.75f), ("aa", 0f), ("ssao", 1f), ("room_shadows", 1f), ("city_detail", 1f), ("reflections", 1f), ("crowd", 1f), ("puddles", 1f), ("volumetric_fog", 0f)]),
        ("Ultra", [("upscaling", 2f), ("render_scale", 0.75f), ("aa", 0f), ("ssao", 1f), ("room_shadows", 1f), ("city_detail", 1f), ("reflections", 1f), ("crowd", 1f), ("puddles", 1f), ("volumetric_fog", 1f)]),
    ];

    public static readonly NeonOption[] All =
    [
        // ── Display: the window ──────────────────────────────────────────────────────────────────────────────
        new()
        {
            Key = "window_mode", Label = "Window mode", Page = Display, Group = Window, Kind = NeonOptionKind.Choice, Store = NeonOptionStore.User,
            Choices = ["Windowed", "Maximized", "Fullscreen"],
            Get = static w => w.User is { } u ? Math.Max(Array.IndexOf(WindowModes, u.WindowMode), 0) : 0,
            Set = static (w, v) => { if (w.User is { } u) u.WindowMode = WindowModes[(int)v]; },
        },
        new()
        {
            Key = "resolution", Label = "Window size", Page = Display, Group = Window, Kind = NeonOptionKind.Stepper, Store = NeonOptionStore.User,
            LiveChoices = static w => w.WindowSizeNames, DimWhen = "window_mode != 0",
            Hint = "The size of the window; fullscreen always uses the whole display.",
            Get = static w => w.WindowSizeIndex,
            Set = static (w, v) => w.WindowSizeIndex = (int)v,
        },
        new()
        {
            Key = "vsync", Label = "VSync", Page = Display, Group = Window, Kind = NeonOptionKind.Toggle, Store = NeonOptionStore.User,
            Get = static w => w.User is { VSync: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.User is { } u) u.VSync = v > 0f; },
        },
        new()
        {
            Key = "max_fps", Label = "Frame rate limit", Page = Display, Group = Window, Kind = NeonOptionKind.Stepper, Store = NeonOptionStore.User,
            Choices = ["Off", "30", "60", "90", "120", "144", "165", "240"], DimWhen = "vsync",
            Get = static w => w.User is { } u ? Math.Max(Array.IndexOf(FrameLimits, u.MaxFps), 0) : 0,
            Set = static (w, v) => { if (w.User is { } u) u.MaxFps = FrameLimits[(int)v]; },
        },
        new()
        {
            Key = "ui_scale", Label = "Interface scale", Page = Display, Group = Window, Kind = NeonOptionKind.Slider, Store = NeonOptionStore.User,
            Min = 0.75f, Max = 1.5f, Step = 0.05f, Format = NeonOption.Percent,
            Get = static w => w.User?.UiScale ?? 1f,
            Set = static (w, v) => { if (w.User is { } u) u.UiScale = v; },
        },

        // ── Display: the image ───────────────────────────────────────────────────────────────────────────────
        new()
        {
            Key = "upscaling", Label = "Upscaling", Page = Display, Group = Image, Kind = NeonOptionKind.Choice,
            Choices = ["TAAU", "FSR 1", "Native"],
            Hint = "Render the 3D scene at a lower resolution and upscale it: TAAU (temporal), FSR 1 (spatial) or native resolution.",
            Get = static w => w.Upscaler,
            Set = static (w, v) => { w.Upscaler = (int)v; w.ApplyScaling(); },
        },
        new()
        {
            Key = "render_scale", Label = "Render scale", Page = Display, Group = Image, Kind = NeonOptionKind.Slider,
            Min = 0.5f, Max = 1f, Step = 0.05f, Format = NeonOption.Percent, DimWhen = "upscaling == 2",
            Hint = "The 3D resolution before upscaling.",
            Get = static w => w.RenderScale,
            Set = static (w, v) => { w.RenderScale = v; w.ApplyScaling(); },
        },
        new()
        {
            Key = "aa", Label = "Anti-aliasing", Page = Display, Group = Image, Kind = NeonOptionKind.Choice,
            Choices = ["TAA", "FXAA", "Off"],
            Hint = "TAA keeps the distant windows and the rain steady in motion; FXAA only smooths geometric edges.",
            Get = static w => w.Display is { } d ? Math.Max(Array.IndexOf(AntiAliasingModes, d.AntiAliasing), 0) : 0,
            Set = static (w, v) => { if (w.Display is { } d) d.AntiAliasing = AntiAliasingModes[(int)v]; },
        },
        new()
        {
            Key = "sharpen", Label = "Sharpening", Page = Display, Group = Image, Kind = NeonOptionKind.Slider,
            Min = 0f, Max = 1f, Step = 0.05f, Format = NeonOption.Percent, DimWhen = "aa != 0",
            Hint = "The sharpen after TAA.",
            Get = static w => w.Display?.TaaSharpness ?? 0.25f,
            Set = static (w, v) => { if (w.Display is { } d) d.TaaSharpness = v; },
        },
        new()
        {
            Key = "fov", Label = "Field of view", Page = Display, Group = Image, Kind = NeonOptionKind.Slider,
            Min = 60f, Max = 110f, Step = 1f, Format = NeonOption.Degrees,
            Hint = "Horizontal field of view.",
            Get = static w => w.Player?.HorizontalFov ?? 90f,
            Set = static (w, v) => { if (w.Player is { } p) p.HorizontalFov = v; },
        },
        new()
        {
            Key = "fps", Label = "Frame rate readout", Page = Display, Group = Image, Kind = NeonOptionKind.Toggle,
            Hint = "FPS, the average and the slowest frame, top right (F3).",
            Get = static w => w.Fps is { Visible: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.Fps is { } fps) fps.Visible = v > 0f; },
        },

        // ── Graphics: the preset ─────────────────────────────────────────────────────────────────────────────
        new()
        {
            Key = "preset", Label = "Preset", Page = Graphics, Group = Quality, Kind = NeonOptionKind.Choice, Store = NeonOptionStore.Derived,
            Choices = ["Low", "Medium", "High", "Ultra", "Custom"],
            Hint = "Sets the costly options below together. Change any of them and this reads Custom.",
            Get = static w => w.CurrentPreset(),
            Set = static (w, v) => w.ApplyPreset((int)v),
        },

        // ── Graphics: shadows and light ──────────────────────────────────────────────────────────────────────
        new()
        {
            Key = "shadows", Label = "Shadow quality", Page = Graphics, Group = Shadows, Kind = NeonOptionKind.Choice, Store = NeonOptionStore.User,
            Choices = ["Off", "Low", "Medium", "High"],
            Hint = "The shadow atlas and cascades. Applies the next time the game starts.",
            Get = static w => w.User is { } u ? Math.Max(Array.IndexOf(ShadowLevels, u.Shadows), 0) : 3,
            Set = static (w, v) => { if (w.User is { } u) u.Shadows = ShadowLevels[(int)v]; },
        },
        new()
        {
            Key = "room_shadows", Label = "Room light shadows", Page = Graphics, Group = Shadows, Kind = NeonOptionKind.Toggle,
            Hint = "Cube shadows from the four main room lights: the costliest thing indoors.",
            Get = static w => w.RoomShadows ? 1f : 0f,
            Set = static (w, v) => w.RoomShadows = v > 0f,
        },
        new()
        {
            Key = "moon_distance", Label = "Moon shadow distance", Page = Graphics, Group = Shadows, Kind = NeonOptionKind.Slider,
            Min = 20f, Max = 120f, Step = 5f, Format = NeonOption.Metres,
            Get = static w => w.Moon?.ShadowMaxDistance ?? 40f,
            Set = static (w, v) => { if (w.Moon is { } m) m.ShadowMaxDistance = v; },
        },
        new()
        {
            Key = "ssao", Label = "Ambient occlusion", Page = Graphics, Group = Shadows, Kind = NeonOptionKind.Toggle,
            Hint = "GTAO: contact darkening in corners and under furniture.",
            Get = static w => w.Profile is { SsaoEnabled: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.Profile is { } p) p.SsaoEnabled = v > 0f; },
        },
        new()
        {
            Key = "ssao_intensity", Label = "Occlusion strength", Page = Graphics, Group = Shadows, Kind = NeonOptionKind.Slider,
            Min = 0f, Max = 3f, Step = 0.1f, DimWhen = "!ssao",
            Get = static w => w.Profile?.SsaoIntensity ?? 1f,
            Set = static (w, v) => { if (w.Profile is { } p) p.SsaoIntensity = v; },
        },
        new()
        {
            Key = "ssao_radius", Label = "Occlusion radius", Page = Graphics, Group = Shadows, Kind = NeonOptionKind.Slider,
            Min = 0.2f, Max = 2f, Step = 0.05f, DimWhen = "!ssao", Format = static v => NeonOption.Number(v) + " m",
            Get = static w => w.Profile?.SsaoRadius ?? 0.7f,
            Set = static (w, v) => { if (w.Profile is { } p) p.SsaoRadius = v; },
        },
        new()
        {
            Key = "bounce", Label = "Indoor bounce light", Page = Graphics, Group = Shadows, Kind = NeonOptionKind.Slider,
            Min = 0f, Max = 3f, Step = 0.1f,
            Hint = "The baked light probes' strength.",
            Get = static w => w.Probes?.Energy ?? 1.5f,
            Set = static (w, v) => { if (w.Probes is { } p) p.Energy = v; },
        },

        // ── Graphics: the city ───────────────────────────────────────────────────────────────────────────────
        new()
        {
            Key = "city_detail", Label = "Window interiors", Page = Graphics, Group = City, Kind = NeonOptionKind.Toggle,
            Hint = "Rooms in parallax behind the windows, deep reveals and weathered walls.",
            Get = static w => w.City is { Detail: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.City is { } c) c.Detail = v > 0f; },
        },
        new()
        {
            Key = "city_lights", Label = "Lit windows", Page = Graphics, Group = City, Kind = NeonOptionKind.Slider,
            Min = 0.2f, Max = 1.6f, Step = 0.05f, Format = NeonOption.Percent,
            Hint = "How many of the city's windows are lit.",
            Get = static w => w.City?.LitWindows ?? 1f,
            Set = static (w, v) => { if (w.City is { } c) c.LitWindows = v; },
        },
        new()
        {
            Key = "neon", Label = "Neon brightness", Page = Graphics, Group = City, Kind = NeonOptionKind.Slider,
            Min = 0.2f, Max = 2f, Step = 0.05f, Format = NeonOption.Percent,
            Hint = "The towers' trims and bands.",
            Get = static w => w.City?.NeonBrightness ?? 1f,
            Set = static (w, v) => { if (w.City is { } c) c.NeonBrightness = v; },
        },
        new()
        {
            Key = "reflections", Label = "Street reflections", Page = Graphics, Group = City, Kind = NeonOptionKind.Slider,
            Min = 0f, Max = 1.5f, Step = 0.05f, Format = NeonOption.Percent,
            Hint = "How strongly the wet streets mirror the city.",
            Get = static w => w.City?.StreetReflections ?? 1f,
            Set = static (w, v) => { if (w.City is { } c) c.StreetReflections = v; },
        },
        new()
        {
            Key = "puddles", Label = "Puddle reflections", Page = Graphics, Group = City, Kind = NeonOptionKind.Toggle,
            Hint = "Screen-space reflections in the puddles on the balcony.",
            Get = static w => w.City is { PuddleReflections: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.City is { } c) c.PuddleReflections = v > 0f; },
        },
        new()
        {
            Key = "holograms", Label = "Holograms", Page = Graphics, Group = City, Kind = NeonOptionKind.Toggle,
            Hint = "The koi, the dancer and the logo.",
            Get = static w => w.City is { Holograms: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.City is { } c) c.Holograms = v > 0f; },
        },
        new()
        {
            Key = "crowd", Label = "Crowd", Page = Graphics, Group = City, Kind = NeonOptionKind.Toggle,
            Hint = "The people on the pavements.",
            Get = static w => w.City is { Crowd: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.City is { } c) c.Crowd = v > 0f; },
        },
        new()
        {
            Key = "traffic", Label = "Traffic", Page = Graphics, Group = City, Kind = NeonOptionKind.Toggle,
            Hint = "Cars, hover cars, the train and the airships.",
            Get = static w => w.City?.TrafficNode is { Visible: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.City?.TrafficNode is { } t) t.Visible = v > 0f; },
        },

        // ── Graphics: the weather ────────────────────────────────────────────────────────────────────────────
        new()
        {
            Key = "rain", Label = "Rain", Page = Graphics, Group = Weather, Kind = NeonOptionKind.Slider,
            Min = 0f, Max = 1f, Step = 0.05f, Format = NeonOption.Percent,
            Hint = "From a dry night to a downpour.",
            Get = static w => w.City?.RainNode?.Density ?? NeonRain.DefaultDensity,
            Set = static (w, v) => { if (w.City?.RainNode is { } r) r.Density = v; },
        },
        new()
        {
            Key = "haze", Label = "Haze", Page = Graphics, Group = Weather, Kind = NeonOptionKind.Slider,
            Min = 0f, Max = 3f, Step = 0.05f, Format = NeonOption.Percent,
            Hint = "The air between the towers, relative to Neon's look.",
            Get = static w => w.Environment is { } e && w.BaseFogDensity > 0f ? e.FogDensity / w.BaseFogDensity : 1f,
            Set = static (w, v) => { if (w.Environment is { } e) e.FogDensity = w.BaseFogDensity * v; },
        },
        new()
        {
            Key = "street_haze", Label = "Street haze", Page = Graphics, Group = Weather, Kind = NeonOptionKind.Slider,
            Min = 0f, Max = 3f, Step = 0.05f, Format = NeonOption.Percent,
            Hint = "The murk that gathers low over the streets.",
            Get = static w => w.Environment is { } e && w.BaseFogHeightDensity > 0f ? e.FogHeightDensity / w.BaseFogHeightDensity : 1f,
            Set = static (w, v) => { if (w.Environment is { } e) e.FogHeightDensity = w.BaseFogHeightDensity * v; },
        },
        new()
        {
            Key = "volumetric_fog", Label = "Volumetric fog", Page = Graphics, Group = Weather, Kind = NeonOptionKind.Toggle,
            Hint = "Lit, shadowed air: shafts from the room lights.",
            Get = static w => w.Environment is { VolumetricFogEnabled: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.Environment is { } e) e.VolumetricFogEnabled = v > 0f; },
        },
        new()
        {
            Key = "volumetric_density", Label = "Fog density", Page = Graphics, Group = Weather, Kind = NeonOptionKind.Slider,
            Min = 0f, Max = 0.02f, Step = 0.001f, DimWhen = "!volumetric_fog", Format = static v => (v * 1000f).ToString("0", System.Globalization.CultureInfo.InvariantCulture),
            Get = static w => w.Environment?.VolumetricFogDensity ?? 0f,
            Set = static (w, v) => { if (w.Environment is { } e) e.VolumetricFogDensity = v; },
        },

        // ── Camera: tone ─────────────────────────────────────────────────────────────────────────────────────
        new()
        {
            Key = "tonemapper", Label = "Tonemapper", Page = Camera, Group = Tone, Kind = NeonOptionKind.Choice,
            Choices = ["AgX", "ACES", "Filmic", "Reinhard"],
            Get = static w => w.Profile is { } p ? Math.Max(Array.IndexOf(Tonemappers, p.Tonemapper), 0) : 0,
            Set = static (w, v) => { if (w.Profile is { } p) p.Tonemapper = Tonemappers[(int)v]; },
        },
        new()
        {
            Key = "exposure", Label = "Exposure", Page = Camera, Group = Tone, Kind = NeonOptionKind.Slider,
            Min = -2f, Max = 2f, Step = 0.1f, Format = NeonOption.Ev,
            Hint = "Exposure compensation in stops.",
            Get = static w => w.Profile is { TonemapExposure: > 0f } p ? MathF.Log2(p.TonemapExposure / w.BaseTonemapExposure) : 0f,
            Set = static (w, v) => { if (w.Profile is { } p) p.TonemapExposure = w.BaseTonemapExposure * MathF.Pow(2f, v); },
        },
        new()
        {
            Key = "auto_exposure", Label = "Auto exposure", Page = Camera, Group = Tone, Kind = NeonOptionKind.Toggle,
            Hint = "Eye adaptation between the bright rooms and the dark balcony.",
            Get = static w => w.Profile is { AutoExposureEnabled: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.Profile is { } p) p.AutoExposureEnabled = v > 0f; },
        },
        new()
        {
            Key = "glow", Label = "Glow", Page = Camera, Group = Tone, Kind = NeonOptionKind.Slider,
            Min = 0f, Max = 3f, Step = 0.05f, Format = NeonOption.Percent,
            Hint = "The bloom round the neon.",
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
            Key = "glow_threshold", Label = "Glow threshold", Page = Camera, Group = Tone, Kind = NeonOptionKind.Slider,
            Min = 0.4f, Max = 4f, Step = 0.1f, DimWhen = "glow == 0",
            Hint = "How bright something must be to bloom. Lower and whole facades glow.",
            Get = static w => w.Profile?.GlowHdrThreshold ?? 1.6f,
            Set = static (w, v) => { if (w.Profile is { } p) p.GlowHdrThreshold = v; },
        },
        new()
        {
            Key = "grade", Label = "Colour grade", Page = Camera, Group = Tone, Kind = NeonOptionKind.Toggle,
            Get = static w => w.Profile is { AdjustmentEnabled: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.Profile is { } p) p.AdjustmentEnabled = v > 0f; },
        },
        new()
        {
            Key = "contrast", Label = "Contrast", Page = Camera, Group = Tone, Kind = NeonOptionKind.Slider,
            Min = 0.7f, Max = 1.5f, Step = 0.01f, DimWhen = "!grade",
            Get = static w => w.Profile?.AdjustmentContrast ?? 1f,
            Set = static (w, v) => { if (w.Profile is { } p) p.AdjustmentContrast = v; },
        },
        new()
        {
            Key = "saturation", Label = "Saturation", Page = Camera, Group = Tone, Kind = NeonOptionKind.Slider,
            Min = 0f, Max = 2f, Step = 0.01f, DimWhen = "!grade",
            Get = static w => w.Profile?.AdjustmentSaturation ?? 1f,
            Set = static (w, v) => { if (w.Profile is { } p) p.AdjustmentSaturation = v; },
        },

        // ── Camera: lens ─────────────────────────────────────────────────────────────────────────────────────
        new()
        {
            Key = "dof", Label = "Depth of field", Page = Camera, Group = Lens, Kind = NeonOptionKind.Toggle,
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
            Key = "dof_distance", Label = "Focus distance", Page = Camera, Group = Lens, Kind = NeonOptionKind.Slider,
            Min = 2f, Max = 200f, Step = 2f, Format = NeonOption.Metres, DimWhen = "!dof",
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
            Key = "vignette", Label = "Vignette", Page = Camera, Group = Lens, Kind = NeonOptionKind.Slider,
            Min = 0f, Max = 1f, Step = 0.05f, Format = NeonOption.Percent,
            Get = static w => w.Lens?.VignetteIntensity ?? 0f,
            Set = static (w, v) => { if (w.Lens is { } lens) lens.VignetteIntensity = v; },
        },
        new()
        {
            Key = "aberration", Label = "Chromatic aberration", Page = Camera, Group = Lens, Kind = NeonOptionKind.Slider,
            Min = 0f, Max = 1f, Step = 0.01f, Format = NeonOption.Percent,
            Get = static w => (w.Lens?.ChromaticAberrationIntensity ?? 0f) / MaxAberration,
            Set = static (w, v) => { if (w.Lens is { } lens) lens.ChromaticAberrationIntensity = v * MaxAberration; },
        },
        new()
        {
            Key = "grain", Label = "Film grain", Page = Camera, Group = Lens, Kind = NeonOptionKind.Slider,
            Min = 0f, Max = 1f, Step = 0.05f, Format = NeonOption.Percent,
            Hint = "Off in Neon's look.",
            Get = static w => (w.Lens?.FilmGrainIntensity ?? 0f) / MaxGrain,
            Set = static (w, v) => { if (w.Lens is { } lens) lens.FilmGrainIntensity = v * MaxGrain; },
        },
        new()
        {
            Key = "light_shafts", Label = "Light shafts", Page = Camera, Group = Lens, Kind = NeonOptionKind.Toggle,
            Hint = "Radial shafts from the moon on screen.",
            Get = static w => w.Profile is { LightShaftsEnabled: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.Profile is { } p) p.LightShaftsEnabled = v > 0f; },
        },

        // ── Audio ────────────────────────────────────────────────────────────────────────────────────────────
        Bus("master", "Master", "Master", "Everything."),
        Bus("music", "Music", "Music", "The stereo."),
        Bus("city", "City", "Ambience", "The street far below, sirens, the traffic."),
        Bus("rain_volume", "Rain", "Rain", "The rain on the glass and the balcony."),
        Bus("effects", "Effects", "Foley", "Footsteps, doors, switches, the shower."),
        Bus("tv", "TV", "Tv", "The television."),
        new()
        {
            Key = "mute", Label = "Mute", Page = Audio, Group = Volume, Kind = NeonOptionKind.Toggle, Store = NeonOptionStore.User,
            Get = static w => w.User is { } u && u.IsBusMuted("Master") ? 1f : 0f,
            Set = static (w, v) => w.User?.SetBusMuted("Master", v > 0f),
        },

        // ── Controls ─────────────────────────────────────────────────────────────────────────────────────────
        new()
        {
            Key = "sensitivity", Label = "Mouse sensitivity", Page = Controls, Group = Look, Kind = NeonOptionKind.Slider,
            Min = 0.02f, Max = 0.5f, Step = 0.01f,
            Hint = "Degrees per mouse count.",
            Get = static w => w.Player?.MouseSensitivity ?? 0.1f,
            Set = static (w, v) => { if (w.Player is { } p) p.MouseSensitivity = v; },
        },
        new()
        {
            Key = "invert_y", Label = "Invert look", Page = Controls, Group = Look, Kind = NeonOptionKind.Toggle,
            Get = static w => w.Player is { InvertY: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.Player is { } p) p.InvertY = v > 0f; },
        },
        new()
        {
            Key = "pad_look", Label = "Gamepad look speed", Page = Controls, Group = Look, Kind = NeonOptionKind.Slider,
            Min = 40f, Max = 400f, Step = 10f, Format = static v => NeonOption.Whole(v) + "°/s",
            Get = static w => w.Player?.GamepadLookSpeed ?? 140f,
            Set = static (w, v) => { if (w.Player is { } p) p.GamepadLookSpeed = v; },
        },
        new()
        {
            Key = "head_bob", Label = "Head bob", Page = Controls, Group = Look, Kind = NeonOptionKind.Toggle,
            Get = static w => w.Player is { HeadBob: true } ? 1f : 0f,
            Set = static (w, v) => { if (w.Player is { } p) p.HeadBob = v > 0f; },
        },
    ];

    private static readonly Dictionary<string, NeonOption> ByKey = All.ToDictionary(o => o.Key, StringComparer.Ordinal);

    /// <summary>The option named <paramref name="key"/>, or null.</summary>
    public static NeonOption? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>The options of a page, in order.</summary>
    public static IEnumerable<NeonOption> OnPage(string page) => All.Where(o => o.Page == page);

    private static NeonOption Bus(string key, string label, string bus, string hint) => new()
    {
        Key = key,
        Label = label,
        Page = Audio,
        Group = Volume,
        Kind = NeonOptionKind.Slider,
        Store = NeonOptionStore.User,
        Min = 0f,
        Max = 1f,
        Step = 0.05f,
        Format = NeonOption.Percent,
        Hint = hint,
        DimWhen = "mute",
        Get = w => w.User?.GetBusVolume(bus) ?? 1f,
        Set = (w, v) => w.User?.SetBusVolume(bus, v),
    };
}
