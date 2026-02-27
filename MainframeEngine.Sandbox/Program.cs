using MainframeEngine;
using MainframeEngine.Sandbox;

try
{
    using var game = new Engine("Mainframe Engine Sandbox", new GameTest());
    var exitCode = game.Run();
    Log.Debug($"Game Exit: {exitCode}");
    return exitCode;
}
catch (Exception e)
{
    Log.Fatal(e);
    return e.HResult;
}