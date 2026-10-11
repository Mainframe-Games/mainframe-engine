using System.Text.Json.Nodes;

namespace MainframeEngine.Tests.Saves;

public sealed class SaveCloudTests : SaveTestBase
{
    private static readonly SaveOptions FastRetries = new() { CloudRetryDelay = TimeSpan.FromMilliseconds(1) };

    private readonly string _otherDevice = Path.Combine(Path.GetTempPath(), "mf-save-tests", Guid.NewGuid().ToString("N"));

    // A save made on another device: its file, as the cloud would hold it.
    private byte[] RemoteSave(string slot, string name, double progress = 0)
    {
        using var other = new SaveServer(new LocalSaveStorage(_otherDevice), new SaveOptions { Device = "other-device" });
        Assert.True(other.Save(slot, Data(name), Schema, new SaveInfo(Progress: progress)).IsOk);
        return File.ReadAllBytes(Path.Combine(_otherDevice, slot + ".msave"));
    }

    private string? SyncedHash(string slot) =>
        File.Exists(PathOf(".sync.json")) ? (string?)JsonNode.Parse(File.ReadAllText(PathOf(".sync.json")))!["slots"]![slot] : null;

    private static string HashOf(byte[] file) => CloudSaveMeta.FromFile(file)!.Sha256;

    private static string LocalName(SaveServer server, string slot) => server.Load(slot, Schema).Data!.Name;

    private string[] ConflictCopies(string slot) => Directory.GetFiles(Root, slot + ".msave.conflict-*");

    // Local and cloud both hold "base" and agree (synced), then each side saves its own change.
    private (SaveServer Server, FakeCloudSaveBackend Cloud) Diverged(SaveConflictPolicy policy, double localProgress, double remoteProgress, bool remoteNewer)
    {
        var cloud = new FakeCloudSaveBackend();
        var server = CreateServer(FastRetries);
        server.Cloud = cloud;
        server.ConflictPolicy = policy;
        Assert.True(server.Save("slot1", Data("base"), Schema).IsOk);
        WaitFor(() => SyncedHash("slot1") is not null);

        server.Cloud = null; // the local change happens offline
        if (remoteNewer)
        {
            server.Save("slot1", Data("local"), Schema, new SaveInfo(Progress: localProgress));
            Thread.Sleep(10);
            cloud.Put("slot1", RemoteSave("slot1", "remote", remoteProgress));
        }
        else
        {
            cloud.Put("slot1", RemoteSave("slot1", "remote", remoteProgress));
            Thread.Sleep(10);
            server.Save("slot1", Data("local"), Schema, new SaveInfo(Progress: localProgress));
        }

        server.Cloud = cloud;
        return (server, cloud);
    }

    [Fact]
    public void ALocalWriteIsUploadedWithTheSameBytes()
    {
        var cloud = new FakeCloudSaveBackend();
        var server = CreateServer(FastRetries);
        server.Cloud = cloud;

        var result = Pump(server, server.SaveAsync("slot1", Data("x"), Schema));

        Assert.False(result.CloudTooLarge);
        WaitFor(() => SyncedHash("slot1") is not null);
        var local = File.ReadAllBytes(PathOf("slot1.msave"));
        Assert.Equal(local, cloud.File("slot1"));
        Assert.Equal(HashOf(local), SyncedHash("slot1"));
    }

    [Fact]
    public void FailedUploadsAreRetried()
    {
        var cloud = new FakeCloudSaveBackend { FailNextUploads = 2 };
        var server = CreateServer(FastRetries);
        server.Cloud = cloud;

        server.Save("slot1", Data("x"), Schema);

        WaitFor(() => cloud.Has("slot1"));
        Assert.Equal(1, cloud.Uploads);
    }

    [Fact]
    public void ASaveOverTheCloudLimitStaysLocal()
    {
        var cloud = new FakeCloudSaveBackend { MaxBytes = 32 };
        var server = CreateServer(FastRetries);
        server.Cloud = cloud;

        var result = Pump(server, server.SaveAsync("slot1", Data("x"), Schema));

        Assert.True(result.IsOk);
        Assert.True(result.CloudTooLarge);
        Assert.True(File.Exists(PathOf("slot1.msave")));
        Pump(server, server.SyncAsync(TestContext.Current.CancellationToken));
        Assert.False(cloud.Has("slot1"));
    }

