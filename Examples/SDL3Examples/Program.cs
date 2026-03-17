using SDL3;
using SDL3Examples;

if (!SDL.Init(SDL.InitFlags.Video))
{
    SDL.LogError(SDL.LogCategory.System, $"SDL could not initialize: {SDL.GetError()}");
    return;
}

const SDL.WindowFlags flags = SDL.WindowFlags.Vulkan | SDL.WindowFlags.Resizable;

if (!SDL.CreateWindowAndRenderer("SDL3 Create Window", 1920, 1080, flags, out var window, out var renderer))
{
    SDL.LogError(SDL.LogCategory.Application, $"Error creating window and rendering: {SDL.GetError()}");
    return;
}

SDL.SetRenderVSync(renderer, 1);
SDL.SetRenderDrawColor(renderer, 100, 149, 237, 0);

var loop = true;
var fpsCounter = new FPSCounter();

while (loop)
{
    while (SDL.PollEvent(out var e))
    {
        if ((SDL.EventType)e.Type == SDL.EventType.Quit)
        {
            loop = false;
        }
    }

    // Update
    {
        fpsCounter.Update();
    }
    
    SDL.RenderClear(renderer);
    SDL.SetRenderDrawColor(renderer, 100, 149, 237, 0);
    
    // Draw
    {
        SDL.RenderDebugText(renderer, 10, 10, $"FPS: {fpsCounter.FPS:N0}");
    }

    SDL.RenderPresent(renderer);
}

SDL.DestroyRenderer(renderer);
SDL.DestroyWindow(window);

SDL.Quit();