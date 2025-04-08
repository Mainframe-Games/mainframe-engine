using SDL3;

namespace SilkVulkanExamples;

public class Window : IDisposable
{
    public readonly IntPtr WindowHandle;
    public readonly IntPtr RendererHandle;
    
    public Window(string title)
    {
        if (!SDL.Init(SDL.InitFlags.Video))
        {
            SDL.LogError(SDL.LogCategory.System, $"SDL could not initialize: {SDL.GetError()}");
            return;
        }

        if (!SDL.CreateWindowAndRenderer(title, 800, 600, 0, out WindowHandle, out RendererHandle))
        {
            SDL.LogError(SDL.LogCategory.Application, $"Error creating window and rendering: {SDL.GetError()}");
            return;
        }

        SDL.SetRenderDrawColor(RendererHandle, 100, 149, 237, 0);
    }

    public void Run()
    {
        var loop = true;
        
        while (loop)
        {
            while (SDL.PollEvent(out var e))
            {
                if (e.Type == (uint)SDL.EventType.Quit)
                    loop = false;
            }

            SDL.RenderClear(RendererHandle);
            SDL.RenderPresent(RendererHandle);
        }
    }

    public void Dispose()
    {
        SDL.DestroyRenderer(RendererHandle);
        SDL.DestroyWindow(RendererHandle);
        
        SDL.Quit();
    }
}