namespace MainframeEngine;

/// <summary>
/// Creates a sky from an equirectangular panoramic image file.
/// </summary>
public class SkyPanoramic(IRenderer renderer, string? panoramicPath)
    : SkyEnvironment(renderer, SkyEnvironmentType.Panoramic, panoramicPath);