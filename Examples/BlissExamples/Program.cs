using BlissExamples;
using Veldrid;

var gameSettings = new GameSettings
{
    Title = "Bliss Examples",
    VSync = false,
    Backend = GraphicsBackend.Vulkan,
    
};

using var game = new Game(gameSettings);
return game.Run();