using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Media.Export;

namespace MainframeEngine.Editor.Tests.Video;

public sealed class VideoExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("video-export-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void TheDefaultIsNextToTheRecordingAndNeverOverwrites()
    {
        var taken = new HashSet<string> { Path.Combine("/r", "run-cut.mp4"), Path.Combine("/r", "run-cut-2.mp4") };
        Assert.Equal(Path.Combine("/r", "run-cut-3.mp4"), VideoExport.DefaultPath(Path.Combine("/r", "run.mp4"), taken.Contains));
        Assert.Equal(Path.Combine("/r", "run-cut.mp4"), VideoExport.DefaultPath(Path.Combine("/r", "run.mp4"), _ => false));
    }

    [Fact]
    public void TheDefaultSkipsFilesThatReallyExist()
    {
        var source = Path.Combine(_dir, "run.mkv");
        File.WriteAllBytes(Path.Combine(_dir, "run-cut.mp4"), [0]);
        Assert.Equal(Path.Combine(_dir, "run-cut-2.mp4"), VideoExport.DefaultPath(source, File.Exists));
    }

    [Fact]
    public void TheRequestCarriesTheModeAndTheSourceSizeForTheEncoder()
    {
        var doc = VideoDocument.FromProject(VideoDocumentTests.Project("/r/run.mp4"), null, null, []);
        doc.Editor.CreateSection(10, 20, anchor: 10);
        var request = VideoExport.Request(doc, "/r/run-cut.mp4", ExportMode.Fast);
        Assert.Equal(ExportMode.Fast, request.Mode);
        Assert.Equal((1920, 1080, 60.0), (request.Width, request.Height, request.FrameRate));
        Assert.Equal(3, request.Segments.Count);
        Assert.Equal("/r/run.mp4", request.SourcePath);
        Assert.Equal(60, request.DurationSeconds);
    }

    [Fact]
    public void TheRequestIsASnapshotLaterEditsCannotChange()
    {
        var doc = VideoDocument.FromProject(VideoDocumentTests.Project("/r/run.mp4"), null, null, []);
        doc.Editor.CreateSection(10, 20, anchor: 10);
        var request = VideoExport.Request(doc, "/r/run-cut.mp4", ExportMode.Precise);
        var before = request.Segments.ToArray();

        doc.Editor.Toggle(0);
        doc.Editor.CreateSection(30, 40, anchor: 30);

        Assert.NotEqual(before.Length, doc.Segments.Count); // the document did change
        Assert.Equal(before, request.Segments.ToArray());
        Assert.Equal(3, request.Segments.Count);
    }

    [Theory]
    [InlineData("/r/a.mp4", true)]
    [InlineData("/r/a.webm", false)]
    [InlineData("/r/a.WEBM", false)]
    [InlineData("/r/a.mkv", true)]
    public void FastExportIsNotOfferedForWebm(string path, bool allowed) => Assert.Equal(allowed, VideoExport.FastAllowed(path));

    [Fact]
    public void FastIntoWebmIsRefused()
    {
        var doc = VideoDocument.FromProject(VideoDocumentTests.Project("/r/run.mp4"), null, null, []);
        Assert.Throws<InvalidOperationException>(() => VideoExport.Request(doc, "/r/out.webm", ExportMode.Fast));
        Assert.Equal(ExportMode.Precise, VideoExport.Request(doc, "/r/out.webm", ExportMode.Precise).Mode);
    }

    [Fact]
    public void TheRecordingItselfIsNeverAnOutput()
    {
        var doc = VideoDocument.FromProject(VideoDocumentTests.Project("/r/run.mp4"), null, null, []);
        var ex = Assert.Throws<InvalidOperationException>(() => VideoExport.Request(doc, "/r/run.mp4", ExportMode.Precise));
        Assert.Contains("recording itself", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(VideoExport.Problem("/r/run.mp4", "/r/sub/../run.mp4")); // the same file by another spelling
        Assert.Null(VideoExport.Problem("/r/run.mp4", "/r/run-cut.mp4"));
    }

    [Fact]
    public void TheRecordingIsTheSameFileInAnyCaseOnWindowsAndMacOnly()
    {
        var same = VideoPaths.SameFile("/r/Run.mp4", "/r/run.MP4");
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), same);
    }

    [Fact]
    public void ASymbolicLinkToTheRecordingIsTheRecording()
    {
        if (OperatingSystem.IsWindows())
            return; // creating links needs a privilege there
        var video = Path.Combine(_dir, "run.mp4");
        File.WriteAllBytes(video, [0]);
        var link = Path.Combine(_dir, "alias.mp4");
        File.CreateSymbolicLink(link, video);
        Assert.True(VideoPaths.SameFile(link, video));
        Assert.NotNull(VideoExport.Problem(video, link));
    }

    [Fact]
    public void ASymbolicLinkedFolderInThePathIsTheSameFolder()
    {
        if (OperatingSystem.IsWindows())
            return; // creating links needs a privilege there
        var real = Directory.CreateDirectory(Path.Combine(_dir, "real")).FullName;
        var link = Path.Combine(_dir, "link");
        Directory.CreateSymbolicLink(link, real);
        var source = Path.Combine(real, "run.mp4");
        File.WriteAllBytes(source, [0]);

        Assert.True(VideoPaths.SameFile(source, Path.Combine(link, "run.mp4")));
        Assert.NotNull(VideoExport.Problem(source, Path.Combine(link, "run.mp4")));
        Assert.False(VideoPaths.SameFile(source, Path.Combine(link, "other.mp4")));
        Assert.Null(VideoExport.Problem(source, Path.Combine(link, "other.mp4")));
    }

    [Fact]
    public void ANotYetExistingOutputUnderALinkedFolderIsJudgedByItsRealFolder()
    {
        if (OperatingSystem.IsWindows())
            return;
        var real = Directory.CreateDirectory(Path.Combine(_dir, "real")).FullName;
        var link = Path.Combine(_dir, "link");
        Directory.CreateSymbolicLink(link, real);
        // Neither the file nor the sub-folder exist: the deepest existing ancestor is resolved, the rest is appended.
        Assert.Equal(Path.Combine(VideoPaths.Canonical(real), "new", "x.mp4"), VideoPaths.Canonical(Path.Combine(link, "new", "x.mp4")));
        Assert.True(VideoPaths.SameFile(Path.Combine(real, "new", "x.mp4"), Path.Combine(link, "new", "x.mp4")));
    }

    [Fact]
    public void ALinkToALinkedFolderAndARelativeLinkAreFollowed()
    {
        if (OperatingSystem.IsWindows())
            return;
        var real = Directory.CreateDirectory(Path.Combine(_dir, "real")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(_dir, "hop1"), "real"); // relative target
        Directory.CreateSymbolicLink(Path.Combine(_dir, "hop2"), Path.Combine(_dir, "hop1"));
        Assert.True(VideoPaths.SameFile(Path.Combine(real, "run.mp4"), Path.Combine(_dir, "hop2", "run.mp4")));
    }

    [Fact]
    public void ALinkLoopDoesNotHangAndIsJustAPath()
    {
        if (OperatingSystem.IsWindows())
            return;
        File.CreateSymbolicLink(Path.Combine(_dir, "a"), Path.Combine(_dir, "b"));
        File.CreateSymbolicLink(Path.Combine(_dir, "b"), Path.Combine(_dir, "a"));
        Assert.False(VideoPaths.SameFile(Path.Combine(_dir, "a", "x.mp4"), Path.Combine(_dir, "other.mp4")));
    }

    [Fact]
    public void ATrailingDotDoesNotDoubleTheExtension()
    {
        var source = Path.Combine(_dir, "run.mp4");
        Assert.Equal(Path.Combine(_dir, "mycut.mp4"), VideoExport.ResolveOutput("mycut.", source));
        Assert.Equal(Path.Combine(_dir, "mycut.mp4"), VideoExport.ResolveOutput("mycut...", source));
    }

    [Theory]
    [InlineData("/r/out.avi")]
    [InlineData("/r/out.txt")]
    public void AnUnsupportedExtensionIsRefusedWithTheSupportedOnes(string path)
    {
        var problem = VideoExport.Problem("/r/run.mp4", path);
        Assert.NotNull(problem);
        Assert.Contains(".mp4, .mov, .mkv, .webm", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("out.MP4")]
    [InlineData("out.mov")]
    [InlineData("out.mkv")]
    [InlineData("out.webm")]
    public void EverySupportedExtensionIsAccepted(string name) => Assert.Null(VideoExport.Problem("/r/run.mp4", Path.Combine("/r", name)));

    [Fact]
    public void AFolderAndAnEmptyPathAreRefused()
    {
        Assert.Contains("folder", VideoExport.Problem("/r/run.mp4", _dir), StringComparison.Ordinal);
        Assert.NotNull(VideoExport.Problem("/r/run.mp4", "  "));
    }

    [Fact]
    public void ANameWithoutAnExtensionGetsMp4AndARelativeOneResolvesBesideTheRecording()
    {
        var source = Path.Combine(_dir, "run.mp4");
        Assert.Equal(Path.Combine(_dir, "out.mp4"), VideoExport.ResolveOutput("out", source));
        Assert.Equal(Path.Combine(_dir, "out.mkv"), VideoExport.ResolveOutput("  out.mkv ", source));
        Assert.Equal(Path.Combine(_dir, "sub", "out.mov"), VideoExport.ResolveOutput(Path.Combine("sub", "out.mov"), source));
        Assert.Equal("", VideoExport.ResolveOutput("   ", source));
    }

    [Fact]
    public void NothingKeptIsRefused()
    {
        var doc = VideoDocument.FromProject(VideoDocumentTests.Project("/r/run.mp4"), null, null, []);
        doc.Editor.Toggle(0);
        Assert.Equal(0, VideoExport.KeptSeconds(doc));
        var ex = Assert.Throws<InvalidOperationException>(() => VideoExport.Request(doc, "/r/out.mp4", ExportMode.Precise));
        Assert.Equal(VideoExport.NothingToExport, ex.Message);
    }

    [Fact]
    public void TheSummaryCountsKeptTimeAndCuts()
    {
        var doc = VideoDocument.FromProject(VideoDocumentTests.Project("/r/run.mp4", duration: 360), null, null, []);
        Assert.Equal("Kept 6:00.0 of 6:00.0 · 0 cuts", VideoExport.Summary(doc));
        doc.Editor.CreateSection(10, 20, anchor: 10);
        Assert.Equal("Kept 5:50.0 of 6:00.0 · 1 cut", VideoExport.Summary(doc));
        doc.Editor.CreateSection(30, 40, anchor: 30);
        Assert.Equal("Kept 5:40.0 of 6:00.0 · 2 cuts", VideoExport.Summary(doc));
    }
}
