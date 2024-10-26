using MainframeEngine;
using MainframeEngine.Test;
using Silk.NET.Windowing;

using var game = new Game(WindowOptions.Default, new GameTest());
var exitCode = game.Run();
Console.WriteLine($"Game Exit: {exitCode}");