using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace SilkVulkanExamples;

public class SilkWindow : IDisposable
{
    private readonly IWindow _window;
    public IntPtr WindowHandle => _window.Handle;
    
    public SilkWindow(string title, int width, int height)
    {
        //Create a window.
        var options = WindowOptions.DefaultVulkan with
        {
            Size = new Vector2D<int>(width, height),
            Title = title
        };

        _window = Window.Create(options);
        _window.Initialize();

        if (_window.VkSurface is null)
            throw new Exception("Windowing platform doesn't support Vulkan.");
    }

    public void Run()
    {
        _window.Run();
    }

    public void Dispose()
    {
        _window.Dispose();
    }
}