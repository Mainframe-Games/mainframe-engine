using MainframeEngine;
using MainframeEngine.Test;

using var game = new Game("Mainframe Engine", new GameTest());
var exitCode = game.Run();
Console.WriteLine($"Game Exit: {exitCode}");