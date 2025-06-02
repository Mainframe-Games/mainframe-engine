using Silk.NET.Windowing;
using SilkSpine;
using Monitor = Silk.NET.Windowing.Monitor;

var windowOptions = WindowOptions.Default with
{
    Title = "Silk Spine",
    VSync = true
};
using var window = Window.Create(windowOptions);
var mainMonitor = Monitor.GetMainMonitor(window);
window.Center(mainMonitor);

var game = new Game();
window.Load += () => game.OnLoad(window);
window.FramebufferResize += s => game.OnFramebufferResize(s);
window.Update += delta => game.OnUpdate(delta);
window.Render += delta => game.OnRender(delta);
window.Closing += () => game.OnClose();

window.Run();

Console.WriteLine("Game terminated.");
