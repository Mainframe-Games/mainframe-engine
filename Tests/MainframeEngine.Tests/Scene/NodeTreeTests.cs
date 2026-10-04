namespace MainframeEngine.Tests.Scene;

public sealed class NodeTreeTests
{
    [Fact]
    public void AddChildSetsParentIndexAndOrder()
    {
        using var root = new PlainNode { Name = "Root" };
        var a = new PlainNode { Name = "A" };
        var b = new PlainNode { Name = "B" };
        root.AddChild(a);
        root.AddChild(b);

        Assert.Same(root, a.Parent);
        Assert.Equal([a, b], root.Children);
        Assert.Equal(0, a.GetIndex());
        Assert.Equal(1, b.GetIndex());
        Assert.Same(b, root.GetChild(1));
        Assert.True(root.IsAncestorOf(b));
        Assert.False(b.IsAncestorOf(root));
    }

    [Fact]
    public void DuplicateAndEmptyNamesAreMadeUnique()
    {
        using var root = new PlainNode { Name = "Root" };
        var first = new PlainNode { Name = "Box" };
        var second = new PlainNode { Name = "Box" };
        var third = new PlainNode { Name = "Box" };
        var unnamed = new LoggingNode();
        root.AddChild(first);
        root.AddChild(second);
        root.AddChild(third);
        root.AddChild(unnamed);

        Assert.Equal("Box", first.Name);
        Assert.Equal("Box2", second.Name);
        Assert.Equal("Box3", third.Name);
        Assert.Equal("LoggingNode", unnamed.Name);

        // Renaming into a collision also gets a number (the first free one; its own current name counts as free).
        third.Name = "Box";
        Assert.Equal("Box3", third.Name);
        unnamed.Name = "Box2";
        Assert.Equal("Box4", unnamed.Name);
    }

    [Fact]
    public void RenamingIntoALaterSiblingsNameStillDeduplicates()
    {
        using var small = new PlainNode();
        var first = new PlainNode { Name = "First" };
        small.AddChild(first);
        small.AddChild(new PlainNode { Name = "Taken" });
        first.Name = "Taken"; // the taken name belongs to a sibling *after* this node
        Assert.Equal("Taken2", first.Name);

        using var large = new PlainNode(); // with the name index (more than 8 children)
        for (var i = 0; i < 12; i++)
            large.AddChild(new PlainNode { Name = $"N{i}" });
        large.GetChild(0).Name = "N11";
        Assert.Equal("N12", large.GetChild(0).Name);
        Assert.Equal(12, large.Children.Select(c => c.Name).Distinct().Count());
    }

    [Fact]
    public void NamesWithPathCharactersAreSanitized()
    {
        var node = new PlainNode { Name = "a/b:c@d%e\"f" };
        Assert.Equal("a_b_c_d_e_f", node.Name);
        node.Name = "..";
        Assert.Equal("_", node.Name);
        node.Free();
    }

    [Fact]
    public void ManyChildrenUseTheNameIndex()
    {
        using var root = new PlainNode { Name = "Root" };
        for (var i = 0; i < 100; i++)
            root.AddChild(new PlainNode { Name = $"Child{i}" });

        Assert.Equal("Child57", root.GetNode("Child57").Name);
        var renamed = root.GetChild(10);
        renamed.Name = "Renamed";
        Assert.Same(renamed, root.GetNodeOrNull("Renamed"));
        Assert.Null(root.GetNodeOrNull("Child10"));

        root.RemoveChild(renamed);
        Assert.Null(root.GetNodeOrNull("Renamed"));
        renamed.Free();
    }

    [Fact]
    public void AddChildRejectsSecondParentsAndCycles()
    {
        using var root = new PlainNode { Name = "Root" };
        var child = new PlainNode { Name = "Child" };
        root.AddChild(child);

        using var other = new PlainNode();
        Assert.Throws<InvalidOperationException>(() => other.AddChild(child));
        Assert.Throws<InvalidOperationException>(() => child.AddChild(root));
        Assert.Throws<InvalidOperationException>(() => root.AddChild(root));
    }

    [Fact]
    public void MoveChildReordersAndReindexes()
    {
        using var root = new PlainNode();
        var a = new PlainNode { Name = "A" };
        var b = new PlainNode { Name = "B" };
        var c = new PlainNode { Name = "C" };
        root.AddChild(a);
        root.AddChild(b);
        root.AddChild(c);

        root.MoveChild(c, 0);
        Assert.Equal([c, a, b], root.Children);
        Assert.Equal([0, 1, 2], root.Children.Select(n => n.GetIndex()));

        root.MoveChild(c, -1);
        Assert.Equal([a, b, c], root.Children);
    }

