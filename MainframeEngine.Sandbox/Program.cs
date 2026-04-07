using MainframeEngine;
using MainframeEngine.Sandbox;

using var game = new Game();
var exitCode = game.Run();
Log.Debug($"Game Exit: {exitCode}");
return (int)exitCode;
