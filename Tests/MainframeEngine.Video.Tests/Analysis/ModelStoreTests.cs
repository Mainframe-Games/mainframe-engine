using MainframeEngine.Video.Analysis;

namespace MainframeEngine.Video.Tests.Analysis;

public sealed class ModelStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mainframe-video-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
        else if (File.Exists(_dir))
        {
            File.Delete(_dir);
        }
    }

    [Fact]
    public void PathsLiveUnderTheCacheDirectoryAndAreNamedByModel()
    {
        var store = new ModelStore(_dir);

        Assert.Equal(_dir, store.CacheDirectory);
        Assert.Equal(Path.Combine(_dir, "ggml-small.en.bin"), store.PathFor(WhisperModel.SmallEn));
    }

    [Fact]
    public void IsDownloadedReflectsTheFileOnDisk()
    {
        var store = new ModelStore(_dir);
        Assert.False(store.IsDownloaded(WhisperModel.BaseEn));

        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(store.PathFor(WhisperModel.BaseEn), [1, 2, 3]);

        Assert.True(store.IsDownloaded(WhisperModel.BaseEn));
    }

    [Fact]
    public async Task EnsureAsyncReturnsAnExistingFileWithoutTouchingTheNetwork()
    {
        var store = new ModelStore(_dir);
        Directory.CreateDirectory(_dir);
        var path = store.PathFor(WhisperModel.TinyEn);
        File.WriteAllBytes(path, [1, 2, 3]);

        var result = await store.EnsureAsync(WhisperModel.TinyEn, null, CancellationToken.None);

        Assert.Equal(path, result);
        Assert.Equal<byte>([1, 2, 3], File.ReadAllBytes(path));
    }

    [Fact]
    public async Task EnsureAsyncWithACancelledTokenThrowsAndLeavesNoPartialFile()
    {
        var store = new ModelStore(_dir);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.EnsureAsync(WhisperModel.TinyEn, null, cts.Token));

        Assert.True(Directory.Exists(_dir));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task EnsureAsyncWrapsAnUncreatableCacheDirectoryInModelDownloadException()
    {
        // A file where the directory should be makes CreateDirectory throw IOException.
        Directory.CreateDirectory(Path.GetDirectoryName(_dir)!);
        File.WriteAllBytes(_dir, [0]);
        var store = new ModelStore(_dir);

        var e = await Assert.ThrowsAsync<ModelDownloadException>(() => store.EnsureAsync(WhisperModel.TinyEn, null, CancellationToken.None));

        Assert.Contains("tiny.en", e.Message, StringComparison.Ordinal);
        Assert.Contains(_dir, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsPlausiblyCompleteIsFalseWhenMissingOrShortAndTrueWhenNearTheExpectedSize()
    {
        var store = new ModelStore(_dir);
        Directory.CreateDirectory(_dir);

        Assert.False(store.IsPlausiblyComplete(WhisperModel.TinyEn));

        var path = store.PathFor(WhisperModel.TinyEn);
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.False(store.IsPlausiblyComplete(WhisperModel.TinyEn));

        using (var stream = new FileStream(path, FileMode.Create))
        {
            stream.SetLength((long)(WhisperModelInfo.For(WhisperModel.TinyEn).ApproximateBytes * 0.95));
        }

        Assert.True(store.IsPlausiblyComplete(WhisperModel.TinyEn));
    }

    [Fact]
    public void DeleteRemovesTheCachedModelAndIsANoOpWhenAbsent()
    {
        var store = new ModelStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(store.PathFor(WhisperModel.BaseEn), [1, 2, 3]);

        store.Delete(WhisperModel.BaseEn);

        Assert.False(store.IsDownloaded(WhisperModel.BaseEn));

        store.Delete(WhisperModel.BaseEn);
    }

    [Fact]
    public void EveryDownloadWritesItsOwnPartFileBesideTheModel()
    {
        var path = Path.Combine(_dir, "ggml-tiny.en.bin");
        var first = ModelStore.PartPathFor(path);
        var second = ModelStore.PartPathFor(path);

        Assert.NotEqual(first, second); // two first-time downloads of one model never share a file
        Assert.Equal(_dir, Path.GetDirectoryName(first));
        Assert.StartsWith("ggml-tiny.en.bin.", Path.GetFileName(first), StringComparison.Ordinal);
        Assert.EndsWith(".part", first, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstFinishedDownloadWinsAndALaterOneIsDiscarded()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "ggml-tiny.en.bin");
        var first = ModelStore.PartPathFor(path);
        var second = ModelStore.PartPathFor(path);
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [2]);

        ModelStore.Commit(first, path);
        Assert.Equal<byte>([1], File.ReadAllBytes(path));
        Assert.False(File.Exists(first));

        ModelStore.Commit(second, path); // the model is already in place: keep it, drop this copy
        Assert.Equal<byte>([1], File.ReadAllBytes(path));
        Assert.False(File.Exists(second));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    [Fact]
    public void AbandonedPartFilesAreDeletedAndRunningOnesKept()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "ggml-tiny.en.bin");
        var now = DateTime.UtcNow;
        var abandoned = ModelStore.PartPathFor(path);
        var running = ModelStore.PartPathFor(path);
        var otherModel = ModelStore.PartPathFor(Path.Combine(_dir, "ggml-base.en.bin"));
        foreach (var file in new[] { abandoned, running, otherModel })
        {
            File.WriteAllBytes(file, [1]);
        }

        File.SetLastWriteTimeUtc(abandoned, now - ModelStore.AbandonedPartAge - TimeSpan.FromMinutes(1));
        File.SetLastWriteTimeUtc(otherModel, now - TimeSpan.FromDays(1));

        ModelStore.DeleteAbandonedParts(path, now);

        Assert.False(File.Exists(abandoned)); // interrupted an hour ago: nothing would ever replace it
        Assert.True(File.Exists(running)); // written just now: a download in progress
        Assert.True(File.Exists(otherModel)); // another model's: not this download's to judge
    }

    [Fact]
    public void SweepingAFolderThatDoesNotExistDoesNothing() =>
        ModelStore.DeleteAbandonedParts(Path.Combine(_dir, "missing", "ggml-tiny.en.bin"), DateTime.UtcNow);
}
