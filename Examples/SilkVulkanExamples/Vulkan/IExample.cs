using Silk.NET.Windowing;

namespace SilkVulkanExamples.Vulkan;

/// <summary>
/// A runnable example. <see cref="IDisposable.Dispose"/> shuts it down and releases its Vulkan resources.
/// </summary>
public interface IExample : IDisposable
{
    string Name { get; }
    void Initialize(IWindow window);
    void Run();
}
