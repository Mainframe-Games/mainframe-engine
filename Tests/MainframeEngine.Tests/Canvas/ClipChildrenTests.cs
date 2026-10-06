using System.Numerics;

namespace MainframeEngine.Tests.Canvas;

/// <summary>Godot's clip children (ADR 0119): the group pass, the Atop blend of its members and the owner's composite.</summary>
public sealed class ClipChildrenTests : IDisposable
{
    private readonly SceneTree _tree;
    private readonly CanvasServer _server;
    private readonly Node2D _root = new() { Name = "Root" };

    public ClipChildrenTests()
    {
        _tree = new SceneTree(new ServerRegistry());
        _server = new CanvasServer(_tree, () => new Vector2(200, 100));
        _tree.ChangeScene(_root);
    }

    public void Dispose()
    {
        _server.Dispose();
        _tree.Shutdown();
    }

    private CanvasFrame Frame()
    {
        _server.Process(new GameTime { DeltaTime = 1f / 60f });
        return _server.Frame;
    }

    private (ClipBox Owner, ClipBox Child, ClipBox Sibling) Build(ClipChildrenMode mode)
    {
        var owner = new ClipBox { Name = "Owner", Position = new Vector2(20, 10), ClipChildren = mode };
        var child = new ClipBox { Name = "Child", Position = new Vector2(5, 5) };
        owner.AddChild(child);
        var sibling = new ClipBox { Name = "Sibling", Position = new Vector2(100, 10), Modulate = new Vector4(1, 0, 0, 1) };
        _root.AddChild(owner);
        _root.AddChild(sibling);
        return (owner, child, sibling);
    }

    [Fact]
    public void AndDrawDrawsTheGroupOnItsOwnTargetBeforeTheMainPass()
    {
        var (owner, _, _) = Build(ClipChildrenMode.AndDraw);
        var frame = Frame();

        Assert.Equal(2, frame.Passes.Count);
        var group = frame.Passes[0];
        Assert.Same(owner, group.ClipOwner);
        Assert.Equal(Vector4.Zero, group.ClearColor);
        // Owner draws normally (mix), the child only where the owner already is (atop).
        Assert.Equal(2, group.BatchCount);
        Assert.Equal(CanvasBlendMode.Mix, frame.Batches[group.FirstBatch].Blend);
        Assert.Equal(CanvasBlendMode.Atop, frame.Batches[group.FirstBatch + 1].Blend);

        // Main pass: the composite (premultiplied quad of the group target over the owner's bounds), then the sibling.
        var main = frame.Passes[1];
        Assert.Null(main.ClipOwner);
        Assert.Equal(2, main.BatchCount);
        var composite = frame.Batches[main.FirstBatch];
        Assert.Equal(CanvasBlendMode.PremultAlpha, composite.Blend);
        Assert.Same(owner, composite.Texture!.ClipGroup);
        Assert.Equal(CanvasBlendMode.Mix, frame.Batches[main.FirstBatch + 1].Blend);
    }

    [Fact]
    public void TheCompositeCoversTheOwnersBoundsWithTargetUvs()
    {
        Build(ClipChildrenMode.AndDraw);
        var frame = Frame();
        var composite = frame.Batches[frame.Passes[1].FirstBatch];
        var first = frame.Indices[composite.FirstIndex];
        var v = frame.Vertices[(int)first];
        Assert.Equal(new Vector2(20, 10), v.Position);          // owner at (20, 10), 10×10 box
        Assert.Equal(new Vector2(0.1f, 0.1f), v.Uv);              // ÷ the 200×100 target
        var far = frame.Vertices[(int)first + 2];
        Assert.Equal(new Vector2(30, 20), far.Position);
    }

    [Fact]
    public void OnlyModeDrawsLikeAndDraw()
    {
        // Deviation (ADR 0119): the owner stays visible in Only mode, since the group target holds the owner as its mask.
        var (owner, _, _) = Build(ClipChildrenMode.Only);
        var frame = Frame();
        var group = frame.Passes[0];
        Assert.Same(owner, group.ClipOwner);
        Assert.Equal(2, group.BatchCount);
        Assert.Equal(CanvasBlendMode.Atop, frame.Batches[group.FirstBatch + 1].Blend);
    }

    [Fact]
    public void DisabledDrawsEverythingInOnePass()
    {
        Build(ClipChildrenMode.Disabled);
        var frame = Frame();
        var pass = Assert.Single(frame.Passes);
        Assert.Null(pass.ClipOwner);
        Assert.All(Enumerable.Range(pass.FirstBatch, pass.BatchCount), b => Assert.NotEqual(CanvasBlendMode.Atop, frame.Batches[b].Blend));
    }

    [Fact]
    public void ANestedClipOwnerJoinsTheOuterGroup()
    {
        var (owner, child, _) = Build(ClipChildrenMode.AndDraw);
        child.ClipChildren = ClipChildrenMode.AndDraw;
        child.AddChild(new ClipBox { Name = "Grandchild" });
        var frame = Frame();
        Assert.Equal(2, frame.Passes.Count);
        Assert.Same(owner, frame.Passes[0].ClipOwner);
        // The owner (mix), then child and grandchild merged into one atop batch: no group of their own.
        var group = frame.Passes[0];
        Assert.Equal(2, group.BatchCount);
        Assert.Equal(CanvasBlendMode.Atop, frame.Batches[group.FirstBatch + 1].Blend);
    }
}

/// <summary>A 10×10 white box.</summary>
public sealed class ClipBox : Node2D
{
    protected override void OnDraw() => DrawRect(new Rect2(0, 0, 10, 10), Vector4.One);
}
