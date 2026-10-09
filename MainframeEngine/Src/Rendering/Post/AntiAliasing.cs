namespace MainframeEngine;

/// <summary>
/// Screen-space anti-aliasing of the main view (<c>rendering.antiAliasing</c>, <see cref="IVulkanContext.AntiAliasing"/>;
/// ADR 0154). Runs on the tonemapped image before the 2D canvas, gizmos and UI, so those stay sharp. TAA is planned
/// (forest-showcase G8d.8) and not built yet.
/// </summary>
public enum AntiAliasing
{
    /// <summary>No anti-aliasing (default).</summary>
    None,

    /// <summary>FXAA 3.11, quality preset 12: one fullscreen pass, smooths geometric edges; cannot fix sub-pixel detail.</summary>
    Fxaa,
}
