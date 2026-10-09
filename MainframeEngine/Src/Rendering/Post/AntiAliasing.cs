namespace MainframeEngine;

/// <summary>
/// Anti-aliasing of the main view (<c>rendering.antiAliasing</c>, <see cref="IVulkanContext.AntiAliasing"/>; ADR 0154,
/// ADR 0166). The 2D canvas, gizmos and UI draw after it, so they stay sharp. One mode at a time: TAA replaces FXAA.
/// </summary>
public enum AntiAliasing
{
    /// <summary>No anti-aliasing (default).</summary>
    None,

    /// <summary>FXAA 3.11, quality preset 12: one fullscreen pass, smooths geometric edges; cannot fix sub-pixel detail.</summary>
    Fxaa,

    /// <summary>
    /// Temporal anti-aliasing (ADR 0166): a Halton (2, 3) sub-pixel jitter, the depth prepass's motion vectors and a
    /// history resolved on the HDR image before auto exposure, glow and the tonemap. Smooths edges and sub-pixel detail
    /// (leaves, grass, thin branches) that FXAA cannot; a light sharpen follows (<see cref="IVulkanContext.TaaSharpness"/>).
    /// </summary>
    Taa,
}
