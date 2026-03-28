namespace MainframeEngine;

/// <summary>
/// Creates a sky from six cubemap face images.
/// <paramref name="facePaths"/> must contain exactly 6 paths in Vulkan face order:
/// +X, -X, +Y, -Y, +Z, -Z.
/// </summary>
public class SkyCubemap(IRenderer renderer, string[] facePaths)
        : SkyEnvironment(renderer, SkyEnvironmentType.Cubemap, cubeFacePaths: facePaths);