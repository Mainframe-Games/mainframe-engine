using System.Runtime.InteropServices;

namespace MainframeEngine.Editor.Tests.Updates;

public sealed class InstallLocationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-install").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void OnlyTheReleasedPlatformsHaveARid()
    {
        Assert.Equal("osx-arm64", UpdatePlatform.Rid(OSPlatform.OSX, Architecture.Arm64));
        Assert.Equal("win-x64", UpdatePlatform.Rid(OSPlatform.Windows, Architecture.X64));
        Assert.Equal("linux-x64", UpdatePlatform.Rid(OSPlatform.Linux, Architecture.X64));
        Assert.Null(UpdatePlatform.Rid(OSPlatform.OSX, Architecture.X64));
        Assert.Null(UpdatePlatform.Rid(OSPlatform.Windows, Architecture.Arm64));
        Assert.Null(UpdatePlatform.Rid(OSPlatform.Linux, Architecture.Arm64));
        Assert.Null(UpdatePlatform.Rid(OSPlatform.FreeBSD, Architecture.X64));
    }

    [Fact]
    public void AssetNamesMatchThePackagingScript()
    {
        var v = new ReleaseVersion(1, 2, 3);
        Assert.Equal("MainframeEngine-1.2.3-osx-arm64.tar.gz", UpdatePlatform.AssetName(v, "osx-arm64"));
        Assert.Equal("MainframeEngine-1.2.3-linux-x64.tar.gz", UpdatePlatform.AssetName(v, "linux-x64"));
        Assert.Equal("MainframeEngine-1.2.3-win-x64.zip", UpdatePlatform.AssetName(v, "win-x64"));
    }

    [Fact]
    public void StagedRootsAndExecutablesFollowTheArchiveLayout()
    {
        var v = new ReleaseVersion(1, 2, 3);
        var mac = UpdatePlatform.StagedRoot("x", v, "osx-arm64");
        Assert.Equal(Path.Combine("x", "Mainframe Engine.app"), mac);
        Assert.Equal(Path.Combine(mac, "Contents", "MacOS", "MainframeEngine.Editor"), UpdatePlatform.ExecutablePath(mac, "osx-arm64"));
        var win = UpdatePlatform.StagedRoot("x", v, "win-x64");
        Assert.Equal(Path.Combine("x", "MainframeEngine-1.2.3-win-x64"), win);
        Assert.Equal(Path.Combine(win, "MainframeEngine.Editor.exe"), UpdatePlatform.ExecutablePath(win, "win-x64"));
        var linux = UpdatePlatform.StagedRoot("x", v, "linux-x64");
        Assert.Equal(Path.Combine(linux, "MainframeEngine.Editor"), UpdatePlatform.ExecutablePath(linux, "linux-x64"));
    }

    [Fact]
    public void TheMacRootIsTheEnclosingBundle()
    {
        var app = Path.Combine(_directory, "Apps", "Mainframe Engine.app");
        var baseDirectory = Path.Combine(app, "Contents", "MacOS") + Path.DirectorySeparatorChar;
        Assert.Equal(Path.GetFullPath(app), InstallLocation.FindRoot(baseDirectory, "osx-arm64"));
    }

    [Fact]
    public void AMacExecutableOutsideABundleHasNoRoot()
    {
        Assert.Null(InstallLocation.FindRoot(Path.Combine(_directory, "bin", "Release") + Path.DirectorySeparatorChar, "osx-arm64"));
        Assert.Equal(InstallKind.NotBundled, InstallLocation.Inspect(Path.Combine(_directory, "bin"), "osx-arm64").Kind);
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("win-x64")]
    public void OtherRootsAreTheEditorFolder(string rid)
    {
        var folder = Path.Combine(_directory, "My Tools", "MainframeEngine-1.0.0-" + rid);
        Assert.Equal(Path.GetFullPath(folder), InstallLocation.FindRoot(folder + Path.DirectorySeparatorChar, rid));
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("win-x64")]
    [InlineData("osx-arm64")]
    public void AFolderWithoutTheEditorExecutableIsNotAnInstall(string rid)
    {
        // Any folder holding the running files is not enough: replacing it would move everything else in it aside.
        var root = UpdatePlatform.IsMac(rid) ? Path.Combine(_directory, "Mainframe Engine.app") : Path.Combine(_directory, "Shared Tools");
        var baseDirectory = UpdatePlatform.IsMac(rid) ? Path.Combine(root, "Contents", "MacOS") : root;
        var checkedPaths = new List<string>();
        var location = InstallLocation.Inspect(baseDirectory, rid, _ => true, p =>
        {
            checkedPaths.Add(p);
            return false;
        });
        Assert.Equal(InstallKind.NotBundled, location.Kind);
        Assert.False(location.CanReplace);
        Assert.Equal(UpdatePlatform.ExecutablePath(Path.GetFullPath(root), rid), Assert.Single(checkedPaths));
        Assert.DoesNotContain(".app", location.Hint, StringComparison.Ordinal); // the hint fits every platform
        Assert.Contains("by hand", location.Hint, StringComparison.Ordinal);

        Assert.True(InstallLocation.Inspect(baseDirectory, rid, _ => true, _ => true).CanReplace);
    }

    [Fact]
    public void TheRealExecutableIsLookedForByDefault()
    {
        var folder = Path.Combine(_directory, "MainframeEngine-1.0.0-linux-x64");
        Directory.CreateDirectory(folder);
        Assert.Equal(InstallKind.NotBundled, InstallLocation.Inspect(folder, "linux-x64", _ => true).Kind);
        File.WriteAllText(UpdatePlatform.ExecutablePath(folder, "linux-x64"), "exe");
        Assert.Equal(InstallKind.Replaceable, InstallLocation.Inspect(folder, "linux-x64", _ => true).Kind);
    }

    [Fact]
    public void TranslocatedAppsCannotBeReplaced()
    {
        var app = Path.Combine(_directory, "AppTranslocation", "1B2C", "d", "Mainframe Engine.app");
        var location = InstallLocation.Inspect(Path.Combine(app, "Contents", "MacOS"), "osx-arm64", _ => true, _ => true);
        Assert.Equal(InstallKind.Translocated, location.Kind);
        Assert.False(location.CanReplace);
        Assert.Contains("Applications", location.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyFoldersCannotBeReplaced()
    {
        var folder = Path.Combine(_directory, "MainframeEngine-1.0.0-linux-x64");
        Assert.Equal(InstallKind.NotWritable, InstallLocation.Inspect(folder, "linux-x64", _ => false, _ => true).Kind);
        // The root's parent must be writable too (the root is renamed inside it).
        var parent = Path.GetFullPath(_directory);
        Assert.Equal(InstallKind.NotWritable, InstallLocation.Inspect(folder, "linux-x64", d => !string.Equals(d, parent, StringComparison.Ordinal), _ => true).Kind);
        var replaceable = InstallLocation.Inspect(folder, "linux-x64", _ => true, _ => true);
        Assert.True(replaceable.CanReplace);
        Assert.Null(replaceable.Hint);
    }

    [Fact]
    public void CanWriteToProbesWithoutLeavingFiles()
    {
        Assert.True(InstallLocation.CanWriteTo(_directory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
        Assert.False(InstallLocation.CanWriteTo(Path.Combine(_directory, "missing")));
    }

    [Fact]
    public void UpdatePathsLiveUnderTheUsersMainframeFolder()
    {
        Assert.EndsWith(Path.Combine(".mainframe", "updates"), UpdatePaths.DefaultDirectory, StringComparison.Ordinal);
        Assert.Equal(Path.Combine("u", "result.json"), UpdatePaths.ResultFile("u"));
        Assert.Equal(Path.Combine("u", "update.log"), UpdatePaths.LogFile("u"));
        Assert.Equal(Path.Combine("a", "Mainframe Engine.app.old"), UpdatePaths.BackupOf(Path.Combine("a", "Mainframe Engine.app") + Path.DirectorySeparatorChar));
    }
}
