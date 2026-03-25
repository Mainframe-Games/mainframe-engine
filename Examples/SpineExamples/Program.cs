using System.Numerics;
using MainframeEngine;
using SilkSpine;

using var engine = new Engine(new Engine.Info
{
    GameName = "Silk Spine",
    RenderingBackend = RenderingBackend.Vulkan,
    WindowSize = new Vector2(1920, 1080),
}, new Game());

engine.Run();
