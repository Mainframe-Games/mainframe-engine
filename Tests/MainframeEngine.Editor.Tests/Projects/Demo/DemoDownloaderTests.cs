using System.Net;
using MainframeEngine.Editor.Tests.Updates;

namespace MainframeEngine.Editor.Tests.Projects.Demo;

public sealed class DemoDownloaderTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mf-demo-dl-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private byte[] ValidZip() => File.ReadAllBytes(DemoZips.Write(Path.Combine(_directory, "demo.zip"), DemoZips.ValidProject()));

    private DemoDownloadRequest Request() => new(Path.Combine(_directory, "projects"), "MainframeEngine.Demo", "/engine");

    private string Downloads => Path.Combine(_directory, "downloads");

    [Fact]
    public async Task DownloadExtractsRewritesAndReportsProgress()
    {
        Directory.CreateDirectory(Request().ParentDirectory);
        var handler = StubHandler.Bytes(ValidZip());
        var progress = new SyncProgress();
        var destination = await new DemoDownloader(new HttpClient(handler), "1.2.3", Downloads)
            .DownloadAsync(Request(), progress, TestContext.Current.CancellationToken);

        Assert.Equal(Request().Destination, destination);
        Assert.True(File.Exists(Path.Combine(destination, "project.mfproj")));
        Assert.Contains("<MainframeEnginePath>/engine</MainframeEnginePath>", File.ReadAllText(Path.Combine(destination, "Directory.Build.props")));
        Assert.Equal(DemoRelease.AssetUrl("1.2.3"), handler.Requests.Single().RequestUri);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Downloads));
        Assert.NotEmpty(progress.Values);
        Assert.Equal(1.0, progress.Values[^1]);
        Assert.Equal([Request().Destination], Directory.EnumerateFileSystemEntries(Request().ParentDirectory)); // staging folder gone
    }

    [Fact]
    public async Task CreatesMissingParentDirectory()
    {
        var destination = await new DemoDownloader(new HttpClient(StubHandler.Bytes(ValidZip())), "1.2.3", Downloads)
            .DownloadAsync(Request(), ct: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(destination, "project.mfproj")));
    }

    [Fact]
    public async Task NonEmptyDestinationFailsAndLeavesNoStagingFolder()
    {
        Directory.CreateDirectory(Request().Destination);
        File.WriteAllText(Path.Combine(Request().Destination, "keep.txt"), "x");
        await Assert.ThrowsAsync<DemoDownloadException>(() =>
            new DemoDownloader(new HttpClient(StubHandler.Bytes(ValidZip())), "1.2.3", Downloads).DownloadAsync(Request(), ct: TestContext.Current.CancellationToken));
        Assert.Equal([Request().Destination], Directory.EnumerateFileSystemEntries(Request().ParentDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Downloads));
    }

    [Fact]
    public async Task ConnectionDroppedMidBodyReportsAnInterruptedDownload()
    {
        Directory.CreateDirectory(Request().ParentDirectory);
        var body = new FailingStream(ValidZip(), afterBytes: 16);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        var error = await Assert.ThrowsAsync<DemoDownloadException>(() =>
            new DemoDownloader(new HttpClient(handler), "1.2.3", Downloads).DownloadAsync(Request(), ct: TestContext.Current.CancellationToken));
        Assert.Contains("interrupted", error.Message);
        Assert.False(Directory.Exists(Request().Destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Downloads));
    }

    [Fact]
    public async Task NotFoundSaysThereIsNoDemoForThisVersion()
    {
        Directory.CreateDirectory(Request().ParentDirectory);
        var handler = StubHandler.Status(HttpStatusCode.NotFound);
        var error = await Assert.ThrowsAsync<DemoDownloadException>(() =>
            new DemoDownloader(new HttpClient(handler), "1.2.3", Downloads).DownloadAsync(Request(), ct: TestContext.Current.CancellationToken));
        Assert.Contains("1.2.3", error.Message);
        Assert.False(Directory.Exists(Request().Destination));
    }

    [Fact]
    public async Task CancellingWhileWaitingForHeadersCleansUp()
    {
        Directory.CreateDirectory(Request().ParentDirectory);
        var handler = new StubHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DemoDownloader(new HttpClient(handler), "1.2.3", Downloads).DownloadAsync(Request(), ct: cancel.Token));
        Assert.False(Directory.Exists(Request().Destination));
        Assert.True(!Directory.Exists(Downloads) || !Directory.EnumerateFileSystemEntries(Downloads).Any());
    }

    [Fact]
    public async Task CancellingCleansUp()
    {
        Directory.CreateDirectory(Request().ParentDirectory);
        using var cancel = new CancellationTokenSource();
        var body = new SlowStream(ValidZip(), afterBytes: 16, onPause: cancel.Cancel);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DemoDownloader(new HttpClient(handler), "1.2.3", Downloads).DownloadAsync(Request(), ct: cancel.Token));
        Assert.False(Directory.Exists(Request().Destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Downloads));
    }

    [Fact]
    public async Task InvalidArchiveLeavesNothingBehind()
    {
        Directory.CreateDirectory(Request().ParentDirectory);
        var handler = StubHandler.Bytes("not a zip"u8.ToArray());
        await Assert.ThrowsAsync<DemoDownloadException>(() =>
            new DemoDownloader(new HttpClient(handler), "1.2.3", Downloads).DownloadAsync(Request(), ct: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Request().Destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Downloads));
    }

    private sealed class SyncProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    /// <summary>Returns <paramref name="afterBytes"/> bytes, then fails like a dropped connection.</summary>
    private sealed class FailingStream(byte[] data, int afterBytes) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position >= afterBytes
                ? throw new IOException("connection reset")
                : base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, afterBytes - Position)], cancellationToken);
    }

    /// <summary>Returns <paramref name="afterBytes"/> bytes, calls <paramref name="onPause"/>, then honours cancellation.</summary>
    private sealed class SlowStream(byte[] data, int afterBytes, Action onPause) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= afterBytes)
            {
                onPause();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return await base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, afterBytes - Position)], cancellationToken);
        }
    }
}