    [Fact]
    public void SyncDownloadsNewSlotsAndUploadsLocalChanges()
    {
        var cloud = new FakeCloudSaveBackend();
        var server = CreateServer(FastRetries);
        server.Save("mine", Data("local only"), Schema); // saved before the cloud was set
        var remote = RemoteSave("theirs", "from the other device");
        cloud.Put("theirs", remote, [9, 9]);
        server.Cloud = cloud;

        Pump(server, server.SyncAsync(TestContext.Current.CancellationToken));

        Assert.Equal(remote, File.ReadAllBytes(PathOf("theirs.msave")));
        Assert.Equal([9, 9], File.ReadAllBytes(PathOf("theirs.png")));
        Assert.Equal(File.ReadAllBytes(PathOf("mine.msave")), cloud.File("mine"));
        Assert.Equal(HashOf(remote), SyncedHash("theirs"));
        Assert.NotNull(SyncedHash("mine"));
        Assert.Equal(["mine", "theirs"], server.ListSlots().Select(s => s.Slot).Order(StringComparer.Ordinal));
        Assert.Empty(ConflictCopies("theirs"));
    }

    [Fact]
    public void ARemoteOnlyChangeReplacesTheLocalCopy()
    {
        var cloud = new FakeCloudSaveBackend();
        var server = CreateServer(FastRetries);
        server.Cloud = cloud;
        server.Save("slot1", Data("base"), Schema);
        WaitFor(() => SyncedHash("slot1") is not null);
        var remote = RemoteSave("slot1", "newer elsewhere");
        cloud.Put("slot1", remote);

        Pump(server, server.SyncAsync(TestContext.Current.CancellationToken));

        Assert.Equal("newer elsewhere", LocalName(server, "slot1"));
        Assert.Empty(ConflictCopies("slot1")); // not a conflict: only the cloud changed
        Assert.True(File.Exists(PathOf("slot1.msave.bak1")));
    }

    [Fact]
    public void ASlotDeletedLocallyIsNotBroughtBack()
    {
        var cloud = new FakeCloudSaveBackend();
        var server = CreateServer(FastRetries);
        server.Cloud = cloud;
        server.Save("slot1", Data("x"), Schema);
        WaitFor(() => SyncedHash("slot1") is not null);

        server.Delete("slot1");
        Pump(server, server.SyncAsync(TestContext.Current.CancellationToken));

        Assert.False(server.Exists("slot1"));
    }

    [Fact]
    public void MostRecentKeepsTheNewerCopy()
    {
        var (server, cloud) = Diverged(SaveConflictPolicy.MostRecent, localProgress: 0.9, remoteProgress: 0.1, remoteNewer: true);
        var local = File.ReadAllBytes(PathOf("slot1.msave"));

        Pump(server, server.SyncAsync(TestContext.Current.CancellationToken));

        Assert.Equal("remote", LocalName(server, "slot1"));
        Assert.Equal(local, File.ReadAllBytes(Assert.Single(ConflictCopies("slot1")))); // the loser is kept
        Assert.Equal(HashOf(cloud.File("slot1")), SyncedHash("slot1"));
    }

    [Fact]
    public void MostRecentKeepsALocalCopyThatIsNewer()
    {
        var (server, cloud) = Diverged(SaveConflictPolicy.MostRecent, localProgress: 0.1, remoteProgress: 0.9, remoteNewer: false);
        var remote = cloud.File("slot1");

        Pump(server, server.SyncAsync(TestContext.Current.CancellationToken));

        Assert.Equal("local", LocalName(server, "slot1"));
        Assert.Equal(File.ReadAllBytes(PathOf("slot1.msave")), cloud.File("slot1")); // the cloud gets the winner
        Assert.Equal(remote, File.ReadAllBytes(Assert.Single(ConflictCopies("slot1"))));
    }

