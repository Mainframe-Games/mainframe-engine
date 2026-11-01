using System.Globalization;
using MainframeEngine;

namespace Neon;

/// <summary>The renderer switches the menu changes (the Vulkan renderer in the game, a plain object in tests).</summary>
public interface INeonDisplay
{
    AntiAliasing AntiAliasing { get; set; }

    Scaling3DMode Scaling3DMode { get; set; }

    float Scaling3DScale { get; set; }

    float TaaSharpness { get; set; }
}

/// <summary>The game's renderer as an <see cref="INeonDisplay"/>.</summary>
public sealed class RendererDisplay(IVulkanContext vulkan) : INeonDisplay
{
    public AntiAliasing AntiAliasing { get => vulkan.AntiAliasing; set => vulkan.AntiAliasing = value; }

    public Scaling3DMode Scaling3DMode { get => vulkan.Scaling3DMode; set => vulkan.Scaling3DMode = value; }

    public float Scaling3DScale { get => vulkan.Scaling3DScale; set => vulkan.Scaling3DScale = value; }

    public float TaaSharpness { get => vulkan.TaaSharpness; set => vulkan.TaaSharpness = value; }

    /// <summary>The tree's renderer, or null without a Vulkan one (headless).</summary>
    public static RendererDisplay? Of(SceneTree? tree) =>
        tree?.Servers.Render is { Vulkan: { } vulkan } ? new RendererDisplay(vulkan) : null;
}

/// <summary>
/// What the pause menu's options act on: the scene's moon, environment, post profile, lens, probes, city, apartment, player
/// and frame-rate readout, the renderer, and the engine's <see cref="UserSettings"/> (window, volumes). Built by
/// <see cref="Capture"/>, which also records the scene's own values: the defaults of the <see cref="NeonOptionStore.Scene"/>
/// options and the baselines the relative ones (haze, glow: "100 %") scale. Scene options the player changed are saved in
/// the settings file under <see cref="Section"/>, only while they differ from the default, so a changed look still reaches
/// players who never touched that option.
/// </summary>
public sealed class NeonWorld
{
    /// <summary>The settings file's section of the menu's own options.</summary>
    public const string Section = "neon";

    private static readonly (int Width, int Height)[] CommonSizes =
    [
        (1280, 720), (1366, 768), (1440, 900), (1600, 900), (1920, 1080), (1920, 1200), (2560, 1080), (2560, 1440), (3440, 1440), (3840, 2160),
    ];

    private bool _roomShadows = true;

    /// <summary>The engine's user settings; null runs without them (headless tools).</summary>
    public UserSettings? User { get; set; }

    public WorldEnvironment? Environment { get; set; }

    public PostProcessProfile? Profile => Environment?.PostProcess;

    public CameraAttributesPractical? Lens => Environment?.CameraAttributes;

    public DirectionalLight3D? Moon { get; set; }

    public LightProbeVolume? Probes { get; set; }

    public NeonCity? City { get; set; }

    public NeonApartment? Apartment { get; set; }

    public FirstPersonController? Player { get; set; }

    public NeonFps? Fps { get; set; }

    public INeonDisplay? Display { get; set; }

    /// <summary>The window sizes offered, as "1920 × 1080" (the displays' bigger ones are left out).</summary>
    public string[] WindowSizeNames { get; set; } = [.. CommonSizes.Select(s => SizeName(s.Width, s.Height))];

