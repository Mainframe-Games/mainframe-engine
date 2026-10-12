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
}
