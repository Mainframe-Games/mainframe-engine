using MainframeEngine;
using MainframeEngine.Sandbox;

using var game = new Game("Mainframe Engine Sandbox", new GameTest());
var exitCode = game.Run();
Log.Print($"Game Exit: {exitCode}");