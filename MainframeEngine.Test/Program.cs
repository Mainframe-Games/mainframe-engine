using MainframeEngine;
using MainframeEngine.Test;

// TODO: may want to hide the console terminal window - https://stackoverflow.com/questions/3571627/show-hide-the-console-window-of-a-c-sharp-console-application

using var game = new Game("Mainframe Engine", new GameTest());
var exitCode = game.Run();
Console.WriteLine($"Game Exit: {exitCode}");