    /// <summary>The window size as an index into <see cref="WindowSizeNames"/>.</summary>
    public int WindowSizeIndex
    {
        get
        {
            var size = User?.WindowSize;
            var index = size is { } s ? Array.IndexOf(WindowSizeNames, SizeName(s.X, s.Y)) : -1;
            return index >= 0 ? index : Math.Max(Array.IndexOf(WindowSizeNames, SizeName(1920, 1080)), 0);
        }

        set
        {
            if (User is null || (uint)value >= (uint)WindowSizeNames.Length)
                return;
            var parts = WindowSizeNames[value].Split('×', StringSplitOptions.TrimEntries);
            User.WindowSize = new Vector2I(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
        }
    }

    /// <summary>The upscaler choice: 0 TAAU, 1 FSR 1, 2 native resolution (<see cref="ApplyScaling"/>).</summary>
    public int Upscaler { get; set; }

    /// <summary>The 3D render scale used when <see cref="Upscaler"/> is not native.</summary>
    public float RenderScale { get; set; } = 0.75f;

    /// <summary>Whether the room lights that cast shadows do (the others never do).</summary>
    public bool RoomShadows
    {
        get => _roomShadows;
        set
        {
            _roomShadows = value;
            foreach (var light in ShadowLights)
                light.CastsShadows = value;
        }
    }

    /// <summary>The room lights the scene gave shadows.</summary>
    public List<OmniLight3D> ShadowLights { get; } = [];

    // The scene's own values, which the relative options scale (captured once).
    public float BaseFogDensity { get; set; }

    public float BaseFogHeightDensity { get; set; }

    public float BaseGlowIntensity { get; set; } = 0.55f;

    public float BaseTonemapExposure { get; set; } = 1f;

    /// <summary>The <see cref="NeonOptionStore.Scene"/> options' values when captured: their "Reset to defaults".</summary>
    public Dictionary<string, float> Defaults { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Finds what the options act on in <paramref name="scene"/> and records the defaults and baselines. Call it before
    /// applying saved settings or <c>--option</c> overrides.
    /// </summary>
    public static NeonWorld Capture(Node scene, INeonDisplay? display, UserSettings? user, NeonFps? fps = null, Vector2I? screen = null)
    {
        var world = new NeonWorld
        {
            User = user,
            Environment = Find<WorldEnvironment>(scene),
            Moon = Find<DirectionalLight3D>(scene),
            Probes = Find<LightProbeVolume>(scene),
            City = Find<NeonCity>(scene),
            Apartment = Find<NeonApartment>(scene),
            Player = Find<FirstPersonController>(scene),
            Fps = fps,
            Display = display,
        };
        if (screen is { X: > 0 } limit)
            world.WindowSizeNames = [.. CommonSizes.Where(s => s.Width <= limit.X && s.Height <= limit.Y).Select(s => SizeName(s.Width, s.Height))];
        if (world.Apartment is { } apartment)
            world.ShadowLights.AddRange(apartment.Lights.Where(l => l.CastsShadows));
        world.CaptureBaselines();
        return world;
    }

    /// <summary>Records the baselines, the upscaler state and every scene option's current value as its default.</summary>
    public void CaptureBaselines()
    {
        if (Environment is { } env)
        {
            BaseFogDensity = env.FogDensity;
            BaseFogHeightDensity = env.FogHeightDensity;
        }

        if (Profile is { } profile)
        {
            BaseGlowIntensity = profile.GlowIntensity > 0f ? profile.GlowIntensity : 0.55f;
            BaseTonemapExposure = profile.TonemapExposure > 0f ? profile.TonemapExposure : 1f;
        }

        if (Display is { } display)
        {
            var scale = display.Scaling3DScale;
            Upscaler = scale >= 0.999f ? 2 : display.Scaling3DMode == Scaling3DMode.Fsr ? 1 : 0;
            RenderScale = scale >= 0.999f ? 0.75f : scale;
        }

        Defaults.Clear();
        foreach (var option in NeonOptions.All)
            if (option.Store == NeonOptionStore.Scene)
                Defaults[option.Key] = option.Clamp(this, option.Get(this));
    }

    /// <summary>The value "Reset to defaults" restores to a scene option.</summary>
    public float DefaultOf(NeonOption option) =>
        Defaults.TryGetValue(option.Key, out var value) ? value : option.Clamp(this, option.Get(this));

    /// <summary>The live value of <paramref name="option"/>, made valid.</summary>
    public float Value(NeonOption option) => option.Clamp(this, option.Get(this));

    /// <summary>
    /// Sets <paramref name="option"/> (clamped) on the game and records a scene option in the settings file while it differs
    /// from its default. Returns the value applied.
    /// </summary>
    public float Apply(NeonOption option, float value)
    {
        value = option.Clamp(this, value);
        option.Set(this, value);
        if (option.Store == NeonOptionStore.Scene && User is { } user)
        {
            if (MathF.Abs(value - DefaultOf(option)) < 1e-4f)
                user.Config.EraseSectionKey(Section, option.Key);
            else
                user.Config.SetValue(Section, option.Key, (double)value);
        }

        return value;
    }

    /// <summary>Applies the saved scene options; unknown keys are dropped.</summary>
    public void ApplySaved()
    {
        if (User is not { } user)
            return;
        foreach (var key in user.Config.GetSectionKeys(Section).ToArray())
        {
            if (NeonOptions.Find(key) is { Store: NeonOptionStore.Scene } option)
                Apply(option, (float)user.Config.GetDouble(Section, key, DefaultOf(option)));
            else
                user.Config.EraseSectionKey(Section, key);
        }
    }

    /// <summary>Restores every option of <paramref name="page"/> to its default.</summary>
    public void Reset(string page)
    {
        foreach (var option in NeonOptions.All)
            if (option.Page == page && option.Store == NeonOptionStore.Scene)
                Apply(option, DefaultOf(option));
        switch (page)
        {
            case NeonOptions.Display:
                User?.Reset(UserSettingsSection.Display);
                break;
            case NeonOptions.Graphics:
                if (User is { } user && GameHost.Project is { } project)
                    user.Shadows = project.Rendering.Shadows;
                break;
            case NeonOptions.Audio:
                User?.Reset(UserSettingsSection.Audio);
                break;
        }
    }

    /// <summary>Applies <see cref="Upscaler"/> and <see cref="RenderScale"/> to the renderer.</summary>
    public void ApplyScaling()
    {
        if (Display is not { } display)
            return;
        if (Upscaler == 2)
        {
            display.Scaling3DScale = 1f;
            return;
        }

        display.Scaling3DMode = Upscaler == 1 ? Scaling3DMode.Fsr : Scaling3DMode.Taau;
        display.Scaling3DScale = RenderScale;
    }

    /// <summary>The preset every option of which matches, or the "Custom" index.</summary>
    public int CurrentPreset()
    {
        var presets = NeonOptions.Presets;
        for (var i = 0; i < presets.Length; i++)
        {
            var matches = true;
            foreach (var (key, value) in presets[i].Values)
            {
                if (NeonOptions.Find(key) is { } option && MathF.Abs(Value(option) - option.Clamp(this, value)) > 1e-3f)
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return i;
        }

        return presets.Length;
    }

    /// <summary>Applies preset <paramref name="index"/>; "Custom" changes nothing.</summary>
    public void ApplyPreset(int index)
    {
        if ((uint)index >= (uint)NeonOptions.Presets.Length)
            return;
        foreach (var (key, value) in NeonOptions.Presets[index].Values)
            if (NeonOptions.Find(key) is { } option)
                Apply(option, value);
    }

    private static string SizeName(int width, int height) => string.Create(CultureInfo.InvariantCulture, $"{width} × {height}");

    private static T? Find<T>(Node node) where T : Node
    {
        if (node is T match)
            return match;
        var children = node.Children;
        for (var i = 0; i < children.Count; i++)
        {
            if (Find<T>(children[i]) is { } found)
                return found;
        }

        return null;
    }
}
