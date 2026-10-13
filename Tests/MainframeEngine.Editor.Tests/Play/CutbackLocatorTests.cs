namespace MainframeEngine.Editor.Tests.Play;

public sealed class CutbackLocatorTests
{
    [Fact]
    public void TheSettingWinsWhenItExists()
    {
        Assert.Equal("/opt/cb", CutbackLocator.Find("/opt/cb", "/usr/bin", OsKind.Linux, "/home/u", null, p => p is "/opt/cb" or "/usr/bin/cutback"));
    }

    [Fact]
    public void AMissingSettingFallsThroughToPath()
    {
        Assert.Equal("/usr/bin/cutback", CutbackLocator.Find("/nope", "/usr/bin", OsKind.Linux, "/home/u", null, p => p == "/usr/bin/cutback"));
    }

    [Fact]
    public void MacFindsTheAppBundle()
    {
        Assert.Equal("/Applications/Cutback.app/Contents/MacOS/Cutback",
            CutbackLocator.Find(null, "", OsKind.MacOS, "/Users/u", null, p => p == "/Applications/Cutback.app/Contents/MacOS/Cutback"));
    }

    [Fact]
    public void WindowsFindsTheLocalProgramsInstall()
    {
        Assert.Equal(@"C:\Users\u\AppData\Local\Programs\Cutback\Cutback.exe",
            CutbackLocator.Find(null, "", OsKind.Windows, @"C:\Users\u", @"C:\Users\u\AppData\Local", p => p == @"C:\Users\u\AppData\Local\Programs\Cutback\Cutback.exe"));
    }

    [Fact]
    public void LinuxFindsTheUserBin()
    {
        Assert.Equal("/home/u/.local/bin/cutback", CutbackLocator.Find(null, "", OsKind.Linux, "/home/u", null, p => p == "/home/u/.local/bin/cutback"));
    }

    [Fact]
    public void NothingFoundIsNull()
    {
        Assert.Null(CutbackLocator.Find(null, "/usr/bin", OsKind.Linux, "/home/u", null, _ => false));
    }

    [Fact]
    public void MacAcceptsAnAppBundleAsTheSetting()
    {
        Assert.Equal("/Users/u/Apps/Cutback.app",
            CutbackLocator.Find("/Users/u/Apps/Cutback.app", "", OsKind.MacOS, "/Users/u", null, _ => false, d => d == "/Users/u/Apps/Cutback.app"));
    }

    [Fact]
    public void OnlyMacAcceptsADirectoryAsTheSetting()
    {
        Assert.Null(CutbackLocator.Find("/opt/Cutback.app", "", OsKind.Linux, "/home/u", null, _ => false, _ => true));
        Assert.Null(CutbackLocator.Find("/Users/u/Cutback", "", OsKind.MacOS, "/Users/u", null, _ => false, _ => true)); // a folder, not a bundle
    }

    [Theory]
    [InlineData("/Applications/Cutback.app", "/Applications/Cutback.app")]
    [InlineData("/Applications/Cutback.app/", "/Applications/Cutback.app")]
    [InlineData("/Applications/Cutback.app/Contents/MacOS/Cutback", "/Applications/Cutback.app")]
    public void MacOpensABundleThroughOpenSoItIsNotTheEditorsChild(string app, string bundle)
    {
        var (fileName, arguments) = CutbackLocator.LaunchCommand(app, "/u/r/a.mp4", OsKind.MacOS);
        Assert.Equal("open", fileName);
        // -n: a new instance every time, since a running Cutback reads no arguments (as on the other platforms)
        Assert.Equal(["-n", "-a", bundle, "--args", "/u/r/a.mp4"], arguments);
    }

    [Theory]
    [InlineData(OsKind.MacOS, "/usr/local/bin/cutback")]
    [InlineData(OsKind.Linux, "/home/u/.local/bin/cutback")]
    [InlineData(OsKind.Windows, @"C:\Programs\Cutback\Cutback.exe")]
    public void OtherPathsAreRunDirectly(OsKind os, string app)
    {
        var (fileName, arguments) = CutbackLocator.LaunchCommand(app, "/u/r/a.mp4", os);
        Assert.Equal(app, fileName);
        Assert.Equal(["/u/r/a.mp4"], arguments);
    }
}
