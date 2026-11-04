using System.Numerics;

namespace MainframeEngine.Tests.Scene;

/// <summary><see cref="SceneTree.FilesDropped"/>: OS drag and drop (docs/design/future/video-nle.md, G20a).</summary>
public sealed class FilesDroppedTests : IDisposable
{
    private readonly SceneTree _tree = new();

    public void Dispose() => _tree.Shutdown();

    [Fact]
    public void RaisesOnceWithPathsAndPosition()
    {
        var drops = new List<FilesDroppedEvent>();
        _tree.FilesDropped += drops.Add;

        _tree.NotifyFilesDropped(["/a/clip.mp4", "/b/music.ogg"], new Vector2(120, 340));

        var drop = Assert.Single(drops);
        Assert.Equal(["/a/clip.mp4", "/b/music.ogg"], drop.Paths);
        Assert.Equal(new Vector2(120, 340), drop.Position);
    }

    [Fact]
    public void AnEmptyDropRaisesNothing()
    {
        var raised = 0;
        _tree.FilesDropped += _ => raised++;

        _tree.NotifyFilesDropped([], Vector2.Zero);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void ADropWithoutSubscribersIsIgnored() =>
        _tree.NotifyFilesDropped(["/a/clip.mp4"], Vector2.Zero);
}
