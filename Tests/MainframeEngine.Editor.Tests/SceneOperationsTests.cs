using System.Numerics;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor.Tests;

[Collection(nameof(SerialEditor))]
public sealed class SceneOperationsTests : IDisposable
{
    private readonly SessionHost _host = new();

    public void Dispose() => _host.Dispose();

    private static readonly string[] CopiedNames = ["Inner", "Sender", "Receiver"];

    private EditedScene NewScene() => _host.Session.NewScene();

    private static Node3D Add(EditedScene scene, string name, Node? parent = null, Vector3 position = default)
    {
        var node = new Node3D { Name = name, Position = position };
        scene.AddNode(node, parent ?? scene.Root);
        return node;
    }

    [Fact]
    public void NewScenesLiveInTheirOwnSubViewportUnderTheHost()
    {
        var a = NewScene();
        var b = NewScene();

        Assert.IsType<SubViewport>(a.Root.Parent);
        Assert.NotSame(a.Viewport, b.Viewport);
        Assert.Same(_host.Host, a.Viewport.Parent);
        Assert.Same(b, _host.Session.Active);
        Assert.Equal(SubViewportUpdateMode.Always, b.Viewport.UpdateMode);
        Assert.Equal(SubViewportUpdateMode.Disabled, a.Viewport.UpdateMode); // only the active tab renders
        Assert.Equal("Untitled", a.DisplayName);
        Assert.Equal("Untitled 2", b.DisplayName);
        Assert.Null(a.Root.Owner);
    }

    [Fact]
    public void AddingANodeOwnsItSelectsItAndUndoDetachesIt()
    {
        var scene = NewScene();
        var node = Add(scene, "Child");

        Assert.Same(scene.Root, node.Owner);
        Assert.Same(node, scene.Selection.Primary);
        Assert.True(scene.IsDirty);
        Assert.Equal("Untitled*", scene.Title);

        scene.History.Undo();
        Assert.Null(node.Parent);
        Assert.Empty(scene.Selection.Nodes); // pruned
        Assert.False(scene.IsDirty);

        scene.History.Redo();
        Assert.Same(scene.Root, node.Parent);
        Assert.Same(scene.Root, node.Owner);

        scene.History.Undo();
        scene.History.Commit(new RenameAction(scene.Root, "Other")); // cuts the branch: the detached node is freed
        Assert.True(node.IsFreed);
    }

    [Fact]
    public void DeleteUndoRestoresTheSubtreeItsOwnersIndexAndConnections()
    {
        var scene = NewScene();
        var first = Add(scene, "First");
        var middle = new SignalNode { Name = "Middle" };
        scene.AddNode(middle, scene.Root);
        Add(scene, "Last");
        var child = Add(scene, "Child", middle);
        var grandchild = Add(scene, "Grandchild", child);
        var listener = new SignalNode { Name = "Listener" };
        scene.AddNode(listener, scene.Root);
        middle.Connect(nameof(SignalNode.Pinged), listener, nameof(SignalNode.OnPinged), ConnectFlags.Persist);

        Assert.True(scene.Delete([middle]));
        Assert.Null(middle.Parent);
        Assert.Equal(["First", "Last", "Listener"], scene.Root.Children.Select(c => c.Name));

        scene.History.Undo();
        Assert.Equal(1, middle.GetIndex());
        Assert.Same(scene.Root, middle.Owner);
        Assert.Same(scene.Root, child.Owner);
        Assert.Same(scene.Root, grandchild.Owner);
        Assert.Same(grandchild, scene.Root.GetNode("Middle/Child/Grandchild"));
        middle.Ping();
        Assert.Equal(1, listener.Received);

        // And it saves exactly as before the delete (owners and the persisted connection intact).
        var json = System.Text.Encoding.UTF8.GetString(SceneSaver.ToJson(scene.Root));
        Assert.Contains("\"Grandchild\"", json, StringComparison.Ordinal);
        Assert.Contains("\"OnPinged\"", json, StringComparison.Ordinal);
        Assert.False(first.IsFreed);
    }

