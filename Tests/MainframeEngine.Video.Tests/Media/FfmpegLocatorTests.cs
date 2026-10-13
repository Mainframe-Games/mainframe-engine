using System.Runtime.InteropServices;
using MainframeEngine.Video.Media;

namespace MainframeEngine.Video.Tests.Media;

public sealed class FfmpegLocatorTests
{
    private static FfmpegLocator Locator(
        OSPlatform platform,
        IEnumerable<string> existingFiles,
        string? userSetting = null,
        string? path = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var files = new HashSet<string>(existingFiles, StringComparer.Ordinal);
        return new FfmpegLocator(
            platform,
            fileExists: files.Contains,
            userConfiguredPath: userSetting,
            pathVariable: path,
            environment: env ?? new Dictionary<string, string>());
    }

    // ---- resolution order ---------------------------------------------------------------------

    [Fact]
    public void UserSettingPointingAtTheBinaryWinsOverPATH()
    {
        var locator = Locator(
            OSPlatform.OSX,
            ["/custom/ffmpeg", "/custom/ffprobe", "/usr/local/bin/ffmpeg", "/usr/local/bin/ffprobe"],
            userSetting: "/custom/ffmpeg",
            path: "/usr/local/bin");

        var location = locator.Locate();

        Assert.Equal("/custom/ffmpeg", location.FfmpegPath);
        Assert.Equal("/custom/ffprobe", location.FfprobePath);
        Assert.Equal(FfmpegLocationSource.UserSetting, location.Source);
    }

    [Fact]
    public void UserSettingMayBeTheDirectoryContainingTheBinaries()
    {
        var locator = Locator(OSPlatform.Linux, ["/custom/ffmpeg", "/custom/ffprobe"], userSetting: "/custom");

        var location = locator.Locate();

        Assert.Equal("/custom/ffmpeg", location.FfmpegPath);
        Assert.Equal("/custom", location.Directory);
    }

    [Fact]
    public void UserSettingUsesTheTargetPlatformSeparatorsNotTheHost()
    {
        // Path.GetDirectoryName would mangle this on a non-Windows host.
        var locator = Locator(
            OSPlatform.Windows,
            [@"C:\Tools\ffmpeg\ffmpeg.exe", @"C:\Tools\ffmpeg\ffprobe.exe"],
            userSetting: @"C:\Tools\ffmpeg\ffmpeg.exe");

        var location = locator.Locate();

        Assert.Equal(@"C:\Tools\ffmpeg\ffprobe.exe", location.FfprobePath);
        Assert.Equal(@"C:\Tools\ffmpeg", location.Directory);
        Assert.Equal(FfmpegLocationSource.UserSetting, location.Source);
    }

    [Fact]
    public void AStaleUserSettingFallsThroughToPATH()
    {
        var locator = Locator(
            OSPlatform.Linux,
            ["/usr/bin/ffmpeg", "/usr/bin/ffprobe"],
            userSetting: "/gone/ffmpeg",
            path: "/usr/bin");

        var location = locator.Locate();

        Assert.Equal("/usr/bin/ffmpeg", location.FfmpegPath);
        Assert.Equal(FfmpegLocationSource.Path, location.Source);
    }

    [Fact]
    public void PATHIsSearchedLeftToRight()
    {
        var locator = Locator(
            OSPlatform.Linux,
            ["/a/ffmpeg", "/a/ffprobe", "/b/ffmpeg", "/b/ffprobe"],
            path: "/b:/a");

        Assert.Equal("/b/ffmpeg", locator.Locate().FfmpegPath);
    }

    [Fact]
    public void PATHUsesThePlatformSeparator()
    {
        var locator = Locator(
            OSPlatform.Windows,
            [@"C:\tools\ffmpeg.exe", @"C:\tools\ffprobe.exe"],
            path: @"C:\Windows;C:\tools");

        Assert.Equal(@"C:\tools\ffmpeg.exe", locator.Locate().FfmpegPath);
    }

