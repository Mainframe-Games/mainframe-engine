using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Something drawn after tonemapping, in the swapchain's (sRGB) encoding, below the dev overlay — the 2D canvas, screen gizmos
/// and the game UI (<see cref="VulkanUiRenderer"/>). Register with <see cref="IVulkanContext.AddOverlayRenderer"/>;
/// renderers run in <see cref="OverlayOrder"/> order (canvas, gizmos, UI).
/// </summary>
/// <remarks>
/// Frame order inside <see cref="IVulkanContext.BeginOverlayPass"/>: the scene pass ends, then
/// <see cref="RecordOffscreen"/> runs for every registered renderer (no render pass active: begin and end your own
/// offscreen passes), then the tonemap pass begins on the swapchain image, then <see cref="RecordOverlay"/> runs for
/// every renderer inside the overlay pass (<see cref="IVulkanContext.OverlayRenderPass"/>).
/// </remarks>
public interface IOverlayRenderer
{
    /// <summary>Records offscreen work (no render pass is active on <paramref name="commandBuffer"/>).</summary>
    void RecordOffscreen(CommandBuffer commandBuffer);

    /// <summary>Draws into the overlay pass (active on <paramref name="commandBuffer"/>; viewport and scissor are yours to set).</summary>
    void RecordOverlay(CommandBuffer commandBuffer);
}