    [Fact]
    public void AddSiblingInsertsAfterThisNode()
    {
        using var root = new PlainNode();
        var a = new PlainNode { Name = "A" };
        var c = new PlainNode { Name = "C" };
        root.AddChild(a);
        root.AddChild(c);
        var b = new PlainNode { Name = "B" };
        a.AddSibling(b);
        Assert.Equal([a, b, c], root.Children);
    }

    [Fact]
    public void OwnerMustBeAnAncestorAndIsClearedWhenItNoLongerIs()
    {
        using var root = new PlainNode { Name = "Root" };
        var child = new PlainNode { Name = "Child" };
        var grandchild = new PlainNode { Name = "Grandchild" };
        root.AddChild(child);
        child.AddChild(grandchild);
        child.Owner = root;
        grandchild.Owner = root;

        using var stranger = new PlainNode();
        Assert.Throws<InvalidOperationException>(() => grandchild.Owner = stranger);

        root.RemoveChild(child);
        Assert.Null(child.Owner);
        Assert.Null(grandchild.Owner);
        child.Free();
    }

    [Fact]
    public void ReparentKeepsOwnersThatStayValid()
    {
        using var root = new PlainNode { Name = "Root" };
        var a = new PlainNode { Name = "A" };
        var b = new PlainNode { Name = "B" };
        var moved = new PlainNode { Name = "Moved" };
        root.AddChild(a);
        root.AddChild(b);
        a.AddChild(moved);
        moved.Owner = root;

        moved.Reparent(b);

        Assert.Same(b, moved.Parent);
        Assert.Same(root, moved.Owner);
    }

    [Fact]
    public void GetNodeResolvesRelativeParentAndCurrentSegments()
    {
        using var root = new PlainNode { Name = "Main" };
        var player = new PlainNode { Name = "Player" };
        var camera = new PlainNode { Name = "Camera" };
        var enemy = new LoggingNode { Name = "Enemy" };
        root.AddChild(player);
        player.AddChild(camera);
        root.AddChild(enemy);

        Assert.Same(camera, root.GetNode<PlainNode>("Player/Camera"));
        Assert.Same(enemy, camera.GetNode<LoggingNode>("../../Enemy"));
        Assert.Same(player, camera.GetNode("..").GetNode("."));
        Assert.Same(camera, player.GetNode("./Camera"));
        Assert.Null(root.GetNodeOrNull("Player/Missing"));
        Assert.Null(root.GetNodeOrNull<LoggingNode>("Player"));
        Assert.True(root.HasNode("Player/Camera"));
        Assert.False(root.HasNode(""));

        Assert.Throws<InvalidOperationException>(() => root.GetNode("Nope"));
        Assert.Throws<InvalidCastException>(() => root.GetNode<LoggingNode>("Player"));
    }

    [Fact]
    public void AbsolutePathsResolveFromTheTreeRoot()
    {
        var tree = new SceneTree();
        var main = new PlainNode { Name = "Main" };
        var child = new PlainNode { Name = "Child" };
        main.AddChild(child);
        tree.Root.AddChild(main);

        Assert.Same(child, main.GetNode("/root/Main/Child"));
        Assert.Same(tree.Root, child.GetNode("/root"));
        Assert.Null(child.GetNodeOrNull("/elsewhere/Main"));
        Assert.Equal("/root/Main/Child", child.GetPath().Path);
        Assert.True(child.GetPath().IsAbsolute);

        // Outside a tree absolute paths do not resolve.
        using var loose = new PlainNode();
        Assert.Null(loose.GetNodeOrNull("/root"));
        tree.Shutdown();
    }

    [Fact]
    public void GetPathToBuildsRelativePaths()
    {
        using var root = new PlainNode { Name = "Main" };
        var a = new PlainNode { Name = "A" };
        var a1 = new PlainNode { Name = "A1" };
        var b = new PlainNode { Name = "B" };
        var b1 = new PlainNode { Name = "B1" };
        root.AddChild(a);
        a.AddChild(a1);
        root.AddChild(b);
        b.AddChild(b1);

        Assert.Equal("../../B/B1", a1.GetPathTo(b1).Path);
        Assert.Equal("A/A1", root.GetPathTo(a1).Path);
        Assert.Equal("..", a1.GetPathTo(a).Path);
        Assert.Equal(".", a.GetPathTo(a).Path);
        Assert.Same(b1, a1.GetNode(a1.GetPathTo(b1)));

        using var stranger = new PlainNode();
        Assert.Throws<InvalidOperationException>(() => stranger.GetPathTo(a));
    }

