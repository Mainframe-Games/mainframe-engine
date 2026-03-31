using Silk.NET.Maths;

namespace MainframeEngine;

/// <summary>
/// Backend-agnostic renderer interface. Implemented by VulkanRenderer.
/// Shapes and game code target this interface so draw logic stays the same regardless of backend.
/// </summary>
public interface IRenderer : IDisposable
{
    RenderingBackend Backend { get; }

    /// <summary>Enables or disables vertical sync. Triggers swapchain recreation on change.</summary>
    bool VSync { get; set; }

    /// <summary>Called by Engine when the framebuffer is resized.</summary>
    void OnResize(Vector2D<int> newSize);

    /// <summary>Called by Engine before game.OnRender. Acquires swapchain image for Vulkan.</summary>
    void BeginFrame();

    /// <summary>Called by Engine after game.OnRender. Submits and presents for Vulkan.</summary>
    void EndFrame();

    /// <summary>Sets the color used when Clear() is called.</summary>
    void SetClearColor(float r, float g, float b, float a = 1f);

    /// <summary>Clears the current framebuffer to the set clear color.</summary>
    void Clear();

    void EnableDepthTest();
    void DisableDepthTest();
}