    [Fact]
    public void HighestProgressKeepsTheFurtherCopyEvenWhenOlder()
    {
        var (server, cloud) = Diverged(SaveConflictPolicy.HighestProgress, localProgress: 0.9, remoteProgress: 0.5, remoteNewer: true);
        var remote = cloud.File("slot1");

        Pump(server, server.SyncAsync(TestContext.Current.CancellationToken));

        Assert.Equal("local", LocalName(server, "slot1"));
        WaitFor(() => cloud.File("slot1").AsSpan().SequenceEqual(File.ReadAllBytes(PathOf("slot1.msave"))));
        Assert.Equal(remote, File.ReadAllBytes(Assert.Single(ConflictCopies("slot1"))));
        Assert.Equal(HashOf(cloud.File("slot1")), SyncedHash("slot1"));
    }

    [Fact]
    public void ManualRaisesConflictDetectedAndWaitsForTheGame()
    {
        var (server, cloud) = Diverged(SaveConflictPolicy.Manual, localProgress: 0.3, remoteProgress: 0.6, remoteNewer: true);
        var local = File.ReadAllBytes(PathOf("slot1.msave"));
        var remote = cloud.File("slot1");
        var conflicts = new List<(SaveSlotInfo Local, SaveSlotInfo Remote, int Thread)>();
        server.ConflictDetected += (l, r) => conflicts.Add((l, r, Environment.CurrentManagedThreadId));

        Pump(server, server.SyncAsync(TestContext.Current.CancellationToken));

        var conflict = Assert.Single(conflicts);
        Assert.Equal(Environment.CurrentManagedThreadId, conflict.Thread); // raised by Process, on the main thread
        Assert.Equal(0.3, conflict.Local.Progress);
        Assert.Equal(0.6, conflict.Remote.Progress);
        Assert.Equal("other-device", conflict.Remote.Device);
        Assert.Equal(local, File.ReadAllBytes(PathOf("slot1.msave"))); // nothing changes until the game decides
        Assert.Empty(ConflictCopies("slot1"));

        Assert.True(Pump(server, server.ResolveConflict("slot1", keepLocal: false)));

        Assert.Equal(remote, File.ReadAllBytes(PathOf("slot1.msave")));
        Assert.Equal(local, File.ReadAllBytes(Assert.Single(ConflictCopies("slot1"))));
        Assert.Equal(HashOf(remote), SyncedHash("slot1"));
        Assert.False(Pump(server, server.ResolveConflict("slot1", keepLocal: true))); // already settled
    }

    [Fact]
    public void ManualKeepLocalUploadsTheLocalCopy()
    {
        var (server, cloud) = Diverged(SaveConflictPolicy.Manual, localProgress: 0, remoteProgress: 0, remoteNewer: true);
        var remote = cloud.File("slot1");
        Pump(server, server.SyncAsync(TestContext.Current.CancellationToken));

        Assert.True(Pump(server, server.ResolveConflict("slot1", keepLocal: true)));

        Assert.Equal("local", LocalName(server, "slot1"));
        Assert.Equal(File.ReadAllBytes(PathOf("slot1.msave")), cloud.File("slot1"));
        Assert.Equal(remote, File.ReadAllBytes(Assert.Single(ConflictCopies("slot1"))));
    }

    [Fact]
    public void CloudMetaReadsTheHeader()
    {
        var file = RemoteSave("slot1", "x", progress: 0.25);

        var meta = CloudSaveMeta.FromFile(file)!;

        Assert.Equal("slot1", meta.Slot);
        Assert.Equal(0.25, meta.Progress);
        Assert.Equal("other-device", meta.Device);
        Assert.Equal(file.Length, meta.SizeBytes);
        Assert.Equal(64, meta.Sha256.Length);
        Assert.Null(CloudSaveMeta.FromFile("not a save\n"u8));
    }

    [Fact]
    public void SyncWithoutACloudCompletesAtOnce()
    {
        var server = CreateServer();

        Assert.True(server.SyncAsync(TestContext.Current.CancellationToken).IsCompletedSuccessfully);
    }

    protected override void Cleanup()
    {
        try
        {
            Directory.Delete(_otherDevice, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
