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
}
