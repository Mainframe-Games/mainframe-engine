using System.IO.Compression;

namespace MainframeEngine.Editor.Tests.Projects.Demo;

public sealed class DemoArchiveTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-demo-zip").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Work() => Directory.CreateDirectory(Path.Combine(_directory, "w" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void ValidZipExtractsToItsTopFolder()
    {
        var zip = DemoZips.Write(Path.Combine(_directory, "ok.zip"), DemoZips.ValidProject());
        var work = Work();
        var root = DemoArchive.ExtractAndValidate(zip, work);
        Assert.Equal(Path.Combine(work, "MainframeEngine.Demo"), root);
        Assert.True(File.Exists(Path.Combine(root, "project.mfproj")));
    }

    [Fact]
    public void EntriesEscapingTheFolderAreRefused()
    {
        var files = DemoZips.ValidProject();
        files["MainframeEngine.Demo/../../evil.txt"] = "x";
        var zip = DemoZips.Write(Path.Combine(_directory, "slip.zip"), files);
        Assert.Throws<DemoDownloadException>(() => DemoArchive.ExtractAndValidate(zip, Work()));
        Assert.False(File.Exists(Path.Combine(_directory, "evil.txt")));
    }

    [Fact]
    public void AbsoluteEntriesAreRefused()
    {
        var files = DemoZips.ValidProject();
        files["/tmp/evil.txt"] = "x";
        var zip = DemoZips.Write(Path.Combine(_directory, "abs.zip"), files);
        Assert.Throws<DemoDownloadException>(() => DemoArchive.ExtractAndValidate(zip, Work()));
    }

    [Fact]
    public void SymlinkEntriesAreRefused()
    {
        var zip = DemoZips.Write(Path.Combine(_directory, "link.zip"), DemoZips.ValidProject(), z =>
        {
            var entry = z.CreateEntry("MainframeEngine.Demo/link");
            entry.ExternalAttributes = unchecked((int)(0xA1FF_0000u)); // S_IFLNK | 0777
            using var w = new StreamWriter(entry.Open());
            w.Write("/etc/passwd");
        });
        Assert.Throws<DemoDownloadException>(() => DemoArchive.ExtractAndValidate(zip, Work()));
    }

    [Fact]
    public void MalformedEntryNamesAreADownloadError()
    {
        var zip = DemoZips.Write(Path.Combine(_directory, "nul.zip"), DemoZips.ValidProject(), z =>
            z.CreateEntry("MainframeEngine.Demo/bad\0name.txt"));
        Assert.Throws<DemoDownloadException>(() => DemoArchive.ExtractAndValidate(zip, Work()));
    }

    [Fact]
    public void OversizedArchivesAreRefused()
    {
        var zip = DemoZips.Write(Path.Combine(_directory, "big.zip"), DemoZips.ValidProject());
        Assert.Throws<DemoDownloadException>(() => DemoArchive.ExtractAndValidate(zip, Work(), maxBytes: 10));
    }

    [Fact]
    public void TwoTopLevelFoldersAreRefused()
    {
        var files = DemoZips.ValidProject();
        files["Other/readme.txt"] = "x";
        var zip = DemoZips.Write(Path.Combine(_directory, "two.zip"), files);
        Assert.Throws<DemoDownloadException>(() => DemoArchive.ExtractAndValidate(zip, Work()));
    }

    [Fact]
    public void DotDotEntriesCannotHideASecondTopLevelFolder()
    {
        var files = DemoZips.ValidProject();
        files["MainframeEngine.Demo/../Other/readme.txt"] = "x";
        var zip = DemoZips.Write(Path.Combine(_directory, "dotdot.zip"), files);
        Assert.Throws<DemoDownloadException>(() => DemoArchive.ExtractAndValidate(zip, Work()));
    }

    [Theory]
    [InlineData("MainframeEngine.Demo/project.mfproj")]
    [InlineData("MainframeEngine.Demo/Demo.Launcher/Demo.Launcher.csproj")]
    [InlineData("MainframeEngine.Demo/Content/Scenes/basic_3d.mscene")]
    public void MissingProjectPartsAreRefused(string missing)
    {
        var files = DemoZips.ValidProject();
        files.Remove(missing);
        var zip = DemoZips.Write(Path.Combine(_directory, "missing.zip"), files);
        Assert.Throws<DemoDownloadException>(() => DemoArchive.ExtractAndValidate(zip, Work()));
    }

    [Fact]
    public void CorruptZipIsADownloadError()
    {
        var path = Path.Combine(_directory, "corrupt.zip");
        File.WriteAllText(path, "not a zip");
        Assert.Throws<DemoDownloadException>(() => DemoArchive.ExtractAndValidate(path, Work()));
    }
}
