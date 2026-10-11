using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace MainframeEngine.Tests.Saves;

public sealed class SaveServerTests : SaveTestBase
{
    private static string FirstLine(byte[] file) => Encoding.UTF8.GetString(file, 0, Array.IndexOf(file, (byte)'\n'));

    [Fact]
    public void SaveAsyncRoundTripsThroughTheWorker()
    {
        using var server = CreateServer();
        server.PlayTime = TimeSpan.FromSeconds(90.5);

        var result = Pump(server, server.SaveAsync("slot1", Data("first", 42), Schema, new SaveInfo("Level 2 — the caves", 0.4)));

        Assert.Equal(SaveStatus.Ok, result.Status);
        Assert.True(result.IsOk);
        Assert.Equal("slot1", result.Slot);
        Assert.False(result.CloudTooLarge);
        var info = result.Info!;
        Assert.Equal("Level 2 — the caves", info.Description);
        Assert.Equal(0.4, info.Progress);
        Assert.Equal(90.5, info.PlayTime.TotalSeconds, 3);
        Assert.Equal("1.2.0", info.GameVersion);
        Assert.Equal(Environment.MachineName, info.Device);
        Assert.Equal(1, info.Format);
        Assert.Equal(1, info.Version);
        Assert.True(info.IsReadable);
        Assert.Equal(new FileInfo(PathOf("slot1.msave")).Length, info.SizeBytes);
        Assert.False(File.Exists(PathOf("slot1.msave.tmp")));

        server.PlayTime = TimeSpan.Zero;
        var load = server.Load("slot1", Schema);
        Assert.Equal(LoadStatus.Ok, load.Status);
        Assert.Equal("first", load.Data!.Name);
        Assert.Equal(42, load.Data.Score);
        Assert.Equal([1, 2, 3], load.Data.Items);
        Assert.Equal(90.5, server.PlayTime.TotalSeconds, 3); // a load sets the play time from the header
    }

    [Fact]
    public void TheFileIsACompactHeaderLineThenThePayload()
    {
        using var server = CreateServer();
        server.Save("slot1", Data("first"), Schema, new SaveInfo("two\nlines"));

        var file = File.ReadAllBytes(PathOf("slot1.msave"));
        var line = FirstLine(file);
        var header = JsonNode.Parse(line)!.AsObject();
        Assert.Equal("mainframe-save", (string?)header["kind"]);
        Assert.Equal(1, (int)header["format"]!);
        Assert.Equal("slot1", (string?)header["slot"]);
        Assert.Equal(1, (int)header["version"]!);
        Assert.EndsWith("Z", (string?)header["savedUtc"], StringComparison.Ordinal);
        Assert.Equal("two\nlines", (string?)header["description"]); // escaped: the header stays one line
        Assert.Equal("Test Game", (string?)header["game"]);
        Assert.Equal("1.2.0", (string?)header["gameVersion"]);
        Assert.Equal(EngineInfo.Version, (string?)header["engine"]);
        Assert.Equal("none", (string?)header["compression"]);
        Assert.Null(header["thumbnail"]);

        var payload = file.AsSpan(line.Length + 1);
        Assert.Equal(payload.Length, (long)header["payloadBytes"]!);
        Assert.Equal(SaveFile.Hash(payload), (string?)header["sha256"]);
        Assert.Equal("first", (string?)JsonNode.Parse(payload)!["name"]); // plain JSON, readable in any editor
    }

