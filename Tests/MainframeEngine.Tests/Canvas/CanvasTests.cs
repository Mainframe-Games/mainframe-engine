using System.Numerics;

namespace MainframeEngine.Tests.Canvas;

/// <summary>
/// The canvas (ADR 0111): Godot's draw order (tree order, show-behind-parent, z-index relative/absolute, y-sort with
/// index ties, top level), modulate inheritance, culling, deferred redraws and the frame the canvas server builds.
/// </summary>
public sealed class CanvasTests : IDisposable
{
    private readonly SceneTree _tree;
    private readonly CanvasServer _server;
    private readonly Node2D _root = new() { Name = "Root" };

    public CanvasTests()
    {
        _tree = new SceneTree(new ServerRegistry());
        _server = new CanvasServer(_tree, () => new Vector2(1000, 1000));
        _tree.ChangeScene(_root);
    }

    public void Dispose()
    {
        _server.Dispose();
        _tree.Shutdown();
    }

    private sealed class Box : Node2D
    {
        public Vector2 Size { get; set; } = new(10, 10);

        protected override void OnDraw() => DrawRect(new Rect2(Vector2.Zero, Size), Vector4.One);
    }

    private static Box AddBox(Node parent, string name, Vector2 position = default, int z = 0)
    {
        var box = new Box { Name = name, Position = position, ZIndex = z };
        parent.AddChild(box);
        return box;
    }

    private List<string> Order()
    {
        _server.Process(new GameTime { DeltaTime = 1f / 60f });
        var culled = new List<CulledCanvasItem>();
        new CanvasCuller().Cull(_tree.Root.RootCanvas, Transform2D.Identity, new Rect2(-10000, -10000, 20000, 20000), culled);
        return culled.Select(c => c.Item.Name).ToList();
    }

    [Fact]
    public void ChildrenDrawAfterTheirParentInTreeOrderUnlessBehind()
    {
        var a = AddBox(_root, "A");
        AddBox(a, "A1");
        AddBox(a, "A2").ShowBehindParent = true;
        AddBox(_root, "B");
        Assert.Equal(["A2", "A", "A1", "B"], Order());
    }

    [Fact]
    public void ZIndexIsRelativeToTheParentUnlessAbsolute()
    {
        var a = AddBox(_root, "A", z: 2);
        AddBox(a, "A1", z: -1);                 // 1
        var abs = AddBox(a, "Abs", z: 0);      // absolute 0
        abs.ZAsRelative = false;
        AddBox(_root, "B", z: 1);
        Assert.Equal(["Abs", "A1", "B", "A"], Order());
    }

    [Fact]
    public void YSortOrdersBySortRootRelativeYWithTreeOrderTies()
    {
        var sorter = new Node2D { Name = "Sorter", YSortEnabled = true, Position = new Vector2(0, 100) };
        _root.AddChild(sorter);
        AddBox(sorter, "Low", new Vector2(0, 50));
        AddBox(sorter, "High", new Vector2(0, -20));
        AddBox(sorter, "TieFirst", new Vector2(5, 10));
        AddBox(sorter, "TieSecond", new Vector2(9, 10));
        var nested = new Node2D { Name = "Nested", YSortEnabled = true, Position = new Vector2(0, 30) };
        sorter.AddChild(nested);
        AddBox(nested, "NestedAt0", new Vector2(0, -40)); // 30 - 40 = -10 relative to Sorter
        Assert.Equal(["High", "NestedAt0", "TieFirst", "TieSecond", "Low"], Order());
    }

    [Fact]
    public void ModulateInheritsSelfModulateDoesNotAndTransparentSubtreesAreSkipped()
    {
        var parent = AddBox(_root, "Parent");
        parent.Modulate = new Vector4(0.5f, 1, 1, 1);
        parent.SelfModulate = new Vector4(1, 0.5f, 1, 1);
        AddBox(parent, "Child");
        var hidden = AddBox(_root, "Faded");
        hidden.Modulate = new Vector4(1, 1, 1, 0.001f);
        AddBox(hidden, "UnderFaded");

        _server.Process(new GameTime { DeltaTime = 1f / 60f });
        var culled = new List<CulledCanvasItem>();
        new CanvasCuller().Cull(_tree.Root.RootCanvas, Transform2D.Identity, new Rect2(0, 0, 100, 100), culled);
        Assert.Equal(["Parent", "Child"], culled.Select(c => c.Item.Name));
        Assert.Equal(new Vector4(0.5f, 0.5f, 1, 1), culled[0].Modulate);
        Assert.Equal(new Vector4(0.5f, 1, 1, 1), culled[1].Modulate);
    }

