using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>The renderer switches the menu changes (the Vulkan renderer in the game, a plain object in tests).</summary>
public interface IForestDisplay
{
    AntiAliasing AntiAliasing { get; set; }

    Scaling3DMode Scaling3DMode { get; set; }

    float Scaling3DScale { get; set; }

    bool VSync { get; set; }
}

/// <summary>The game's renderer as an <see cref="IForestDisplay"/>.</summary>
public sealed class RendererDisplay(IRenderer renderer, IVulkanContext vulkan) : IForestDisplay
{
    public AntiAliasing AntiAliasing { get => vulkan.AntiAliasing; set => vulkan.AntiAliasing = value; }

    public Scaling3DMode Scaling3DMode { get => vulkan.Scaling3DMode; set => vulkan.Scaling3DMode = value; }

    public float Scaling3DScale { get => vulkan.Scaling3DScale; set => vulkan.Scaling3DScale = value; }

    public bool VSync { get => renderer.VSync; set => renderer.VSync = value; }

    /// <summary>The tree's renderer, or null without a Vulkan one (headless).</summary>
    public static RendererDisplay? Of(SceneTree? tree) =>
        tree?.Servers.Render is { Vulkan: { } vulkan } render ? new RendererDisplay(render.Renderer, vulkan) : null;
}

/// <summary>
/// What the pause menu's options act on (ADR 0180): the Forest scene's sun, environment, post profile, lens, probes,
/// terrain, player, audio and frame-rate readout, the renderer, and the player's <see cref="ForestSettings"/>. Built by
/// <see cref="Capture"/>, which also records the scene's own values: the defaults of the <see cref="ForestOptionStore.Scene"/>
/// options and the baselines the relative ones (haze, glow, wind: "100 %") scale.
/// </summary>
public sealed class ForestWorld
{
    /// <summary>The persisted settings the options read and write.</summary>
    public ForestSettings Settings { get; set; } = new();

    public WorldEnvironment? Environment { get; set; }

    public PostProcessProfile? Profile => Environment?.PostProcess;

    public CameraAttributesPractical? Lens => Environment?.CameraAttributes;

    public DirectionalLight3D? Sun { get; set; }

    public LightProbeVolume? Probes { get; set; }

    public Terrain3D? Terrain { get; set; }

    public FirstPersonController? Player { get; set; }

    public ForestAudio? Audio { get; set; }

    public FpsHud? Fps { get; set; }

    public IForestDisplay? Display { get; set; }

    /// <summary>The upscaler choice: 0 TAAU, 1 FSR 1, 2 native resolution (<see cref="ApplyScaling"/>).</summary>
    public int Upscaler { get; set; }

    /// <summary>The 3D render scale used when <see cref="Upscaler"/> is not native.</summary>
    public float RenderScale { get; set; } = 0.75f;

    // The scene's own values, which the relative options scale (captured once).
    public float BaseFogDensity { get; set; }

    public float BaseVolumetricFogDensity { get; set; }

    public float BaseGlowIntensity { get; set; } = 0.3f;

    public float BaseWindStrength { get; set; }

    public float BaseTonemapExposure { get; set; } = 1f;

    public float BaseAngularDistance { get; set; } = 0.5f;

