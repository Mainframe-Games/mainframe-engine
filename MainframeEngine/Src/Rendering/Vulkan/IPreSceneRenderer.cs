using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Offscreen work recorded before the frame's scene passes (G11.5): the game UI's world-space panels
/// (<see cref="UiPanel3D"/>) render their contexts into textures here, so meshes sample this frame's UI. Register with
/// <see cref="IVulkanContext.AddPreSceneRenderer"/>; <see cref="RenderServer.RenderOffscreen"/> runs them (in registration
/// order) after the shadow pass and before the sub-viewports, with no render pass active.
/// </summary>
public interface IPreSceneRenderer
{
    /// <summary>Records offscreen work (no render pass is active on <paramref name="commandBuffer"/>).</summary>
    void RecordPreScene(CommandBuffer commandBuffer);
}