    [Fact]
    public void ItemsOutsideTheViewAreCulledButTheirChildrenAreNot()
    {
        var far = AddBox(_root, "Far", new Vector2(5000, 0));
        AddBox(far, "Back", new Vector2(-5000, 0));
        _server.Process(new GameTime { DeltaTime = 1f / 60f });
        var culled = new List<CulledCanvasItem>();
        new CanvasCuller().Cull(_tree.Root.RootCanvas, Transform2D.Identity, new Rect2(0, 0, 100, 100), culled);
        Assert.Equal(["Back"], culled.Select(c => c.Item.Name));
    }

    [Fact]
    public void TopLevelItemsUseTheirOwnTransformAndDrawAsCanvasRoots()
    {
        var parent = AddBox(_root, "Parent", new Vector2(100, 100));
        var top = AddBox(parent, "Top", new Vector2(10, 10));
        AddBox(_root, "Sibling");
        top.TopLevel = true;
        Assert.Equal(new Vector2(10, 10), top.GlobalPosition);
        var order = Order();
        Assert.Equal(["Parent", "Sibling", "Top"], order);

        parent.Visible = false;
        Assert.DoesNotContain("Top", Order());
    }

    [Fact]
    public void RedrawsRunAtTheEndOfTheFrameOnlyWhenQueued()
    {
        var counter = new CountingItem { Name = "Counter" };
        _root.AddChild(counter);
        Assert.Equal(0, counter.Draws);
        _server.Process(new GameTime { DeltaTime = 1f / 60f });
        Assert.Equal(1, counter.Draws);
        _server.Process(new GameTime { DeltaTime = 1f / 60f });
        Assert.Equal(1, counter.Draws);
        counter.QueueRedraw();
        counter.QueueRedraw();
        _server.Process(new GameTime { DeltaTime = 1f / 60f });
        Assert.Equal(2, counter.Draws);
        Assert.Throws<InvalidOperationException>(() => counter.DrawRect(new Rect2(0, 0, 1, 1), Vector4.One));
    }

    private sealed class CountingItem : Node2D
    {
        public int Draws { get; private set; }

        protected override void OnDraw()
        {
            Draws++;
            DrawCircle(Vector2.Zero, 4, Vector4.One);
        }
    }

    [Fact]
    public void TheFrameIsInTargetPixelsWithLayersAboveTheRootCanvasAndTheirOwnModulate()
    {
        var box = AddBox(_root, "World", new Vector2(10, 20));
        _root.AddChild(new CanvasModulate { Name = "Tint", Color = new Vector4(0.5f, 0.5f, 0.5f, 1) });
        var layer = new CanvasLayer { Name = "Hud", Layer = 1, Offset = new Vector2(100, 0) };
        _root.AddChild(layer);
        AddBox(layer, "HudBox");
        _tree.Root.CanvasTransform = Transform2D.FromTrs(new Vector2(1, 2), 0, new Vector2(2));

        _server.Process(new GameTime { DeltaTime = 1f / 60f });
        var frame = _server.Frame;
        Assert.Equal(2, frame.Batches.Count); // different canvas modulate breaks the batch
        Assert.Equal(new Vector4(0.5f, 0.5f, 0.5f, 1), frame.Batches[0].CanvasModulate);
        Assert.Equal(Vector4.One, frame.Batches[1].CanvasModulate);
        // World box corner (0,0) → canvas (10,20) → ×2 + (1,2).
        Assert.Equal(new Vector2(21, 42), frame.Vertices[(int)frame.Indices[frame.Batches[0].FirstIndex]].Position);
        Assert.Equal(new Vector2(100, 0), frame.Vertices[(int)frame.Indices[frame.Batches[1].FirstIndex]].Position);
        Assert.NotNull(box);
    }
}
