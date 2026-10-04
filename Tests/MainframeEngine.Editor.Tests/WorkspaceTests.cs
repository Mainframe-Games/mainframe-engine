using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests;

/// <summary>The whole editor UI headless: RmlUi documents on a headless UI server, no window or GPU.</summary>
public sealed class HeadlessEditor : IDisposable
{
    private readonly AssetDatabase _previousDatabase = AssetDatabase.Current;
    private readonly string? _previousProject = ContentPaths.ProjectDirectory;
    private readonly List<string> _rmlMessages = [];

    public HeadlessEditor(string? initialScene = null)
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("mf-editor-ui").FullName;
        Log.MessageLogged += OnLog;
        Server = new UiServer(options: new UiServerOptions { HotReload = false, HeadlessViewport = new Vector2(1600, 900) });
        Servers.Register(Server);
        Tree = new SceneTree(Servers) { EditMode = true };
        Workspace = new EditorWorkspace(Host, new EditorWorkspaceOptions
        {
            LayoutPath = Path.Combine(Directory, "layout.json"),
            InitialScene = initialScene,
            ShowSplash = false,
            RecoveryDirectory = Path.Combine(Directory, "recovery"),
        });
        Tree.Root.AddChild(Workspace);
        Tick(3);
    }

    public string Directory { get; }
    public HeadlessEditorHost Host { get; } = new();
    public ServerRegistry Servers { get; } = new();
    public UiServer Server { get; }
    public SceneTree Tree { get; }
    public EditorWorkspace Workspace { get; }
    public EditedScene Scene => Workspace.Session.Active!;

    /// <summary>RmlUi warnings and errors seen so far.</summary>
    public IReadOnlyList<string> RmlMessages
    {
        get
        {
            lock (_rmlMessages)
                return [.. _rmlMessages];
        }
    }

    private void OnLog(Log.Level level, string text)
    {
        if (level >= Log.Level.Warning && (text.Contains("[RmlUi]", StringComparison.Ordinal) || text.Contains("[UI]", StringComparison.Ordinal)))
            lock (_rmlMessages)
                _rmlMessages.Add(text);
    }

    public void Tick(int frames = 1)
    {
        for (var i = 0; i < frames; i++)
            Tree.Tick(new GameTime { DeltaTime = 1f / 60f });
    }

    public void Key(Key key, params Key[] modifiers)
    {
        foreach (var m in modifiers)
            Tree.PushInput(new InputEventKey { Key = m, Pressed = true });
        Tree.PushInput(new InputEventKey { Key = key, Pressed = true });
        Tree.PushInput(new InputEventKey { Key = key, Pressed = false });
        foreach (var m in modifiers)
            Tree.PushInput(new InputEventKey { Key = m, Pressed = false });
        Tick();
    }

    public void Dispose()
    {
        Log.MessageLogged -= OnLog;
        Tree.Shutdown();
        Servers.Dispose();
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousDatabase;
        ContentPaths.ProjectDirectory = _previousProject;
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

[Collection(nameof(SerialEditor))]
public sealed class WorkspaceTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    private Node3D AddChild(string name)
    {
        var node = new Node3D { Name = name };
        _editor.Scene.AddNode(node, _editor.Scene.Root);
        _editor.Tick();
        return node;
    }

    [Fact]
    public void EveryPanelLoadsWithoutRmlUiErrorsAndANewSceneIsOpen()
    {
        foreach (var document in W.PanelLayer.Documents)
            Assert.True(document.IsLoaded, document.Name);
        Assert.Equal(8, W.PanelLayer.Documents.Count);
        Assert.Empty(_editor.RmlMessages);

        Assert.Single(W.Session.Scenes);
        Assert.Equal("Node3D", _editor.Scene.Root.Name);
        Assert.Equal("Untitled — Mainframe Editor", _editor.Host.Title);
        Assert.Single(W.SceneTree.Model.Rows);
    }

    [Fact]
    public void ThePanelsFollowTheLayoutAndTheWindowSize()
    {
        Assert.Equal(W.Layout.Inspector, W.Inspector.Rect);
        _editor.Host.WindowSize = new Vector2(1200, 700);
        _editor.Tick();
        Assert.Equal(1200, W.Layout.Inspector.Right, 2);
        Assert.Equal(W.Layout.Inspector, W.Inspector.Rect);
    }

    [Fact]
    public void ClickingATreeRowSelectsTheNodeAndTheInspectorShowsIt()
    {
        var child = AddChild("Child");
        W.SceneTree.Press(0, 0, EditorModifiers.None);
        _editor.Tick();
        Assert.Same(_editor.Scene.Root, _editor.Scene.Selection.Primary);
        Assert.Same(_editor.Scene.Root, W.Inspector.Target);

        W.SceneTree.Press(1, 0, EditorModifiers.Command); // Ctrl/Cmd+click adds
        _editor.Tick();
        Assert.Equal(2, _editor.Scene.Selection.Count);
        Assert.Same(child, W.Inspector.Target);
        Assert.True(W.SceneTree.Model.Rows[0].Selected && W.SceneTree.Model.Rows[1].Selected);
    }

    [Fact]
    public void InspectorEditsGoThroughTheHistoryAndUndoRefreshesTheFields()
    {
        var child = AddChild("Child");
        var rows = W.Inspector.Rows;
        var position = rows.ToList().FindIndex(r => r.Name == "Position");

        Assert.True(W.Inspector.Commit(position, 1, "2.5"));
        _editor.Tick();
        Assert.Equal(2.5f, child.Position.Y);
        Assert.Equal("Set Position", _editor.Scene.History.UndoAction!.Name);
        Assert.False(W.Inspector.Commit(position, 1, "not a number"));
        Assert.Equal(2.5f, child.Position.Y);

        _editor.Key(Key.Z, Key.ControlLeft);
        Assert.Equal(0f, child.Position.Y);
        var field = W.Inspector.Document.GetElementById($"p{position}c1");
        Assert.Equal("0", field.Value);

        _editor.Key(Key.Z, Key.ControlLeft, Key.ShiftLeft);
        Assert.Equal(2.5f, child.Position.Y);
        Assert.Equal("2.5", W.Inspector.Document.GetElementById($"p{position}c1").Value);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void ATypedValueIsCommittedToItsOwnNodeWhenTheSelectionChanges()
    {
        var a = AddChild("A");
        var b = AddChild("B");
        _editor.Scene.Selection.Set(a);
        _editor.Tick();
        var row = W.Inspector.Rows.ToList().FindIndex(r => r.Name == "Position");
        var field = W.Inspector.Document.GetElementById($"p{row}c1");
        Assert.True(field.Focus());
        field.SetValue("9");

        _editor.Scene.Selection.Set(b); // the inspector rebuilds for B: A's pending edit is committed first
        _editor.Tick();

        Assert.Equal(9f, a.Position.Y);
        Assert.Equal(0f, b.Position.Y);
        Assert.Same(b, W.Inspector.Target);
    }

    [Fact]
    public void EveryEditorKindRendersAndEdits()
    {
        var node = new AllHintsNode { Name = "All" };
        _editor.Scene.AddNode(node, _editor.Scene.Root);
        _editor.Tick(2);
        var rows = W.Inspector.Rows.ToList();
        int Row(string name) => rows.FindIndex(r => r.Name == name && ReferenceEquals(r.Target, node));

        Assert.True(W.Inspector.Commit(Row("Mood"), 0, "Grumpy"));
        Assert.Equal(Mood.Grumpy, node.Mood);
        Assert.True(W.Inspector.Commit(Row("Volume"), 0, "7.5"));
        Assert.Equal(7.5f, node.Volume);
        Assert.True(W.Inspector.Commit(Row("Tint"), 9, "#0000ff"));
        Assert.Equal(255, node.Tint.B);
        Assert.True(W.Inspector.Commit(Row("Orientation"), 1, "45"));
        Assert.Equal(45f, EulerAngles.FromQuaternion(node.Orientation).Y, 3);
        Assert.True(W.Inspector.Commit(Row("Title"), 0, "renamed"));
        Assert.Equal("renamed", node.Title);

        W.Inspector.RunAction(Row("Samples"), "arr-add");
        _editor.Tick();
        Assert.Equal([1f, 2f, 0f], node.Samples);
        W.Inspector.RunAction(W.Inspector.Rows.ToList().FindIndex(r => r.Name == "Tags"), "arr-remove", 0);
        _editor.Tick();
        Assert.Empty(node.Tags);

        W.Inspector.RunAction(W.Inspector.Rows.ToList().FindIndex(r => r.Name == "Settings"), "res-new"); // one concrete type: created at once
        _editor.Tick();
        Assert.IsType<TestSettings>(node.Settings);
        // The new inline resource is expanded: its own rows are editable through the same history.
        var strength = W.Inspector.Rows.ToList().FindIndex(r => r.Name == "Strength");
        Assert.True(strength > 0);
        Assert.True(W.Inspector.Commit(strength, 0, "3"));
        Assert.Equal(3f, node.Settings!.Strength);

        W.Inspector.RunAction(W.Inspector.Rows.ToList().FindIndex(r => r.Name == "Tint"), "color");
        _editor.Tick();
        Assert.False(W.Inspector.Document.GetElementById($"p{W.Inspector.Rows.ToList().FindIndex(r => r.Name == "Tint")}s0").IsNull);

        // Every edit is one history entry, undone in order.
        while (_editor.Scene.History.CanUndo && _editor.Scene.History.UndoAction is SetPropertyAction)
            _editor.Scene.History.Undo();
        _editor.Tick();
        Assert.Equal(Mood.Happy, node.Mood);
        Assert.Null(node.Settings);
        Assert.Equal([1f, 2f], node.Samples);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void AddNodeDialogListsRegisteredTypesAndAddsTheChosenOne()
    {
        W.Commands.Execute("node.add");
        _editor.Tick();
        Assert.True(W.ListPicker.Visible);
        Assert.Contains("OmniLight3D", W.ListPicker.VisibleLabels);
        Assert.DoesNotContain("MissingNode", W.ListPicker.VisibleLabels);
        Assert.DoesNotContain("EditorWorkspace", W.ListPicker.VisibleLabels);

        W.ListPicker.SetQuery("omni");
        _editor.Tick();
        Assert.Equal(["OmniLight3D"], W.ListPicker.VisibleLabels);
        W.ListPicker.Accept();
        _editor.Tick();

        var light = Assert.IsType<OmniLight3D>(_editor.Scene.Selection.Primary);
        Assert.Same(_editor.Scene.Root, light.Parent);
        Assert.Same(_editor.Scene.Root, light.Owner);
        Assert.False(W.ListPicker.Visible);
        Assert.Equal(2, W.SceneTree.Model.Rows.Count);
    }

    [Fact]
    public void ShortcutsRunCommandsUnlessADialogIsOpen()
    {
        var child = AddChild("Child");
        _editor.Scene.Selection.Set(child);

        _editor.Key(Key.D, Key.ControlLeft);
        Assert.Equal(["Child", "Child2"], _editor.Scene.Root.Children.Select(c => c.Name));
        _editor.Key(Key.Delete);
        Assert.Equal(1, _editor.Scene.Root.ChildCount);
        _editor.Key(Key.E);
        Assert.Equal(GizmoMode.Rotate, W.Gizmo.Mode);

        W.Commands.Execute("node.add");
        _editor.Key(Key.W); // goes nowhere while the dialog is open
        Assert.Equal(GizmoMode.Rotate, W.Gizmo.Mode);

        // Closing the dialog releases the search field's focus, so shortcuts work again at once.
        W.ListPicker.Cancel();
        _editor.Tick();
        Assert.False(_editor.Server.TextInputActive);
        _editor.Key(Key.W);
        Assert.Equal(GizmoMode.Translate, W.Gizmo.Mode);
    }

    [Theory]
    [InlineData(Key.S, EditorModifiers.Command, "file.save")]
    [InlineData(Key.S, EditorModifiers.Command | EditorModifiers.Shift, "file.save_as")]
    [InlineData(Key.Z, EditorModifiers.Command, "edit.undo")]
    [InlineData(Key.Z, EditorModifiers.Command | EditorModifiers.Shift, "edit.redo")]
    [InlineData(Key.Y, EditorModifiers.Command, "edit.redo")]
    [InlineData(Key.D, EditorModifiers.Command, "edit.duplicate")]
    [InlineData(Key.Delete, EditorModifiers.None, "edit.delete")]
    [InlineData(Key.F, EditorModifiers.None, "view.frame")]
    [InlineData(Key.W, EditorModifiers.None, "gizmo.translate")]
    [InlineData(Key.W, EditorModifiers.Alt, null)]
    [InlineData(Key.K, EditorModifiers.None, null)]
    public void ShortcutTable(Key key, EditorModifiers modifiers, string? command) =>
        Assert.Equal(command, EditorWorkspace.ShortcutFor(key, modifiers));

    [Fact]
    public void MenusListCommandsWithStateAndRunTheChosenOne()
    {
        var child = AddChild("Child");
        W.MenuBar.OpenMenu("edit");
        _editor.Tick();
        Assert.True(W.Popup.Visible);
        var undo = W.Popup.Items.First(i => i.Command == "edit.undo");
        Assert.Equal("Undo Add Child", undo.Label);
        Assert.True(undo.Enabled);
        Assert.False(W.Popup.Items.First(i => i.Command == "edit.redo").Enabled);

        Assert.True(W.Popup.Choose("edit.undo"));
        _editor.Tick();
        Assert.False(W.Popup.Visible);
        Assert.Null(child.Parent);

        W.Commands.ShowHistory();
        Assert.Contains(W.Popup.Items, i => i.Command == "history:1" && i.Css == "undone");
        W.Popup.Choose("history:1");
        Assert.Same(_editor.Scene.Root, child.Parent);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void TreeDragReparentsAndReorders()
    {
        var a = AddChild("A");
        var b = AddChild("B");
        var tree = W.SceneTree;

        tree.Press(2, 0, EditorModifiers.None); // B
        tree.DragOver(1, 0.5f);                 // onto the middle of A
        tree.Release(1);
        _editor.Tick();
        Assert.Same(a, b.Parent);

        Assert.True(tree.Drop(b, a, TreeDropPosition.Before));
        Assert.Equal(["B", "A"], _editor.Scene.Root.Children.Select(c => c.Name));
        Assert.False(tree.Drop(a, a, TreeDropPosition.Inside));
        Assert.False(tree.Drop(b, _editor.Scene.Root, TreeDropPosition.Before)); // nothing beside the root
    }

    [Fact]
    public void RenameAsksForTheNameInAMessageBox()
    {
        var child = AddChild("Child");
        W.Commands.Execute("edit.rename");
        _editor.Tick();
        Assert.True(W.Message.Visible);
        W.Message.Document.GetElementById("input").SetValue("Renamed");
        W.Message.Answer(0);
        _editor.Tick();
        Assert.Equal("Renamed", child.Name);
        Assert.Equal("Renamed", W.SceneTree.Model.Rows[1].Name);
    }

    [Fact]
    public void QuittingWithUnsavedChangesAsksFirst()
    {
        AddChild("X");
        W.RequestQuit();
        Assert.False(_editor.Host.QuitRequested);
        Assert.Equal("Unsaved changes", W.Message.Current!.Title);
        W.Message.Answer(2); // cancel
        Assert.False(_editor.Host.QuitRequested);

        W.RequestQuit();
        W.Message.Answer(1); // don't save
        Assert.True(_editor.Host.QuitRequested);
    }

    [Fact]
    public void QuittingACleanSessionDoesNotAsk()
    {
        W.RequestQuit();
        Assert.True(_editor.Host.QuitRequested);
        Assert.Null(W.Message.Current);
    }

    [Fact]
    public void SaveAsWritesTheChosenFileAndClosingADirtySceneAsks()
    {
        AddChild("Child");
        W.Commands.Execute("file.save_as");
        Assert.True(W.FilePicker.Visible);
        W.FilePicker.NavigateTo(_editor.Directory);
        W.FilePicker.Model!.FileName = "Saved";
        W.FilePicker.Accept();
        _editor.Tick();

        var path = Path.Combine(_editor.Directory, "Saved.mscene");
        Assert.True(File.Exists(path), $"{W.Message.Current?.Message} | {W.FilePicker.Model?.Error}");
        Assert.False(_editor.Scene.IsDirty);
        Assert.Equal("Saved.mscene — Mainframe Editor", _editor.Host.Title);

        AddChild("More");
        W.Commands.Execute("file.close");
        Assert.True(W.Message.Visible);
        W.Message.Answer(1); // don't save
        _editor.Tick();
        Assert.Empty(W.Session.Scenes);
        Assert.Equal("Mainframe Editor", _editor.Host.Title);
    }

    [Fact]
    public void LogMessagesShowInTheOutputPanelWithFilters()
    {
        Log.Info("workspace-test-info");
        Log.Warning("workspace-test-warning");
        _editor.Tick(2);
        Assert.Contains(W.OutputPanel.VisibleMessages, m => m.Text == "workspace-test-info");

        W.OutputPanel.Toggle(OutputLevel.Info);
        Assert.DoesNotContain(W.OutputPanel.VisibleMessages, m => m.Text == "workspace-test-info");
        Assert.Contains(W.OutputPanel.VisibleMessages, m => m.Text == "workspace-test-warning");
        Assert.Equal(0b1101, W.Layout.Settings.OutputFilter); // persisted with the layout
    }

    [Fact]
    public void CommandFailuresAreReportedNotThrown()
    {
        Assert.False(W.Commands.Execute("no.such.command"));
        W.Session.Close(_editor.Scene);
        Assert.True(W.Commands.Execute("edit.undo")); // nothing open: a no-op
    }

    [Fact]
    public void RecoveryCopiesAreWrittenForDirtyScenes()
    {
        Assert.Empty(W.WriteRecoveryCopies());
        AddChild("Unsaved");
        var written = Assert.Single(W.WriteRecoveryCopies());
        Assert.StartsWith(Path.Combine(_editor.Directory, "recovery"), written, StringComparison.Ordinal);
        Assert.Contains("\"Unsaved\"", File.ReadAllText(written), StringComparison.Ordinal);
    }

    [Fact]
    public void LayoutIsSavedWhenTheWorkspaceLeaves()
    {
        W.Layout.DragSplitter(Splitter.Left, 360, 0);
        W.SaveLayout();
        var saved = EditorLayout.Load(Path.Combine(_editor.Directory, "layout.json"));
        Assert.Equal(W.Layout.Settings.LeftWidth, saved.LeftWidth);
        Assert.Equal(1600, saved.WindowWidth);
    }

    [Fact]
    public void IdleFramesWithAThousandNodesAllocateNothing()
    {
        var scene = _editor.Scene;
        var mesh = new BoxMesh();
        for (var i = 0; i < 1000; i++)
        {
            var node = new MeshInstance3D { Name = $"Box{i}", Mesh = mesh, Position = new Vector3(i % 30, 0, i / 30) };
            scene.Root.AddChild(node);
            node.Owner = scene.Root;
        }

        scene.History.Clear();
        W.SceneTree.Refresh();
        scene.Selection.Set(scene.Root.GetChild(10));
        _editor.Tick(120);

        // Minimum over several windows (the first may include one-time JIT/tiering work).
        var best = long.MaxValue;
        for (var window = 0; window < 3; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            _editor.Tick(100);
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.True(best == 0, $"{best} B allocated over 100 idle frames");
        Assert.Equal(1001, W.SceneTree.Model.Rows.Count);
    }
}
