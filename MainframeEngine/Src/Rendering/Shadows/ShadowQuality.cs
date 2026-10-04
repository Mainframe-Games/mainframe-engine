namespace MainframeEngine;

/// <summary>
/// The shadow budget of a project (<c>project.mfproj</c> <c>rendering.shadows</c>, <see cref="RenderServer.ShadowQuality"/>).
/// Each level maps to <see cref="ShadowQualitySettings"/> (docs/design/shadow-system.md#quality-levels).
/// </summary>
public enum ShadowQuality
{
    /// <summary>No shadow maps (<see cref="RenderServer.ShadowsEnabled"/> = false): lit pipelines bind the fallback set.</summary>
    Off,

    /// <summary>Atlas ≤ 1024, hard (bilinear) taps, 2 cascades, maps ≤ 1024.</summary>
    Low,

    /// <summary>Atlas ≤ 2048, 3×3 PCF, 3 cascades, maps ≤ 2048.</summary>
    Medium,

    /// <summary>The shadow system's defaults: atlas ≤ 4096, 16-tap Poisson PCF, 4 cascades, maps at the lights' resolution.</summary>
    High,
}

/// <summary>What a <see cref="ShadowQuality"/> level sets on the <see cref="ShadowSystem"/> (<see cref="ShadowSystem.Apply"/>).</summary>
/// <param name="MaxAtlasSize">Largest spot/secondary-directional atlas (<see cref="ShadowSystem.MaxAtlasSize"/>).</param>
/// <param name="Filter">PCF kernel (<see cref="ShadowSystem.Filter"/>).</param>
/// <param name="FilterRadius">Kernel radius in texels (<see cref="ShadowSystem.FilterRadius"/>).</param>
/// <param name="CascadeLimit">Most sun cascades (<see cref="ShadowSystem.CascadeLimit"/>).</param>
/// <param name="ResolutionLimit">Largest side of one map: cascade layer, atlas tile, cube face (<see cref="ShadowSystem.ResolutionLimit"/>).</param>
public readonly record struct ShadowQualitySettings(int MaxAtlasSize, ShadowFilter Filter, float FilterRadius, int CascadeLimit, int ResolutionLimit)
{
    /// <summary>
    /// The settings of <paramref name="quality"/>. <see cref="ShadowQuality.High"/> equals a new <see cref="ShadowSystem"/>'s
    /// defaults; <see cref="ShadowQuality.Off"/> returns the <see cref="ShadowQuality.Low"/> settings (unused: there is no shadow system).
    /// </summary>
    public static ShadowQualitySettings For(ShadowQuality quality) => quality switch
    {
        ShadowQuality.High => new(4096, ShadowFilter.Poisson16, 1.5f, 4, Light.MaxShadowResolution),
        ShadowQuality.Medium => new(2048, ShadowFilter.Pcf3x3, 1.5f, 3, 2048),
        ShadowQuality.Low or ShadowQuality.Off => new(1024, ShadowFilter.Hard, 1f, 2, 1024),
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Unknown shadow quality."),
    };
}