    [Fact]
    public void DeletingANodeAndItsDescendantDeletesOnceAndTheRootIsNeverDeleted()
    {
        var scene = NewScene();
        var parent = Add(scene, "Parent");
        var child = Add(scene, "Child", parent);

        Assert.False(scene.Delete([scene.Root]));
        Assert.True(scene.Delete([child, parent, scene.Root]));
        Assert.IsType<RemoveNodeAction>(scene.History.Actions[^1]); // the child is covered by its parent
        Assert.Null(parent.Parent);
        Assert.Same(parent, child.Parent);
    }

    [Fact]
    public void DeletedSubtreesAreFreedWhenTheirActionLeavesTheHistory()
    {
        var scene = NewScene();
        var node = Add(scene, "Doomed");
        scene.Delete([node]);
        scene.History.Clear();
        Assert.True(node.IsFreed);
    }

    [Fact]
    public void ReparentKeepsTheGlobalTransformAndUndoRestoresTheLocalOneExactly()
    {
        var scene = NewScene();
        var a = Add(scene, "A", position: new Vector3(10, 0, 0));
        a.RotationDegrees = new Vector3(0, 90, 0);
        var b = Add(scene, "B", position: new Vector3(1, 2, 3));
        b.RotationDegrees = new Vector3(15, 0, 0);
        var global = b.GlobalPosition;

        Assert.True(scene.Reparent(b, a));
        Assert.Same(a, b.Parent);
        Assert.True(Vector3.Distance(global, b.GlobalPosition) < 1e-4f);
        Assert.Same(scene.Root, b.Owner);

        scene.History.Undo();
        Assert.Same(scene.Root, b.Parent);
        Assert.Equal(1, b.GetIndex());
        Assert.Equal(new Vector3(1, 2, 3), b.Position);
        Assert.Equal(new Vector3(15, 0, 0), b.RotationDegrees);

        Assert.True(scene.Reparent(b, a, keepGlobalTransform: false));
        Assert.Equal(new Vector3(1, 2, 3), b.Position); // local kept, so it moved in the world
    }

    [Fact]
    public void ReparentRejectsCyclesTheRootAndNoOps()
    {
        var scene = NewScene();
        var a = Add(scene, "A");
        var b = Add(scene, "B", a);

        Assert.False(scene.Reparent(a, b));
        Assert.False(scene.Reparent(scene.Root, a));
        Assert.False(scene.Reparent(b, a)); // already the last child of A
        Assert.Throws<ArgumentException>(() => new ReparentAction(a, b));
    }

    [Fact]
    public void ReorderingWithinTheParentInsertsBeforeTheTarget()
    {
        var scene = NewScene();
        var a = Add(scene, "A");
        Add(scene, "B");
        Add(scene, "C");

        Assert.True(scene.Reparent(a, scene.Root, index: 3)); // after C
        Assert.Equal(["B", "C", "A"], scene.Root.Children.Select(c => c.Name));
        scene.History.Undo();
        Assert.Equal(["A", "B", "C"], scene.Root.Children.Select(c => c.Name));

        Assert.True(scene.Move(a, 1));
        Assert.Equal(["B", "A", "C"], scene.Root.Children.Select(c => c.Name));
        Assert.False(scene.Move(scene.Root, 1));
    }

