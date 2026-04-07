using MainframeEngine;
using MainframeEngine.Sandbox;
using Silk.NET.Maths;

var info = new EngineOptions
{
    GameName = "Mainframe Engine Sandbox",
    RenderingBackend = RenderingBackend.Vulkan,
    WindowSize = new Vector2D<int>(1920, 1080),
    IconPath = "Content/Branding/mg_300_circle.png"
};
using var game = new Game(info);
var exitCode = game.Run();
Log.Debug($"Game Exit: {exitCode}");
return (int)exitCode;
