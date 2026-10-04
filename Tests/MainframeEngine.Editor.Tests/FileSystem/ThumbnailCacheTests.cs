namespace MainframeEngine.Editor.Tests.FileSystem;

public sealed class ThumbnailCacheTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("mf-thumbs").FullName;
    private readonly ThumbnailCache _cache;
    private readonly List<(string Source, string Thumbnail)> _ready = [];
    private readonly List<(string Source, string Message)> _failed = [];

    public ThumbnailCacheTests()
    {
        _cache = new ThumbnailCache(Path.Combine(_temp, ".mainframe", "cache", "thumbnails"), size: 32);
        _cache.Ready += (source, thumbnail) => _ready.Add((source, thumbnail));
        _cache.Failed += (source, message) => _failed.Add((source, message));
    }

    public void Dispose()
    {
        _cache.Dispose();
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private bool PumpUntil(Func<bool> done) => FileSystemProject.WaitUntil(() =>
    {
        _cache.Pump();
        return done();
    });

    [Fact]
    public void GeneratesCachesAndRegeneratesThumbnails()
    {
        var image = Path.Combine(_temp, "wide.png");
        FileSystemProject.WritePng(image, 200, 100);

        Assert.False(_cache.TryGet(image, out var thumbnail));
        Assert.Null(thumbnail);
        Assert.False(_cache.TryGet(image, out _)); // queued once
        Assert.True(PumpUntil(() => _ready.Count == 1), "No thumbnail was generated.");
        Assert.Equal(image, _ready[0].Source);
        var png = Png.ReadRgba8(_ready[0].Thumbnail);
        Assert.Equal((32, 16), (png.Width, png.Height));

        Assert.True(_cache.TryGet(image, out thumbnail));
        Assert.Equal(_ready[0].Thumbnail, thumbnail);

        // A modified source is regenerated.
        FileSystemProject.WritePng(image, 10, 40, seed: 9);
        File.SetLastWriteTimeUtc(image, DateTime.UtcNow.AddMinutes(1));
        Assert.False(_cache.TryGet(image, out _));
        Assert.True(PumpUntil(() => _ready.Count == 2), "The thumbnail was not regenerated.");
        png = Png.ReadRgba8(_ready[1].Thumbnail);
        Assert.Equal((8, 32), (png.Width, png.Height));
        Assert.True(_cache.TryGet(image, out _));

        // A second cache over the same folder finds it on disk.
        using var reopened = new ThumbnailCache(_cache.CacheDirectory, size: 32);
        Assert.True(reopened.TryGet(image, out var again));
        Assert.Equal(thumbnail, again);
        Assert.Empty(_failed);
    }

    [Fact]
    public void CorruptImagesFailOnceUntilTheyChange()
    {
        var image = Path.Combine(_temp, "broken.png");
        File.WriteAllBytes(image, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5, 6, 7]);

        Assert.False(_cache.TryGet(image, out _));
        Assert.True(PumpUntil(() => _failed.Count == 1), "No failure was reported.");
        Assert.Equal(image, _failed[0].Source);
        Assert.StartsWith("Cannot read the image 'broken.png'", _failed[0].Message, StringComparison.Ordinal);

        Assert.False(_cache.TryGet(image, out _)); // not retried
        Thread.Sleep(50);
        Assert.Equal(0, _cache.Pump());
        Assert.Empty(_ready);
    }

    [Fact]
    public void DownscalingWeightsColourByAlphaAndNeverEnlarges()
    {
        byte[] quad =
        [
            255, 0, 0, 255, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
        ];
        var (width, height, pixels) = ThumbnailCache.Downscale(2, 2, quad, 1);
        Assert.Equal((1, 1), (width, height));
        Assert.Equal(new byte[] { 255, 0, 0, 64 }, pixels); // transparent black does not darken the red

        var small = ThumbnailCache.Downscale(2, 2, quad, 96);
        Assert.Equal((2, 2), (small.Width, small.Height));
        Assert.Equal(quad, small.Pixels);
    }
}