    [Fact]
    public void UniqueNamesResolveWithinTheOwner()
    {
        using var root = new PlainNode { Name = "Main" };
        var deep = new PlainNode { Name = "Deep" };
        var hud = new PlainNode { Name = "Hud" };
        var label = new PlainNode { Name = "Score" };
        root.AddChild(deep);
        deep.AddChild(hud);
        hud.AddChild(label);
        foreach (var n in new[] { deep, hud, label })
            n.Owner = root;

        label.UniqueNameInOwner = true;

        Assert.Same(label, root.GetNode("%Score"));   // the owner itself
        Assert.Same(label, deep.GetNode("%Score"));   // a node sharing the owner
        Assert.Null(root.GetNodeOrNull("%Missing"));

        label.Name = "Points";
        Assert.Same(label, root.GetNode("%Points"));
        Assert.Null(root.GetNodeOrNull("%Score"));

        var duplicate = new PlainNode { Name = "Other" };
        hud.AddChild(duplicate);
        duplicate.Owner = root;
        duplicate.Name = "Points2";
        label.UniqueNameInOwner = false;
        Assert.Null(root.GetNodeOrNull("%Points"));
    }

    [Fact]
    public void FindChildMatchesWildcardsDepthFirst()
    {
        using var root = new PlainNode { Name = "Main" };
        var enemies = new PlainNode { Name = "Enemies" };
        var orc = new LoggingNode { Name = "Orc1" };
        var goblin = new LoggingNode { Name = "Goblin" };
        root.AddChild(enemies);
        enemies.AddChild(orc);
        enemies.AddChild(goblin);
        foreach (var n in new Node[] { enemies, orc, goblin })
            n.Owner = root;

        Assert.Same(orc, root.FindChild("Or?1"));
        Assert.Same(goblin, root.FindChild("*lin"));
        Assert.Null(root.FindChild("Orc*", recursive: false));
        Assert.Equal([orc, goblin], root.FindChildren<LoggingNode>());
        Assert.Equal(2, root.FindChildren<Node>("*", recursive: true).Count(n => n is LoggingNode));

        // owned: only nodes sharing the owner (runtime-spawned nodes are skipped).
        var spawned = new LoggingNode { Name = "Spawned" };
        enemies.AddChild(spawned);
        Assert.Null(root.FindChild("Spawned"));
        Assert.Same(spawned, root.FindChild("Spawned", owned: false));
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "a*", true)]
    [InlineData("abc", "*c", true)]
    [InlineData("abc", "a?c", true)]
    [InlineData("abc", "*", true)]
    [InlineData("", "*", true)]
    [InlineData("abc", "a*d", false)]
    [InlineData("abc", "ab", false)]
    [InlineData("aXbXc", "a*b*c", true)]
    public void WildcardMatching(string text, string pattern, bool expected) =>
        Assert.Equal(expected, Node.WildcardMatch(text, pattern));

    [Fact]
    public void FreeDetachesFreesChildrenAndInvalidates()
    {
        var root = new PlainNode { Name = "Root" };
        var child = new PlainNode { Name = "Child" };
        var grandchild = new PlainNode { Name = "Grandchild" };
        root.AddChild(child);
        child.AddChild(grandchild);

        child.Free();

        Assert.Empty(root.Children);
        Assert.True(child.IsFreed);
        Assert.True(grandchild.IsFreed);
        Assert.False(Node.IsInstanceValid(child));
        Assert.True(Node.IsInstanceValid(root));
        Assert.False(Node.IsInstanceValid(null));
        Assert.Throws<ObjectDisposedException>(() => child.AddChild(new PlainNode()));

        child.Free(); // idempotent
        root.Dispose();
        Assert.True(root.IsFreed);
    }

    [Fact]
    public void QueueFreeOutsideATreeFreesImmediately()
    {
        var node = new PlainNode();
        node.QueueFree();
        Assert.True(node.IsFreed);
    }

    [Fact]
    public void NodePathEqualityAndParsing()
    {
        NodePath a = "../Player/Camera";
        var b = new NodePath("../Player/Camera");
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a.IsAbsolute);
        Assert.Equal(["..", "Player", "Camera"], a.GetNames());
        Assert.True(NodePath.Empty.IsEmpty);
        Assert.Equal(string.Empty, default(NodePath).Path);
        Assert.Equal("../Player/Camera", a.ToString());
    }

    [Fact]
    public void NodesReceiveDistinctIds()
    {
        using var a = new PlainNode();
        using var b = new PlainNode();
        Assert.NotEqual(a.Id, b.Id);
    }
}