    [Fact]
    public void CommonLocationsAreSearchedAfterPATH()
    {
        var locator = Locator(
            OSPlatform.OSX,
            ["/opt/homebrew/bin/ffmpeg", "/opt/homebrew/bin/ffprobe"],
            path: "/usr/bin:/bin");

        var location = locator.Locate();

        Assert.Equal("/opt/homebrew/bin/ffmpeg", location.FfmpegPath);
        Assert.Equal(FfmpegLocationSource.CommonLocation, location.Source);
    }

    [Fact]
    public void WindowsCommonLocationsIncludeWingetAndProgramFiles()
    {
        var env = new Dictionary<string, string>
        {
            ["ProgramFiles"] = @"C:\Program Files",
            ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local",
        };
        var locator = Locator(
            OSPlatform.Windows,
            [@"C:\Users\me\AppData\Local\Microsoft\WinGet\Links\ffmpeg.exe", @"C:\Users\me\AppData\Local\Microsoft\WinGet\Links\ffprobe.exe"],
            env: env);

        Assert.Equal(@"C:\Users\me\AppData\Local\Microsoft\WinGet\Links\ffmpeg.exe", locator.Locate().FfmpegPath);
    }

    [Fact]
    public void LinuxCommonLocationsIncludeUsrBin()
    {
        var locator = Locator(OSPlatform.Linux, ["/usr/bin/ffmpeg", "/usr/bin/ffprobe"]);

        Assert.Equal("/usr/bin/ffmpeg", locator.Locate().FfmpegPath);
    }

    // ---- ffprobe is required ------------------------------------------------------------------

    [Fact]
    public void ALocationWithFfmpegButNoFfprobeIsSkipped()
    {
        var locator = Locator(
            OSPlatform.Linux,
            ["/a/ffmpeg", "/b/ffmpeg", "/b/ffprobe"],
            path: "/a:/b");

        Assert.Equal("/b", locator.Locate().Directory);
    }

    [Fact]
    public void FfmpegWithoutFfprobeAnywhereReportsTheMissingFfprobe()
    {
        var locator = Locator(OSPlatform.Linux, ["/usr/bin/ffmpeg"], path: "/usr/bin");

        var act = () => locator.Locate();

        var e = Assert.Throws<FfmpegMissingException>(act);
        Assert.Matches("ffprobe[\\s\\S]*/usr/bin", e.Message);
    }

    // ---- not found ----------------------------------------------------------------------------

    [Fact]
    public void TryLocateReturnsNullWhenNothingIsFound()
    {
        var locator = Locator(OSPlatform.Linux, [], path: "/nowhere");

        Assert.Null(locator.TryLocate());
    }

    [Theory]
    [InlineData("Windows", "winget install")]
    [InlineData("OSX", "brew install ffmpeg")]
    [InlineData("Linux", "apt install ffmpeg")]
    public void NotFoundMessageNamesThePlatformInstallCommand(string platformName, string expectedHint)
    {
        var platform = OSPlatform.Create(platformName.ToUpperInvariant());
        var locator = Locator(platform, [], path: "/nowhere");

        var act = () => locator.Locate();

        var e = Assert.Throws<FfmpegMissingException>(act);
        Assert.Contains(expectedHint, e.Message, StringComparison.Ordinal);
        Assert.Contains("ffmpeg", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NotFoundMessageMentionsTheSettingWhenOneWasConfigured()
    {
        var locator = Locator(OSPlatform.OSX, [], userSetting: "/old/ffmpeg", path: "/nowhere");

        var act = () => locator.Locate();

        var e = Assert.Throws<FfmpegMissingException>(act);
        Assert.Contains("/old/ffmpeg", e.Message, StringComparison.Ordinal);
    }

    // ---- defaults -----------------------------------------------------------------------------

    [Fact]
    public void DefaultConstructorUsesTheCurrentProcessEnvironment()
    {
        // Only checks it does not throw and picks the right binary name for this OS.
        var locator = new FfmpegLocator();

        Assert.Equal(OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg", locator.ExecutableName("ffmpeg"));
    }
}
