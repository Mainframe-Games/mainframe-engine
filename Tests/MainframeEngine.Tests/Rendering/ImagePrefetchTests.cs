namespace MainframeEngine.Tests.Rendering;

/// <summary>
/// ADR 0183: during an asynchronous scene load, the images of the textures it creates are decoded (and coverage mip chains
/// built) on worker threads, shared per file, and handed to their users; outside a load nothing changes.
/// </summary>
public sealed class ImagePrefetchTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("mf-prefetch").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WritePng(string name, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i++)
            rgba[i] = (byte)(i * 7 % 251);
        var path = Path.Combine(_folder, name);
        Png.WriteRgba8(path, width, height, rgba);
        return path;
    }

    [Fact]
    public async Task TexturesCreatedDuringALoadDecodeOnceAheadAndShareThePixels()
    {
        var path = WritePng("albedo.png", 64, 32);
        var expected = Texture2D.FromFile(path).DecodePixels(); // no prefetch live: an ordinary decode
        using var prefetch = ImagePrefetch.Begin();

        // The load's flow (a worker, as SceneLoad's): textures created there are queued, one decode per file.
        var (first, second) = await Task.Run(() =>
        {
            prefetch.CollectOnCurrentFlow();
            return (Texture2D.FromFile(path), Texture2D.FromFile(path));
        }, TestContext.Current.CancellationToken);
        Assert.Equal(1, prefetch.Count);
        await prefetch.WhenAll().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal(1, prefetch.Completed);
        Assert.Equal(1f, prefetch.Fraction);

        // Readers share the decoded array; DecodePixels hands out copies (callers may modify theirs).
        var shared = first.DecodePixelsReadOnly();
        Assert.Same(shared.Rgba, second.DecodePixelsReadOnly().Rgba);
        var copy = first.DecodePixels();
        Assert.NotSame(shared.Rgba, copy.Rgba);
        Assert.Equal(expected.Rgba, copy.Rgba);
        Assert.Equal((64, 32), (first.Width, first.Height));

        // A texture created on another flow is not queued; after the load everything decodes on demand again.
        _ = Texture2D.FromFile(WritePng("other.png", 4, 4));
        Assert.Equal(1, prefetch.Count);
        prefetch.Dispose();
        Assert.NotSame(shared.Rgba, first.DecodePixelsReadOnly().Rgba);
    }

    [Fact]
    public async Task CoverageMipChainsAreBuiltAheadForTheColourSpaceInUse()
    {
        var rgba = new byte[32 * 32 * 4];
        for (var i = 0; i < rgba.Length; i++)
            rgba[i] = (byte)((i * 13) & 0xff);
        var settings = new TextureImportSettings { PreserveAlphaCoverage = true, AlphaCoverageCutoff = 0.4f };
        using var prefetch = ImagePrefetch.Begin();
        var texture = await Task.Run(() =>
        {
            prefetch.CollectOnCurrentFlow();
            return Texture2D.FromPixels(32, 32, rgba, settings);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(2, prefetch.Count); // auto colour space: sRGB as albedo, linear as data
        await prefetch.WhenAll().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.True(ImagePrefetch.TryGetMips(texture, srgb: true, out var srgb));
        Assert.Equal(MipChain.Build(rgba, 32, 32, srgb: true, 0.4f), srgb);
        Assert.True(ImagePrefetch.TryGetMips(texture, srgb: false, out var linear));
        Assert.Equal(MipChain.Build(rgba, 32, 32, srgb: false, 0.4f), linear);

        // A changed texture (a new version) is built again where it is used.
        texture.ImportSettings = settings with { AlphaCoverageCutoff = 0.6f };
        Assert.False(ImagePrefetch.TryGetMips(texture, srgb: true, out _));

        // Textures without coverage mips queue nothing.
        await Task.Run(() =>
        {
            prefetch.CollectOnCurrentFlow();
            _ = Texture2D.FromPixels(2, 2, new byte[16]);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(2, prefetch.Count);
    }
}