    [Fact]
    public void GzipPayloadsRoundTrip()
    {
        using var server = CreateServer(new SaveOptions { Compression = SaveCompression.Gzip });
        var data = new TestSave(new string('x', 5000), 7, [.. Enumerable.Range(0, 100)]);

        Assert.True(Pump(server, server.SaveAsync("zipped", data, Schema)).IsOk);

        var file = File.ReadAllBytes(PathOf("zipped.msave"));
        var line = FirstLine(file);
        Assert.Equal("gzip", (string?)JsonNode.Parse(line)!["compression"]);
        var stored = file.AsSpan(line.Length + 1);
        Assert.Equal(0x1f, stored[0]); // the gzip magic
        Assert.Equal(0x8b, stored[1]);
        Assert.True(stored.Length < 2000);
        Assert.Equal(SaveFile.Hash(stored), (string?)JsonNode.Parse(line)!["sha256"]); // over the stored bytes

        var load = server.Load("zipped", Schema);
        Assert.Equal(LoadStatus.Ok, load.Status);
        Assert.Equal(data.Name, load.Data!.Name);
        Assert.Equal(data.Items, load.Data.Items);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Slot1")]
    [InlineData("a b")]
    [InlineData("../evil")]
    [InlineData("slot.1")]
    [InlineData("slot/1")]
    [InlineData("é")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65
    public void InvalidSlotNamesThrow(string slot)
    {
        using var server = CreateServer();

        Assert.False(SaveServer.IsValidSlotName(slot));
        Assert.Throws<ArgumentException>(() => { _ = server.SaveAsync(slot, Data("x"), Schema); });
        Assert.Throws<ArgumentException>(() => server.Save(slot, Data("x"), Schema));
        Assert.Throws<ArgumentException>(() => server.Load(slot, Schema));
        Assert.Throws<ArgumentException>(() => { _ = server.LoadAsync(slot, Schema); });
        Assert.Throws<ArgumentException>(() => server.Exists(slot));
        Assert.Throws<ArgumentException>(() => server.Delete(slot));
        Assert.Empty(Directory.GetFiles(Root));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("slot-1_autosave")]
    [InlineData("0123456789abcdefghijklmnopqrstuvwxyz_-0123456789abcdefghijklmnop")] // 64
    public void ValidSlotNamesSave(string slot)
    {
        using var server = CreateServer();

        Assert.True(SaveServer.IsValidSlotName(slot));
        Assert.True(server.Save(slot, Data("x"), Schema).IsOk);
        Assert.True(server.Exists(slot));
    }

    [Fact]
    public void LongDescriptionsAndNonFiniteProgressThrow()
    {
        using var server = CreateServer();

        Assert.Throws<ArgumentException>(() => server.Save("a", Data("x"), Schema, new SaveInfo(new string('d', SaveServer.MaxDescriptionLength + 1))));
        Assert.Throws<ArgumentException>(() => server.Save("a", Data("x"), Schema, new SaveInfo(Progress: double.NaN)));
    }

    [Fact]
    public void ListingReadsHeadersOnlyEvenForA50MbPayload()
    {
        var storage = new InstrumentedStorage(new LocalSaveStorage(Root));
        using (var writer = CreateServer(new SaveOptions { MaxBytes = 64L << 20 }))
            Assert.True(writer.Save("huge", new BigSave(new string('a', 50_000_000)), new SaveSchema<BigSave>(SaveTestJson.Default.BigSave, 1)).IsOk);
        Assert.True(new FileInfo(PathOf("huge.msave")).Length > 50_000_000);

        // Each listing is a fresh server (an empty cache); the fastest of a few runs rules out a cold disk or a busy machine.
        var fastest = TimeSpan.MaxValue;
        for (var run = 0; run < 7; run++)
        {
            var server = CreateServer(storage: storage);
            var before = storage.BytesRead;
            var watch = Stopwatch.StartNew();
            var slots = server.ListSlots();
            watch.Stop();
            fastest = watch.Elapsed < fastest ? watch.Elapsed : fastest;

            var slot = Assert.Single(slots);
            Assert.Equal("huge", slot.Slot);
            Assert.True(slot.IsReadable);
            Assert.True(slot.SizeBytes > 50_000_000);
            Assert.True(storage.BytesRead - before <= 4096, $"listing read {storage.BytesRead - before} bytes"); // the header's first read only
        }

        Assert.True(fastest < TimeSpan.FromMilliseconds(5), $"listing took {fastest.TotalMilliseconds:0.00} ms");
    }

    [Fact]
    public void ListSlotsIsNewestFirstAndCachedUntilTheNextWrite()
    {
        using var server = CreateServer();
        server.Save("older", Data("1"), Schema);
        Thread.Sleep(5);
        server.Save("newer", Data("2"), Schema);

        var first = server.ListSlots();
        Assert.Equal(["newer", "older"], first.Select(s => s.Slot));
        Assert.Same(first, server.ListSlots());

        File.Copy(PathOf("older.msave"), PathOf("sneaky.msave")); // behind the server's back: not seen until a write
        Assert.Same(first, server.ListSlots());

        Thread.Sleep(5); // savedUtc has millisecond precision: a tie would sort "newer" first (by name)
        Pump(server, server.SaveAsync("newest", Data("3"), Schema));
        var second = server.ListSlots();
        Assert.NotSame(first, second);
        Assert.Equal(4, second.Count);
        Assert.Equal("newest", second[0].Slot);
    }

    [Fact]
    public void DamagedFilesAreListedAsUnreadable()
    {
        using var server = CreateServer();
        server.Save("good", Data("1"), Schema);
        File.WriteAllText(PathOf("bad.msave"), "not a save");
        File.WriteAllText(PathOf("notes.txt"), "ignored");
        File.WriteAllText(PathOf("Bad Name.msave"), "ignored");

        var slots = server.ListSlots();

        Assert.Equal(2, slots.Count);
        Assert.True(slots.Single(s => s.Slot == "good").IsReadable);
        Assert.False(slots.Single(s => s.Slot == "bad").IsReadable);
    }

    public enum Damage { Truncated, FlippedByte, BadJson, FailedMigration }

    [Theory]
    [InlineData(Damage.Truncated)]
    [InlineData(Damage.FlippedByte)]
    [InlineData(Damage.BadJson)]
    [InlineData(Damage.FailedMigration)]
    public void ADamagedSlotIsQuarantinedAndRecoveredFromTheBackup(Damage damage)
    {
        var v2 = new SaveSchema<TestSave>(SaveTestJson.Default.TestSave, 2,
            new JsonMigration(1, static root => throw new InvalidOperationException("this migration is broken")));
        using var server = CreateServer();
        Assert.True(server.Save("slot1", Data("first"), v2).IsOk);
        Thread.Sleep(5);
        Assert.True(server.Save("slot1", Data("second"), v2).IsOk);
        var main = PathOf("slot1.msave");
        var bak1 = File.ReadAllBytes(PathOf("slot1.msave.bak1"));

        switch (damage)
        {
            case Damage.Truncated:
                var whole = File.ReadAllBytes(main);
                File.WriteAllBytes(main, whole[..(whole.Length - 10)]);
                break;
            case Damage.FlippedByte:
                var bytes = File.ReadAllBytes(main);
                bytes[^5] ^= 0x20;
                File.WriteAllBytes(main, bytes);
                break;
            case Damage.BadJson:
                var junk = "{\"name\": \"half"u8.ToArray(); // valid checksum, invalid JSON
                var header = new SaveHeader { Kind = SaveFile.Kind, Format = 1, Slot = "slot1", Version = 2, SavedUtc = DateTimeOffset.UtcNow, PayloadBytes = junk.Length, Sha256 = SaveFile.Hash(junk) };
                File.WriteAllBytes(main, SaveFile.Build(header, junk));
                break;
            case Damage.FailedMigration:
                Assert.True(server.Save("slot1", new OldSave("old"), new SaveSchema<OldSave>(SaveTestJson.Default.OldSave, 1)).IsOk); // v1 payload; bak1 is "second"
                bak1 = File.ReadAllBytes(PathOf("slot1.msave.bak1"));
                break;
        }

        var damaged = File.ReadAllBytes(main);
        var load = server.Load("slot1", v2);

        Assert.Equal(LoadStatus.RecoveredFromBackup, load.Status);
        Assert.True(load.IsOk);
        Assert.Equal(damage == Damage.FailedMigration ? "second" : "first", load.Data!.Name);
        Assert.NotNull(load.Error);
        Assert.Equal(load.Info!.SavedUtc, server.ListSlots().Single().SavedUtc);
        var corrupt = Assert.Single(Directory.GetFiles(Root, "slot1.msave.corrupt-*"));
        Assert.Equal(damaged, File.ReadAllBytes(corrupt)); // kept for support, never deleted
        Assert.Equal(bak1, File.ReadAllBytes(main));       // the backup is the slot again
        Assert.Equal(LoadStatus.Ok, server.Load("slot1", v2).Status);
    }

    [Fact]
    public void AnInterruptedFirstSaveIsRecoveredFromItsTempFile()
    {
        using var server = CreateServer();
        server.Save("slot1", Data("first"), Schema);
        File.Move(PathOf("slot1.msave"), PathOf("slot1.msave.tmp")); // the crash hit between the flush and the rename

        Assert.True(server.Exists("slot1"));
        var load = server.Load("slot1", Schema);

        Assert.Equal(LoadStatus.RecoveredFromBackup, load.Status);
        Assert.Equal("first", load.Data!.Name);
        Assert.True(File.Exists(PathOf("slot1.msave")));
        Assert.Empty(Directory.GetFiles(Root, "*.corrupt-*")); // nothing was damaged
    }

    [Fact]
    public void MissingAndHopelessSlots()
    {
        using var server = CreateServer(new SaveOptions { Backups = 1 });

        var missing = server.Load("nothing", Schema);
        Assert.Equal(LoadStatus.NotFound, missing.Status);
        Assert.False(server.Exists("nothing"));

        File.WriteAllText(PathOf("broken.msave"), "garbage\n");
        File.WriteAllText(PathOf("broken.msave.bak1"), "{}\n");
        var broken = server.Load("broken", Schema);
        Assert.Equal(LoadStatus.Corrupt, broken.Status);
        Assert.False(broken.IsOk);
        Assert.Contains("bak1", broken.Error, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Root, "broken.msave.corrupt-*"));
    }

    [Fact]
    public void ASaveFromANewerEngineIsTooNewAndUntouched()
    {
        using var server = CreateServer();
        var payload = "{\"name\":\"future\",\"score\":1,\"items\":[]}"u8.ToArray();
        var header = new SaveHeader { Kind = SaveFile.Kind, Format = 2, Slot = "future", Version = 1, SavedUtc = DateTimeOffset.UtcNow, PayloadBytes = payload.Length, Sha256 = SaveFile.Hash(payload) };
        var file = SaveFile.Build(header, payload);
        File.WriteAllBytes(PathOf("future.msave"), file);
        var written = File.GetLastWriteTimeUtc(PathOf("future.msave"));

        var load = server.Load("future", Schema);

        Assert.Equal(LoadStatus.TooNew, load.Status);
        Assert.Contains("format 2", load.Error, StringComparison.Ordinal);
        Assert.Equal(file, File.ReadAllBytes(PathOf("future.msave")));
        Assert.Equal(written, File.GetLastWriteTimeUtc(PathOf("future.msave")));
        Assert.Single(Directory.GetFiles(Root));
    }

    [Fact]
    public void ANewerPayloadVersionIsTooNewAndUntouched()
    {
        using var server = CreateServer();
        server.Save("slot1", Data("v3"), new SaveSchema<TestSave>(SaveTestJson.Default.TestSave, 3));
        var file = File.ReadAllBytes(PathOf("slot1.msave"));

        var load = server.Load("slot1", new SaveSchema<TestSave>(SaveTestJson.Default.TestSave, 2));

        Assert.Equal(LoadStatus.TooNew, load.Status);
        Assert.Equal(3, load.Info!.Version);
        Assert.Equal(file, File.ReadAllBytes(PathOf("slot1.msave")));
        Assert.Single(Directory.GetFiles(Root));
    }

    [Fact]
    public void AnOlderPayloadMigrates()
    {
        using var server = CreateServer();
        server.Save("slot1", new OldSave("veteran"), new SaveSchema<OldSave>(SaveTestJson.Default.OldSave, 1));
        var v2 = new SaveSchema<TestSave>(SaveTestJson.Default.TestSave, 2, new JsonMigration(1, static root =>
        {
            root["score"] = 99;
            root["items"] = new JsonArray(7);
        }));

        var load = server.Load("slot1", v2);

        Assert.Equal(LoadStatus.Ok, load.Status);
        Assert.Equal(new TestSave("veteran", 99, [7]).Items, load.Data!.Items);
        Assert.Equal(99, load.Data.Score);
    }

    [Fact]
    public void APayloadOverTheLimitIsTooLargeAndWritesNothing()
    {
        using var server = CreateServer(new SaveOptions { MaxBytes = 64 });
        var raised = new List<SaveResult>();
        server.Saved += raised.Add;

        var async = Pump(server, server.SaveAsync("big", Data(new string('x', 100)), Schema));
        var sync = server.Save("big", Data(new string('x', 100)), Schema);

        Assert.Equal(SaveStatus.TooLarge, async.Status);
        Assert.Equal(SaveStatus.TooLarge, sync.Status);
        Assert.Contains("maxBytes", async.Error, StringComparison.Ordinal);
        Assert.Equal(2, raised.Count);
        Assert.Empty(Directory.GetFiles(Root));
    }

    [Fact]
    public void QueuedSavesOfTheSameSlotCoalesce()
    {
        using var gate = new ManualResetEventSlim();
        var storage = new InstrumentedStorage(new LocalSaveStorage(Root)) { Gate = gate };
        using var server = CreateServer(storage: storage);
        var saved = new List<SaveResult>();
        server.Saved += saved.Add;

        var first = server.SaveAsync("slot1", Data("v1"), Schema);
        Assert.True(storage.Entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)); // the worker is inside the first write
        var second = server.SaveAsync("slot1", Data("v2"), Schema);
        var other = server.SaveAsync("other", Data("o"), Schema);
        var third = server.SaveAsync("slot1", Data("v3"), Schema);
        gate.Set();

        Assert.True(Pump(server, first).IsOk);
        var secondResult = Pump(server, second);
        var thirdResult = Pump(server, third);
        Assert.True(Pump(server, other).IsOk);

        Assert.True(secondResult.IsOk);
        Assert.Equal(secondResult, thirdResult); // both callers get the result of the one write
        Assert.Equal(2, storage.Writes["slot1.msave"]); // v1, then v3 (v2 was never written)
        Assert.Equal(3, saved.Count);                  // one Saved per write
        Assert.Equal("v3", server.Load("slot1", Schema).Data!.Name);
        File.Copy(PathOf("slot1.msave.bak1"), PathOf("check.msave"));
        Assert.Equal("v1", server.Load("check", Schema).Data!.Name); // the previous save
    }

