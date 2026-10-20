using System.Numerics;

namespace MainframeEngine.Tests.Canvas;

/// <summary>
/// #109: Godot's <see cref="CanvasGroup"/>: its children on the group's own target, then one composite quad over their
/// bounds (plus the fit margin) with the group's modulate.
/// </summary>
public sealed class CanvasGroupTests : IDisposable
{
    private readonly SceneTree _tree;
    private readonly CanvasServer _server;
    private readonly Node2D _root = new() { Name = "Root" };

    public CanvasGroupTests()
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

    // Two 10×10 boxes overlapping by half, the open hand and the fist.
    private CanvasGroup Build()
    {
        var group = new CanvasGroup { Name = "Hand", Position = new Vector2(20, 10), FitMargin = 2 };
        group.AddChild(new ClipBox { Name = "Open" });
        group.AddChild(new ClipBox { Name = "Fist", Position = new Vector2(5, 0) });
        _root.AddChild(group);
        _root.AddChild(new ClipBox { Name = "Sibling", Position = new Vector2(100, 10) });
        return group;
    }

    [Fact]
    public void TheChildrenDrawOnTheGroupTargetAndTheGroupComposites()
    {
        var group = Build();
        group.SelfModulate = new Vector4(1, 1, 1, 0.5f);
        var frame = Frame();

        Assert.Equal(2, frame.Passes.Count);
        var pass = frame.Passes[0];
        Assert.Same(group, pass.ClipOwner);
        Assert.Equal(Vector4.Zero, pass.ClearColor);
        Assert.Equal(CanvasBlendMode.Mix, frame.Batches[pass.FirstBatch].Blend); // plain mix: no clip (Atop) in a canvas group
        Assert.Equal(1, pass.BatchCount);                                          // both boxes in one batch
        var open = frame.Vertices[(int)frame.Indices[frame.Batches[pass.FirstBatch].FirstIndex]];
        Assert.Equal(Vector4.One, open.Color); // self modulate stays off the children

        var main = frame.Passes[1];
        Assert.Equal(2, main.BatchCount);
        var composite = frame.Batches[main.FirstBatch];
        Assert.Equal(CanvasBlendMode.PremultAlpha, composite.Blend);
        Assert.Same(group, composite.Texture!.ClipGroup);
        var first = (int)frame.Indices[composite.FirstIndex];
        // The children cover (20, 10)–(35, 20); the fit margin grows that by 2 on every side.
        Assert.Equal(new Vector2(18, 8), frame.Vertices[first].Position);
        Assert.Equal(new Vector2(37, 22), frame.Vertices[first + 2].Position);
        Assert.Equal(new Vector2(18f / 200f, 8f / 100f), frame.Vertices[first].Uv);
        Assert.Equal(new Vector4(0.5f, 0.5f, 0.5f, 0.5f), frame.Vertices[first].Color); // premultiplied for the premultiplied blend
    }

    [Fact]
    public void ModulateReachesTheChildrenAndTheCompositeAsInGodot()
    {
        var group = Build();
        group.Modulate = new Vector4(1, 1, 1, 0.5f);
        var frame = Frame();

        var pass = frame.Passes[0];
        Assert.Equal(0.5f, frame.Vertices[(int)frame.Indices[frame.Batches[pass.FirstBatch].FirstIndex]].Color.W);
        var composite = frame.Batches[frame.Passes[1].FirstBatch];
        Assert.Equal(0.5f, frame.Vertices[(int)frame.Indices[composite.FirstIndex]].Color.W);
    }

    [Fact]
    public void AnEmptyGroupDrawsNoComposite()
    {
        var group = Build();
        Frame();
        foreach (var child in group.Children.ToList())
            child.Free();

        var frame = Frame();

        Assert.Single(frame.Passes);
        Assert.Equal(1, frame.Passes[0].BatchCount); // the sibling only
    }

    [Fact]
    public void ANestedGroupDrawsUngroupedInItsAncestorsGroup()
    {
        var outer = Build();
        var inner = new CanvasGroup { Name = "Inner" };
        inner.AddChild(new ClipBox { Name = "Deep", Position = new Vector2(0, 5) });
        outer.AddChild(inner);

        var frame = Frame();

        Assert.Equal(2, frame.Passes.Count); // one group pass: the outer group's
        Assert.Same(outer, frame.Passes[0].ClipOwner);
    }
}
