using Silk.NET.Windowing;

namespace SilkVulkanExamples.Vulkan;

public interface IExample
{
    string Name { get; }
    void Initialize(IWindow window);
    void Run();
    void Shutdown();
}
