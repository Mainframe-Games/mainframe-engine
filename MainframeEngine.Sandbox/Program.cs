using MainframeEngine;
using MainframeEngine.Sandbox;

using var game = new Game("Mainframe Engine Sandbox", new GameTest());
var exitCode = game.Run();
Log.Debug($"Game Exit: {exitCode}");