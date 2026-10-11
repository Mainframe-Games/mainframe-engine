using System.Text;
using System.Text.Json.Nodes;

namespace MainframeEngine.Tests.Saves;

public sealed class SaveThumbnailTests : SaveTestBase
{
    // A tree that says it can capture frames; the test delivers the capture the engine would.
    private static SceneTree CapturingTree(out List<int> requests)
    {
        var tree = new SceneTree { CanCaptureFrame = true };
        var list = requests = [];
        tree.CaptureRequested += () => list.Add(1);
        return tree;
    }

    private static FrameCapture Frame(int width, int height, byte red)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = red;
            pixels[i + 1] = 20;
            pixels[i + 2] = 30;
            pixels[i + 3] = 0; // swapchain alpha: forced opaque
        }

        return new FrameCapture(width, height, pixels);
    }

    private static string? HeaderThumbnail(string path)
    {
        var file = File.ReadAllBytes(path);
        return (string?)JsonNode.Parse(Encoding.UTF8.GetString(file, 0, Array.IndexOf(file, (byte)'\n')))!["thumbnail"];
    }

    [Fact]
    public void ASaveWithAThumbnailWaitsForTheFrameThenWritesAScaledPng()
    {
        var tree = CapturingTree(out var requests);
        using var server = CreateServer(tree: tree);

        var task = server.SaveAsync("slot1", Data("x"), Schema, new SaveInfo(Thumbnail: true));
        Assert.Single(requests);
        server.Process(new GameTime());
        Assert.False(task.IsCompleted); // waiting for the frame
        tree.DeliverCapture(Frame(1280, 720, 200));
        var result = Pump(server, task);

        Assert.True(result.IsOk);
        Assert.Equal(PathOf("slot1.png"), result.Info!.ThumbnailPath);
        Assert.Equal("slot1.png", HeaderThumbnail(PathOf("slot1.msave")));
        var png = Png.ReadRgba8(PathOf("slot1.png"));
        Assert.Equal(320, png.Width);
        Assert.Equal(180, png.Height);
        Assert.Equal(200, png.Pixels[0]);
        Assert.Equal(255, png.Pixels[3]);
        Assert.Equal(PathOf("slot1.png"), server.ListSlots().Single().ThumbnailPath);
    }

    [Fact]
    public void CaptureThumbnailIsUsedByTheNextSave()
    {
        var tree = CapturingTree(out var requests);
        using var server = CreateServer(new SaveOptions { ThumbnailWidth = 16, ThumbnailHeight = 9 }, tree);

        server.CaptureThumbnail(); // the pause menu opens
        tree.DeliverCapture(Frame(160, 90, 50));
        var result = server.Save("slot1", Data("x"), Schema); // blocking: uses the frame already captured

        Assert.Single(requests);
        Assert.NotNull(result.Info!.ThumbnailPath);
        var png = Png.ReadRgba8(PathOf("slot1.png"));
        Assert.Equal((16, 9), (png.Width, png.Height));

        // Used once: the next save has none, and the old thumbnail goes with it.
        Assert.Null(server.Save("slot1", Data("y"), Schema).Info!.ThumbnailPath);
        Assert.False(File.Exists(PathOf("slot1.png")));
    }

    [Fact]
    public void WithoutFrameCaptureThumbnailsAreIgnored()
    {
        using var server = CreateServer(tree: new SceneTree());

        server.CaptureThumbnail();
        var result = Pump(server, server.SaveAsync("slot1", Data("x"), Schema, new SaveInfo(Thumbnail: true)));

        Assert.True(result.IsOk);
        Assert.Null(result.Info!.ThumbnailPath);
        Assert.Null(HeaderThumbnail(PathOf("slot1.msave")));
        Assert.False(File.Exists(PathOf("slot1.png")));
    }

    [Fact]
    public void FlushWritesASaveStillWaitingForItsFrameWithoutOne()
    {
        var tree = CapturingTree(out _);
        using var server = CreateServer(tree: tree);
        var task = server.SaveAsync("slot1", Data("x"), Schema, new SaveInfo(Thumbnail: true));

        Assert.True(server.Flush(TimeSpan.FromSeconds(10)));

        Assert.Null(Pump(server, task).Info!.ThumbnailPath);
        Assert.True(File.Exists(PathOf("slot1.msave")));
    }

    [Fact]
    public void TheBoxFilterAveragesAndCropsToTheTargetAspect()
    {
        // 4x2 → 2x1: each output pixel averages a 2x2 block.
        byte[] pixels =
        [
            0, 0, 0, 0,   100, 0, 0, 0,   10, 10, 10, 0,   10, 10, 10, 0,
            0, 0, 0, 0,   100, 0, 0, 0,   30, 30, 30, 0,   30, 30, 30, 0,
        ];
        var scaled = SaveThumbnail.Downscale(pixels, 4, 2, 2, 1);
        Assert.Equal(new byte[] { 50, 0, 0, 255, 20, 20, 20, 255 }, scaled);

        // 6x2 → 2x2 (square): the centre 2x2 block is kept, the sides are cropped.
        var wide = new byte[6 * 2 * 4];
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 6; x++)
                wide[(y * 6 + x) * 4] = (byte)(x * 10);
        var square = SaveThumbnail.Downscale(wide, 6, 2, 2, 2);
        Assert.Equal(20, square[0]);
        Assert.Equal(30, square[4]);
    }
}