    [Fact]
    public void DuplicateCopiesTheSubtreeWithUniqueNamesNextToTheOriginal()
    {
        var scene = NewScene();
        var box = Add(scene, "Box", position: new Vector3(1, 2, 3));
        var inner = Add(scene, "Inner", box);
        var sender = new SignalNode { Name = "Sender" };
        scene.AddNode(sender, box);
        var receiver = new SignalNode { Name = "Receiver" };
        scene.AddNode(receiver, box);
        sender.Connect(nameof(SignalNode.Pinged), receiver, nameof(SignalNode.OnPinged), ConnectFlags.Persist);
        Add(scene, "After");

        var copies = scene.Duplicate([box]);

        var copy = Assert.IsType<Node3D>(Assert.Single(copies));
        Assert.Equal("Box2", copy.Name);
        Assert.Equal(1, copy.GetIndex());
        Assert.Equal(new Vector3(1, 2, 3), copy.Position);
        Assert.Same(scene.Root, copy.Owner);
        Assert.All(CopiedNames, name => Assert.Same(scene.Root, copy.GetNode(name).Owner));
        Assert.NotSame(inner, copy.GetNode("Inner"));
        Assert.Same(copy, scene.Selection.Primary);

        // The connection inside the copy is the copy's own, and it is saved with the scene.
        copy.GetNode<SignalNode>("Sender").Ping();
        Assert.Equal(1, copy.GetNode<SignalNode>("Receiver").Received);
        Assert.Equal(0, receiver.Received);
        var json = System.Text.Encoding.UTF8.GetString(SceneSaver.ToJson(scene.Root));
        Assert.Contains("\"from\": \"Box2/Sender\"", json, StringComparison.Ordinal);

        var again = scene.Duplicate([box]);
        Assert.Equal("Box3", again[0].Name);

        scene.History.Undo();
        scene.History.Undo();
        Assert.Equal(["Box", "After"], scene.Root.Children.Select(c => c.Name));
    }

    [Fact]
    public void DuplicatingSeveralSiblingsPutsEachCopyRightAfterItsOriginal()
    {
        var scene = NewScene();
        var a = Add(scene, "A");
        var b = Add(scene, "B");
        Add(scene, "C");

        scene.Duplicate([a, b]);
        Assert.Equal(["A", "A2", "B", "B2", "C"], scene.Root.Children.Select(c => c.Name));

        scene.History.Undo();
        scene.History.Redo();
        Assert.Equal(["A", "A2", "B", "B2", "C"], scene.Root.Children.Select(c => c.Name));
    }

    [Fact]
    public void SavingIntoAnotherProjectIsRefusedWhileScenesOfTheCurrentOneAreOpen()
    {
        var first = NewScene();
        _host.Session.Save(first, _host.ScenePath("First.mscene"));
        var other = Directory.CreateTempSubdirectory("mf-other-project").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(other, "Content"));
            var second = NewScene();
            Add(second, "X");

            var error = Assert.Throws<InvalidOperationException>(() => _host.Session.Save(second, Path.Combine(other, "Content", "S.mscene")));
            Assert.Contains("other scenes", error.Message, StringComparison.Ordinal);
            Assert.True(second.IsDirty);
            Assert.Equal(_host.ProjectDirectory, _host.Session.ProjectRoot);
            Assert.Equal(_host.ProjectDirectory, AssetDatabase.Current.ProjectRoot);

            // Alone, a scene may move the session to another project.
            _host.Session.Close(first);
            _host.Session.Save(second, Path.Combine(other, "Content", "S.mscene"));
            Assert.Equal(other, _host.Session.ProjectRoot);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void DuplicatingAnInstancedSceneKeepsItAnInstance()
    {
        var subPath = _host.ScenePath("Sub.mscene");
        var sub = new Node3D { Name = "Sub" };
        var part = new Node3D { Name = "Part" };
        sub.AddChild(part);
        part.Owner = sub;
        _host.Session.UseProjectOf(subPath);
        SceneSaver.Save(sub, subPath);
        sub.Free();

        var scene = NewScene();
        var instance = scene.InstanceScene(subPath);
        Assert.NotNull(instance.SceneFilePath);
        Assert.Same(instance, instance.GetNode("Part").Owner); // inner nodes belong to the instance
        Assert.False(scene.IsEditable(instance.GetNode("Part")));
        Assert.Same(instance, scene.SelectableFor(instance.GetNode("Part")));

        var copy = scene.Duplicate([instance])[0];
        Assert.Equal(instance.SceneFilePath, copy.SceneFilePath);
        Assert.Same(copy, copy.GetNode("Part").Owner);
        Assert.Same(scene.Root, copy.Owner);

        var json = System.Text.Encoding.UTF8.GetString(SceneSaver.ToJson(scene.Root));
        Assert.Equal(2, json.Split("\"instance\":").Length - 1);
    }

    [Fact]
    public void RenameIsUndoableAndKeepsNamesUnique()
    {
        var scene = NewScene();
        var a = Add(scene, "A");
        Add(scene, "B");

        Assert.True(scene.Rename(a, " B "));
        Assert.Equal("B2", a.Name);
        Assert.False(scene.Rename(a, "  "));
        scene.History.Undo();
        Assert.Equal("A", a.Name);
    }