    /// <summary>The <see cref="ForestOptionStore.Scene"/> options' values when captured: their "Reset to defaults".</summary>
    public Dictionary<string, float> Defaults { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Finds what the options act on in <paramref name="scene"/> (by type, anywhere below it) and records the defaults
    /// and baselines. Call it before applying saved settings or <c>--set</c> overrides.
    /// </summary>
    public static ForestWorld Capture(Node scene, IForestDisplay? display, ForestSettings settings)
    {
        var world = new ForestWorld
        {
            Settings = settings,
            Environment = Find<WorldEnvironment>(scene),
            Sun = Find<DirectionalLight3D>(scene),
            Probes = Find<LightProbeVolume>(scene),
            Terrain = Find<Terrain3D>(scene),
            Player = Find<FirstPersonController>(scene),
            Audio = Find<ForestAudio>(scene),
            Fps = Find<FpsHud>(scene),
            Display = display,
        };
        world.CaptureBaselines();
        return world;
    }

    /// <summary>Records the baselines, the upscaler state and every scene option's current value as its default.</summary>
    public void CaptureBaselines()
    {
        if (Environment is { } env)
        {
            BaseFogDensity = env.FogDensity;
            BaseVolumetricFogDensity = env.VolumetricFogDensity;
            BaseWindStrength = env.WindStrength;
        }

        if (Profile is { } profile)
        {
            BaseGlowIntensity = profile.GlowIntensity > 0f ? profile.GlowIntensity : 0.3f;
            BaseTonemapExposure = profile.TonemapExposure > 0f ? profile.TonemapExposure : 1f;
        }

        if (Sun is { LightAngularDistance: > 0f } sun)
            BaseAngularDistance = sun.LightAngularDistance;
        if (Sun is not null)
            BaseSunAngles = SunAngles();
        if (Display is { } display)
        {
            var scale = display.Scaling3DScale;
            Upscaler = scale >= 0.999f ? 2 : display.Scaling3DMode == Scaling3DMode.Fsr ? 1 : 0;
            RenderScale = scale >= 0.999f ? 0.75f : scale;
        }

        Defaults.Clear();
        foreach (var option in ForestOptions.All)
            if (option.Store == ForestOptionStore.Scene)
                Defaults[option.Key] = option.Clamp(option.Get(this));
    }

    /// <summary>The value "Reset to defaults" restores.</summary>
    public float DefaultOf(ForestOption option)
    {
        if (option.Store == ForestOptionStore.Scene)
            return Defaults.TryGetValue(option.Key, out var value) ? value : option.Clamp(option.Get(this));
        var fresh = new ForestWorld { Settings = new ForestSettings() };
        return option.Clamp(option.Get(fresh));
    }

    /// <summary>The live value of <paramref name="option"/>, made valid.</summary>
    public float Value(ForestOption option) => option.Clamp(option.Get(this));

    /// <summary>
    /// Sets <paramref name="option"/> (clamped) on the game and records it in <see cref="Settings"/>: a scene option's
    /// value is stored only while it differs from its default. Returns the value applied.
    /// </summary>
    public float Apply(ForestOption option, float value)
    {
        value = option.Clamp(value);
        option.Set(this, value);
        if (option.Store == ForestOptionStore.Scene)
        {
            if (MathF.Abs(value - DefaultOf(option)) < 1e-4f)
                Settings.Graphics.Remove(option.Key);
            else
                Settings.Graphics[option.Key] = value;
        }

        return value;
    }

    /// <summary>Applies the saved graphics options (<see cref="ForestSettings.Graphics"/>); unknown keys are ignored.</summary>
    public void ApplySaved()
    {
        foreach (var (key, value) in Settings.Graphics.ToArray())
        {
            if (ForestOptions.Find(key) is { Store: ForestOptionStore.Scene } option)
                Apply(option, value);
            else
                Settings.Graphics.Remove(key);
        }
    }

    /// <summary>Restores every option of <paramref name="page"/> to its default.</summary>
    public void Reset(string page)
    {
        foreach (var option in ForestOptions.All)
            if (option.Page == page)
                Apply(option, DefaultOf(option));
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

    /// <summary>The sun's elevation above the horizon and azimuth from north (clockwise, degrees), from its rotation.</summary>
    public (float Elevation, float Azimuth) SunAngles()
    {
        if (Sun is null)
            return (ValleyLayout.SunElevationDegrees, ValleyLayout.SunAzimuthDegrees);
        // The light shines along its −Z: +Z is towards the sun (ValleyLayout.TowardsSun's convention, north −Z).
        var towards = Vector3.Transform(Vector3.UnitZ, Sun.Rotation);
        var elevation = float.RadiansToDegrees(MathF.Asin(Math.Clamp(towards.Y, -1f, 1f)));
        var azimuth = float.RadiansToDegrees(MathF.Atan2(towards.X, -towards.Z));
        if (azimuth < 0f)
            azimuth += 360f;
        if (azimuth >= 357.5f)
            azimuth -= 360f; // north reads as 0°, not 360°
        return (elevation, azimuth);
    }

    /// <summary>The sun's angles when captured (the direction the light probes were baked for).</summary>
    public (float Elevation, float Azimuth) BaseSunAngles { get; set; } = (ValleyLayout.SunElevationDegrees, ValleyLayout.SunAzimuthDegrees);

    /// <summary>
    /// Points the sun (ForestScene's convention: pitch down by the elevation, then turn from north to the azimuth). Away
    /// from the baked direction the probes stop baking when stale (<see cref="LightProbeVolume.BakeWhenStale"/>): their
    /// bake no longer matches the sun on purpose, and a minute-long background bake per launch (or per slider move before
    /// the first check) would take the probes away meanwhile. The lighting is approximate, as the menu says.
    /// </summary>
    public void SetSunAngles(float elevation, float azimuth)
    {
        if (Sun is null)
            return;
        Sun.RotationDegrees = new Vector3(-elevation, 180f - azimuth, 0f);
        if (Probes is { } probes && (MathF.Abs(elevation - BaseSunAngles.Elevation) > 0.5f || MathF.Abs(azimuth - BaseSunAngles.Azimuth) > 0.5f))
            probes.BakeWhenStale = false;
    }

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
