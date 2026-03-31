using MainframeEngine;
using Silk.NET.Maths;
using SilkSpine;

using var game = new Game(new EngineOptions
{
    GameName = "Silk Spine",
    RenderingBackend = RenderingBackend.Vulkan,
    WindowSize = new Vector2D<int>(1920, 1080),
});
game.Run();