    [Fact]
    public async Task DisposeFlushesQueuedSaves()
    {
        var storage = new InstrumentedStorage(new LocalSaveStorage(Root)) { WriteDelay = TimeSpan.FromMilliseconds(200) };
        var server = new SaveServer(storage, new SaveOptions());
        var task = server.SaveAsync("quit", Data("last words"), Schema);

        server.Dispose(); // the game quits right after SaveAsync

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(SaveStatus.Ok, (await task).Status);
        using var reader = CreateServer();
        Assert.Equal("last words", reader.Load("quit", Schema).Data!.Name);
        Assert.Throws<ObjectDisposedException>(() => { _ = server.SaveAsync("quit", Data("late"), Schema); });
    }

    [Fact]
    public void FlushWaitsForQueuedWrites()
    {
        var storage = new InstrumentedStorage(new LocalSaveStorage(Root)) { WriteDelay = TimeSpan.FromMilliseconds(100) };
        using var server = CreateServer(storage: storage);
        var task = server.SaveAsync("slot1", Data("x"), Schema);

        Assert.False(server.Flush(TimeSpan.FromMilliseconds(1)));
        Assert.True(server.Flush(TimeSpan.FromSeconds(10)));
        Assert.True(File.Exists(PathOf("slot1.msave")));
        Assert.False(task.IsCompleted); // delivered by the next Process
        server.Process(new GameTime());
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void ResultsCompleteOnTheThreadThatCallsProcess()
    {
        using var server = CreateServer();
        int? savedThread = null, continuationThread = null;
        server.Saved += _ => savedThread = Environment.CurrentManagedThreadId;
        var task = server.SaveAsync("slot1", Data("x"), Schema);
        var continuation = task.ContinueWith(_ => continuationThread = Environment.CurrentManagedThreadId, TestContext.Current.CancellationToken,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Assert.True(server.Flush(TimeSpan.FromSeconds(10)));
        Assert.False(task.IsCompleted);

        var processThread = new Thread(() => Pump(server, continuation));
        processThread.Start();
        processThread.Join();

        Assert.Equal(processThread.ManagedThreadId, savedThread);
        Assert.Equal(processThread.ManagedThreadId, continuationThread);
    }

    [Fact]
    public void LoadAsyncCompletesInProcessAndSetsThePlayTime()
    {
        using var server = CreateServer();
        server.PlayTime = TimeSpan.FromMinutes(3);
        server.Save("slot1", Data("x"), Schema);
        server.PlayTime = TimeSpan.Zero;

        var task = server.LoadAsync("slot1", Schema);
        Assert.True(server.Flush(TimeSpan.FromSeconds(10)));
        Assert.False(task.IsCompleted);
        Assert.Equal(TimeSpan.Zero, server.PlayTime);
        var load = Pump(server, task);

        Assert.Equal(LoadStatus.Ok, load.Status);
        Assert.Equal(180, server.PlayTime.TotalSeconds, 3);
        Assert.Equal(LoadStatus.NotFound, Pump(server, server.LoadAsync("missing", Schema)).Status);
    }

    [Fact]
    public void PlayTimeAccumulatesOnlyWhileTheTreeIsUnpaused()
    {
        var tree = new SceneTree();
        using var server = CreateServer(tree: tree);

        server.Process(new GameTime { DeltaTime = 0.5f });
        server.Process(new GameTime { DeltaTime = 0.25f });
        Assert.Equal(0.75, server.PlayTime.TotalSeconds, 5);

        tree.Paused = true;
        server.Process(new GameTime { DeltaTime = 10f });
        Assert.Equal(0.75, server.PlayTime.TotalSeconds, 5);

        tree.Paused = false;
        server.Process(new GameTime { DeltaTime = 0.25f });
        Assert.Equal(1.0, server.PlayTime.TotalSeconds, 5);
        Assert.Equal(1.0, server.Save("slot1", Data("x"), Schema).Info!.PlayTime.TotalSeconds, 3);
    }

    [Fact]
    public void DeleteRemovesTheSlotItsBackupsAndThumbnailButKeepsDamagedCopies()
    {
        using var server = CreateServer();
        for (var i = 0; i < 3; i++)
            server.Save("slot1", Data("v" + i), Schema);
        server.Save("slot2", Data("other"), Schema);
        File.WriteAllBytes(PathOf("slot1.png"), [1, 2, 3]);
        File.WriteAllText(PathOf("slot1.msave.corrupt-20260101T000000000Z"), "damaged");
        Assert.True(File.Exists(PathOf("slot1.msave.bak2")));

        Assert.True(server.Delete("slot1"));

        Assert.False(server.Exists("slot1"));
        Assert.Equal(["slot1.msave.corrupt-20260101T000000000Z", "slot2.msave"], Directory.GetFiles(Root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(["slot2"], server.ListSlots().Select(s => s.Slot));
        Assert.False(server.Delete("slot1"));
    }

    [Fact]
    public void BackupsRollPerTheOptions()
    {
        using var server = CreateServer(new SaveOptions { Backups = 3 });
        for (var i = 0; i < 6; i++)
            server.Save("slot1", Data("v" + i), Schema);

        Assert.True(File.Exists(PathOf("slot1.msave.bak3")));
        Assert.False(File.Exists(PathOf("slot1.msave.bak4")));
    }

    [Fact]
    public void SaveOptionsComeFromTheProject()
    {
        var project = new ProjectSettings { Name = "Proj", Version = "3.1" };
        project.Saves.MaxBytes = 1000;
        project.Saves.Backups = 4;
        project.Saves.Compression = SaveCompression.Gzip;
        project.Saves.ThumbnailWidth = 160;
        project.Saves.ThumbnailHeight = 90;

        var options = SaveOptions.FromProject(project);

        Assert.Equal(1000, options.MaxBytes);
        Assert.Equal(4, options.Backups);
        Assert.Equal(SaveCompression.Gzip, options.Compression);
        Assert.Equal(160, options.ThumbnailWidth);
        Assert.Equal(90, options.ThumbnailHeight);
        Assert.Equal("Proj", options.GameName);
        Assert.Equal("3.1", options.GameVersion);
    }
}

[Collection(nameof(Physics.SerialAllocationGates))]
public sealed class SaveServerAllocationTests : SaveTestBase
{
    [Fact]
    public void ProcessAllocatesNothingWhenIdle()
    {
        var tree = new SceneTree();
        using var server = CreateServer(tree: tree);
        server.Save("slot1", new TestSave("x", 1, []), Schema);
        Pump(server, server.SaveAsync("slot1", new TestSave("y", 2, []), Schema)); // warm the completion path
        var time = new GameTime { DeltaTime = 1f / 60 };

        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                tree.Paused = (i & 1) == 0;
                server.Process(time);
            }
        });

        Assert.Equal(0, allocated);
    }
}
