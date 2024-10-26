using Silk.NET.Windowing;
using SilkSpine;
using Monitor = Silk.NET.Windowing.Monitor;

var game = new Game();

var windowOptions = WindowOptions.Default;
windowOptions.Title = "Silk Spine";
using var window = Window.Create(windowOptions) ?? throw new NullReferenceException();

// set window to center of monitor
var monitor = Monitor.GetMainMonitor(window);
var centerScreen = (monitor.VideoMode.Resolution - window.Size) / 2;
window.Position = centerScreen!.Value;

window.Load += () => game.OnLoad(window);
window.FramebufferResize += s => game.OnFramebufferResize(s);
window.Update += delta => game.OnUpdate(delta);
window.Render += delta => game.OnRender(delta);
window.Closing += () => game.OnClose();

window.Run();

Console.WriteLine("Game terminated.");
