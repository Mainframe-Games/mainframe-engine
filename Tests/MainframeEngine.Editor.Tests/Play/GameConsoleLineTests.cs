namespace MainframeEngine.Editor.Tests;

public sealed class GameConsoleLineTests
{
    [Theory]
    [InlineData("[17:15:55.305] [INFO]\t[L10n] Locale 'en' (0 messages)", OutputLevel.Info, "[L10n] Locale 'en' (0 messages)")]
    [InlineData("[17:15:55.305] [Debug]\t[Jitter2] Creating new world.", OutputLevel.Debug, "[Jitter2] Creating new world.")]
    [InlineData("[17:15:55.305] [WARN]\t[Audio] No device", OutputLevel.Warning, "[Audio] No device")]
    [InlineData("[17:15:55.305] [FATAL]\tboom", OutputLevel.Error, "boom")]
    [InlineData("Unhandled exception. System.Exception: x", OutputLevel.Error, "Unhandled exception. System.Exception: x")]
    [InlineData("plain text\twith a tab", OutputLevel.Info, "plain text\twith a tab")]
    [InlineData("[x] [ODD]\tkept whole", OutputLevel.Info, "[x] [ODD]\tkept whole")]
    [InlineData("", OutputLevel.Info, "")]
    public void ConsoleLinesKeepTheirLevelAndLoseTheTimeStamp(string line, OutputLevel level, string text)
    {
        var parsed = PlayController.ParseConsoleLine(line);
        Assert.Equal(level, parsed.Level);
        Assert.Equal(text, parsed.Text);
    }
}