    [Fact]
    public void SetPropertyMergesContinuousEditsByKey()
    {
        var scene = NewScene();
        var node = Add(scene, "N");
        var position = TypeRegistry.GetRequired(typeof(Node3D)).FindProperty("Position")!;

        scene.SetProperty(node, position, new Vector3(1, 0, 0), "drag");
        scene.SetProperty(node, position, new Vector3(2, 0, 0), "drag");
        scene.History.EndMerge();
        scene.SetProperty(node, position, new Vector3(2, 0, 0)); // unchanged: no entry

        Assert.Equal(2, scene.History.Actions.Count);
        scene.History.Undo();
        Assert.Equal(Vector3.Zero, node.Position);
    }

    [Fact]
    public void SavingWritesAtomicallyMarksCleanAndReloadsIdentically()
    {
        var scene = NewScene();
        Add(scene, "Child", position: new Vector3(0, 1, 0));
        var path = _host.ScenePath("Saved.mscene");

        _host.Session.Save(scene, path);

        Assert.False(scene.IsDirty);
        Assert.Equal("Saved.mscene", scene.Title);
        Assert.Equal(_host.ProjectDirectory, _host.Session.ProjectRoot);
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));

        var saved = File.ReadAllBytes(path);
        var reopened = PackedScene.Parse(saved, path).Instantiate();
        Assert.Equal(new Vector3(0, 1, 0), reopened.GetNode<Node3D>("Child").Position);
        var uid = System.Text.Json.JsonDocument.Parse(saved).RootElement.GetProperty("uid").GetString();
        Assert.Equal(saved, SceneSaver.ToJson(reopened, uid));
        reopened.Free();

        // Opening the same file activates its tab; closing frees the world.
        Assert.Same(scene, _host.Session.Open(path));
        var viewport = scene.Viewport;
        _host.Session.Close(scene);
        Assert.True(viewport.IsFreed);
        Assert.Empty(_host.Session.Scenes);
        Assert.Null(_host.Session.Active);
    }

    [Fact]
    public void AFailedSaveLeavesThePreviousFileAndTheSceneDirty()
    {
        var scene = NewScene();
        var path = _host.ScenePath("Keep.mscene");
        _host.Session.Save(scene, path);
        var before = File.ReadAllBytes(path);
        Add(scene, "Unsaved");

        // A directory where the temp file would go makes the write fail.
        Directory.CreateDirectory(path + ".tmp");
        Assert.ThrowsAny<Exception>(() => _host.Session.Save(scene));
        Assert.True(scene.IsDirty);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void OpeningASceneUsesItsProjectFolderForResources()
    {
        var path = _host.ScenePath("Open.mscene");
        var root = new Node3D { Name = "Open" };
        _host.Session.UseProjectOf(path);
        SceneSaver.Save(root, path);
        root.Free();

        var scene = _host.Session.Open(path);

        Assert.Equal(_host.ProjectDirectory, _host.Session.ProjectRoot);
        Assert.Equal(_host.ProjectDirectory, ContentPaths.ProjectDirectory);
        Assert.Equal(_host.ProjectDirectory, AssetDatabase.Current.ProjectRoot);
        Assert.Equal("Open", scene.Root.Name);
        Assert.False(scene.IsDirty);
        Assert.Equal(_host.ProjectDirectory, EditorSession.FindProjectRoot(path));
        Assert.Throws<FileNotFoundException>(() => _host.Session.Open(_host.ScenePath("Missing.mscene")));
    }

    [Fact]
    public void EditedScenesDoNotProcessOutsideToolNodes()
    {
        var scene = NewScene();
        var counter = new CountingNode { Name = "Counter" };
        scene.AddNode(counter, scene.Root);
        _host.Tick(3);
        Assert.Equal(0, counter.Frames);
    }

    private sealed class CountingNode : Node
    {
        public int Frames { get; private set; }

        protected override void OnProcess(in GameTime gameTime) => Frames++;
    }
}
