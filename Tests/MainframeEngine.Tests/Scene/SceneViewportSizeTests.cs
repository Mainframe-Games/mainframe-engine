using System.Numerics;

namespace MainframeEngine.Tests.Scene;

/// <summary>Buy My Snake Oil's port: <see cref="SceneViewport.SizeChanged"/> (#111) and an assignable <see cref="SceneTree.CurrentScene"/> (#115).</summary>
public sealed class SceneViewportSizeTests : IDisposable
{
    private readonly SceneTree _tree = new();

    public void Dispose() => _tree.Shutdown();

    [Fact]
    public void SizeChangedFiresOncePerChangeWithTheVisibleRectUpdated()
    {
        var root = _tree.Root;
        var seen = new List<Rect2>();
        root.SizeChanged += () => seen.Add(root.GetVisibleRect());

        root.SetSize(new Vector2(640, 360));
        root.SetSize(new Vector2(640, 360)); // unchanged: no signal
        root.SetSize(new Vector2(800, 600));

        Assert.Equal([new Rect2(Vector2.Zero, new Vector2(640, 360)), new Rect2(Vector2.Zero, new Vector2(800, 600))], seen);
    }

    [Fact]
    public void SizeChangedFiresWhenTheStretchChanges()
    {
        var root = _tree.Root;
        root.SetSize(new Vector2(1920, 1080));
        var count = 0;
        root.SizeChanged += () => count++;

        root.ContentScaleMode = ContentScaleMode.CanvasItems;
        root.ContentScaleSize = new Vector2(960, 540);
        root.SetSize(new Vector2(1920, 1080)); // the engine's per-frame call applies the new stretch

        Assert.Equal(1, count);
        Assert.Equal(new Vector2(960, 540), root.GetVisibleRect().Size);
    }

    [Fact]
    public void AssigningCurrentSceneHandsTheNextChangeToThatScene()
    {
        var harness = new Node { Name = "Harness" };
        _tree.ChangeScene(harness);
        var title = new Node { Name = "Title" };
        _tree.Root.AddChild(title);

        _tree.CurrentScene = title;
        _tree.ChangeScene(new Node { Name = "Game" });

        Assert.True(title.IsFreed);
        Assert.False(harness.IsFreed); // no longer current: the change left it alone
        Assert.True(harness.IsInsideTree);
        Assert.Equal("Game", _tree.CurrentScene!.Name);
    }

    [Fact]
    public void CurrentSceneMustBeAChildOfTheRoot()
    {
        var parent = new Node { Name = "Parent" };
        var child = new Node { Name = "Child" };
        parent.AddChild(child);
        _tree.Root.AddChild(parent);

        Assert.Throws<InvalidOperationException>(() => _tree.CurrentScene = child);
        Assert.Throws<InvalidOperationException>(() => _tree.CurrentScene = new Node());
        _tree.CurrentScene = parent;
        _tree.CurrentScene = null;
        Assert.Null(_tree.CurrentScene);
        Assert.False(parent.IsFreed);
    }
}
