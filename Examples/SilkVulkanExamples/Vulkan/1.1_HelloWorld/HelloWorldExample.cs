using Silk.NET.Vulkan;

namespace SilkVulkanExamples.Vulkan;

/// <summary>
/// 1.1 Hello World - Clears the screen to DarkSlateGray. No geometry, no pipeline.
/// </summary>
public unsafe class HelloWorldExample : ExampleBase
{
    // No pipeline needed - clear color is set via render pass load op in ExampleBase.
    // OnRender just lets the render pass clear the framebuffer.
    protected override void OnRender(CommandBuffer cmd, uint imageIndex, double delta)
    {
        // Nothing to draw - the render pass clear color (DarkSlateGray) is set in ExampleBase.
    }
}
