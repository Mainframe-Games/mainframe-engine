using MainframeEngine;
using MainframeEngine.Sandbox;

try
{
    var info = new Engine.Info
    {
        GameName = "Mainframe Engine Sandbox",
        RendererBackend = RenderingBackend.Vulkan
    };
    using var game = new Engine(info, new GameTest());
    var exitCode = game.Run();
    Log.Debug($"Game Exit: {exitCode}");
    return exitCode;
}
catch (Exception e)
{
    Log.Fatal(e);
    return e.HResult;
}