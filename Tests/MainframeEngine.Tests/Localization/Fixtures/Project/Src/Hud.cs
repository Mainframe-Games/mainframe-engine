// Extraction fixture for the GetText.NET extractor (not compiled: see MainframeEngine.Tests.csproj).
using MainframeEngine.Localization;

namespace Fixture;

public static class Hud
{
    public static void Draw(int score, long enemies, string player)
    {
        var title = Tr._("Settings");
        var scoreText = Tr._("Score: {0}", score);
        var open = Tr.P("menu", "Open");
        var left = Tr.N("{0} enemy left", "{0} enemies left", enemies);
        var coins = Tr.NP("hud", "{0} coin", "{0} coins", enemies);
        var greeting = Tr._($"Hello, {player}!");
        var notExtracted = string.Concat(title, scoreText, open, left, coins, greeting);
    }
}
