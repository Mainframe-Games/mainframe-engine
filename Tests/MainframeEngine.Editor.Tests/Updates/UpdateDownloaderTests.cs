using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace MainframeEngine.Editor.Tests.Updates;

public sealed class UpdateDownloaderTests : IDisposable
{
    private static readonly ReleaseVersion Version = new(1, 1, 0);
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-download").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Updates => Path.Combine(_directory, "updates");

    private static byte[] TarGz(params (string Name, string Text, UnixFileMode Mode)[] entries)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var (name, text, mode) in entries)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { Mode = mode, DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)) };
                tar.WriteEntry(entry);
            }
        }

        return output.ToArray();
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }

        return output.ToArray();
    }

    private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
    private const UnixFileMode Plain = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static (ReleaseInfo Release, ReleaseAsset Asset) Release(string rid, byte[] archive, string? sha256 = null, string scheme = "https")
    {
        var name = UpdatePlatform.AssetName(Version, rid);
        var asset = new ReleaseAsset(name, new Uri($"{scheme}://example.test/{name}"), archive.Length, sha256 ?? Convert.ToHexStringLower(SHA256.HashData(archive)));
        return (new ReleaseInfo(Version, "v1.1.0", "", new Uri("https://example.test/release"), [asset]), asset);
    }

    private static StubHandler Serving(byte[] archive) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });

    private async Task<StagedUpdate> Download(string rid, byte[] archive, string? sha256 = null, IProgress<double>? progress = null, CancellationToken? ct = null)
    {
        var (release, asset) = Release(rid, archive, sha256);
        using var http = new HttpClient(Serving(archive));
        return await new UpdateDownloader(http, "1.0.0").DownloadAsync(release, asset, rid, Updates, progress, ct ?? TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ALinuxArchiveIsStagedWithItsExecuteBit()
    {
        var archive = TarGz(("MainframeEngine-1.1.0-linux-x64/MainframeEngine.Editor", "exe", Executable),
                            ("MainframeEngine-1.1.0-linux-x64/Content/a.txt", "a", Plain));
        var reports = new List<double>();

        var staged = await Download("linux-x64", archive, progress: new SyncProgress(reports));

        Assert.Equal(Version, staged.Version);
        Assert.Equal(Path.Combine(Updates, "1.1.0"), staged.Folder);
        Assert.Equal(Path.Combine(Updates, "1.1.0", "app", "MainframeEngine-1.1.0-linux-x64"), staged.Root);
        Assert.True(File.Exists(staged.Executable));
        Assert.True(File.Exists(Path.Combine(staged.Root, "Content", "a.txt")));
        Assert.False(File.Exists(Path.Combine(staged.Folder, "MainframeEngine-1.1.0-linux-x64.tar.gz"))); // the archive is gone
        Assert.Equal(1.0, reports[^1]);
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(staged.Executable).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task AMacArchiveIsStagedAsABundle()
    {
        var archive = TarGz(("Mainframe Engine.app/Contents/MacOS/MainframeEngine.Editor", "exe", Executable),
                            ("Mainframe Engine.app/Contents/Info.plist", "plist", Plain));
        var staged = await Download("osx-arm64", archive);
        Assert.EndsWith("Mainframe Engine.app", staged.Root, StringComparison.Ordinal);
        Assert.True(File.Exists(staged.Executable));
    }

    [Fact]
    public async Task AWindowsZipIsStaged()
    {
        var staged = await Download("win-x64", Zip(("MainframeEngine-1.1.0-win-x64/MainframeEngine.Editor.exe", "exe")));
        Assert.True(File.Exists(staged.Executable));
    }

    [Fact]
    public async Task AChecksumMismatchIsRefusedAndCleanedUp()
    {
        var archive = TarGz(("MainframeEngine-1.1.0-linux-x64/MainframeEngine.Editor", "exe", Executable));
        var e = await Assert.ThrowsAsync<UpdateException>(() => Download("linux-x64", archive, sha256: new string('0', 64)));
        Assert.Contains("checksum", e.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Updates, "1.1.0")));
    }

    [Fact]
    public async Task AnArchiveWithoutTheEditorIsRefused()
    {
        var archive = TarGz(("something-else/readme.txt", "hi", Plain));
        var e = await Assert.ThrowsAsync<UpdateException>(() => Download("linux-x64", archive));
        Assert.Contains("MainframeEngine.Editor", e.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Updates, "1.1.0")));
    }

    [Fact]
    public async Task EntriesOutsideTheStagingFolderAreRefused()
    {
        var archive = TarGz(("../escaped.txt", "x", Plain), ("MainframeEngine-1.1.0-linux-x64/MainframeEngine.Editor", "exe", Executable));
        await Assert.ThrowsAsync<UpdateException>(() => Download("linux-x64", archive));
        Assert.False(File.Exists(Path.Combine(Updates, "1.1.0", "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(Updates, "escaped.txt")));
        Assert.False(Directory.Exists(Path.Combine(Updates, "1.1.0")));
    }

    [Fact]
    public async Task HttpErrorsAreUpdateExceptions()
    {
        var (release, asset) = Release("linux-x64", [1, 2, 3]);
        using var http = new HttpClient(StubHandler.Status(HttpStatusCode.NotFound));
        var e = await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateDownloader(http, "1.0.0").DownloadAsync(release, asset, "linux-x64", Updates, null, TestContext.Current.CancellationToken));
        Assert.Contains("404", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADroppedConnectionIsAnUpdateException()
    {
        var (release, asset) = Release("linux-x64", [1, 2, 3]);
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DroppingStream()) }));
        var e = await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateDownloader(http, "1.0.0").DownloadAsync(release, asset, "linux-x64", Updates, null, TestContext.Current.CancellationToken));
        Assert.Contains("download failed", e.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Updates, "1.1.0")));
    }

    [Fact]
    public async Task NonHttpsDownloadsAreRefused()
    {
        var archive = TarGz(("MainframeEngine-1.1.0-linux-x64/MainframeEngine.Editor", "exe", Executable));
        var (release, asset) = Release("linux-x64", archive, scheme: "http");
        var handler = Serving(archive);
        using var http = new HttpClient(handler);
        var e = await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateDownloader(http, "1.0.0").DownloadAsync(release, asset, "linux-x64", Updates, null, TestContext.Current.CancellationToken));
        Assert.Contains("HTTPS", e.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        Assert.False(Directory.Exists(Path.Combine(Updates, "1.1.0")));
    }

    [Fact]
    public async Task CancellingDeletesTheFolder()
    {
        var archive = TarGz(("MainframeEngine-1.1.0-linux-x64/MainframeEngine.Editor", "exe", Executable));
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Download("linux-x64", archive, ct: cancel.Token));
        Assert.False(Directory.Exists(Path.Combine(Updates, "1.1.0")));
    }

    [Fact]
    public async Task CancellingAfterTheDownloadFinishedStillDeletesTheFolder()
    {
        // The last progress report (1.0) comes after the body is read and before the checksum and extraction.
        var archive = TarGz(("MainframeEngine-1.1.0-linux-x64/MainframeEngine.Editor", "exe", Executable));
        using var cancel = new CancellationTokenSource();
        var reports = 0;
        var progress = new CallbackProgress(value =>
        {
            if (value >= 1.0 && ++reports == 2)
                cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Download("linux-x64", archive, progress: progress, ct: cancel.Token));
        Assert.False(Directory.Exists(Path.Combine(Updates, "1.1.0")));
    }

    [Fact]
    public async Task AnEarlierStagingOfTheSameVersionIsReplaced()
    {
        Directory.CreateDirectory(Path.Combine(Updates, "1.1.0", "app", "stale"));
        var staged = await Download("linux-x64", TarGz(("MainframeEngine-1.1.0-linux-x64/MainframeEngine.Editor", "exe", Executable)));
        Assert.False(Directory.Exists(Path.Combine(staged.Folder, "app", "stale")));
    }

    /// <summary>Yields a few bytes, then fails like a connection that drops mid-body.</summary>
    private sealed class DroppingStream : Stream
    {
        private bool _sent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_sent)
                throw new IOException("The connection was reset.");
            _sent = true;
            buffer[0] = 1;
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SyncProgress(List<double> reports) : IProgress<double>
    {
        public void Report(double value) => reports.Add(value);
    }

    private sealed class CallbackProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
