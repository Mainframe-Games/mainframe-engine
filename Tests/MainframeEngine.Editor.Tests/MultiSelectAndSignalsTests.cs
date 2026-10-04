using System.Numerics;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor.Tests;

/// <summary>E5: multi-selection editing in the inspector and the Signals tab (connect/disconnect, undo, save).</summary>
[Collection(nameof(SerialEditor))]
public sealed class MultiSelectAndSignalsTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    private T Add<T>(string name, Vector3 position = default)
        where T : Node3D, new()
    {
        var node = new T { Name = name, Position = position };
        _editor.Scene.AddNode(node, _editor.Scene.Root);
        _editor.Tick();
        return node;
    }

    private int Row(string name) => W.Inspector.Rows.ToList().FindIndex(r => r.Name == name);

    private void SelectAll(params Node[] nodes)
    {
        _editor.Scene.Selection.Set(nodes[0]);
        for (var i = 1; i < nodes.Length; i++)
            _editor.Scene.Selection.Add(nodes[i]);
        _editor.Tick();
    }

    [Fact]
    public void TheModelOfSeveralNodesHasOnlyTheirSharedProperties()
    {
        var light = new OmniLight3D();
        var mesh = new MeshInstance3D();
        var model = InspectorModel.Build([light, mesh]);

        var names = model.Properties.Select(p => p.Name).ToList();
        Assert.Contains("Position", names);
        Assert.Contains("Visible", names);
        Assert.DoesNotContain("Mesh", names);
        Assert.DoesNotContain("Range", names);
        Assert.All(model.Properties, p => Assert.Equal(2, p.Targets.Count));
        Assert.Same(mesh, model.Target);
        Assert.Null(model.CustomInspector);
        Assert.Equal("Node3D", InspectorModel.CommonType([light, mesh])!.Name);
        Assert.Equal("OmniLight3D", InspectorModel.CommonType([light, new OmniLight3D()])!.Name);
        light.Free();
        mesh.Free();
    }

    [Fact]
    public void MixedValuesShowAsADashAndAComponentEditKeepsEachNodesOtherComponents()
    {
        var a = Add<Node3D>("A", new Vector3(1, 1, 3));
        var b = Add<OmniLight3D>("B", new Vector3(1, 2, 4));
        SelectAll(a, b);
        Assert.Equal(2, W.Inspector.Targets.Count);
        Assert.Same(b, W.Inspector.Target);

        var position = Row("Position");
        var property = W.Inspector.Rows[position];
        Assert.True(property.IsMixed);
        Assert.False(property.IsComponentMixed(0));
        Assert.True(property.IsComponentMixed(1));
        var y = W.Inspector.Document.GetElementById($"p{position}c1");
        Assert.Equal("", y.Value);
        Assert.True(y.IsClassSet("mixed"));
        Assert.Equal("1", W.Inspector.Document.GetElementById($"p{position}c0").Value);

        // An untouched mixed field commits nothing.
        var entries = _editor.Scene.History.Actions.Count;
        Assert.False(W.Inspector.Commit(position, 1, ""));
        Assert.Equal(entries, _editor.Scene.History.Actions.Count);

        Assert.True(W.Inspector.Commit(position, 0, "5"));
        Assert.Equal(new Vector3(5, 1, 3), a.Position);
        Assert.Equal(new Vector3(5, 2, 4), b.Position);
        Assert.Equal(entries + 1, _editor.Scene.History.Actions.Count);
        Assert.Contains("2 nodes", _editor.Scene.History.UndoAction!.Name, StringComparison.Ordinal);

        _editor.Scene.History.Undo();
        _editor.Tick();
        Assert.Equal(new Vector3(1, 1, 3), a.Position);
        Assert.Equal(new Vector3(1, 2, 4), b.Position);

        // Setting the mixed component makes it equal everywhere.
        position = Row("Position");
        Assert.True(W.Inspector.Commit(position, 1, "7"));
        _editor.Tick();
        Assert.Equal(7, a.Position.Y);
        Assert.Equal(7, b.Position.Y);
        position = Row("Position");
        Assert.False(W.Inspector.Rows[position].IsComponentMixed(1));
        Assert.Equal("7", W.Inspector.Document.GetElementById($"p{position}c1").Value);
    }

    [Fact]
    public void BoolsAndContinuousEditsApplyToAllAsOneUndoStep()
    {
        var a = Add<Node3D>("A");
        var b = Add<Node3D>("B");
        b.Visible = false;
        SelectAll(a, b);
        var visible = Row("Visible");
        Assert.True(W.Inspector.Rows[visible].IsMixed);
        Assert.True(W.Inspector.Document.GetElementById($"p{visible}c0").IsClassSet("mixed"));

        var entries = _editor.Scene.History.Actions.Count;
        Assert.True(W.Inspector.Commit(visible, 0, "false"));
        Assert.False(a.Visible);
        Assert.False(b.Visible);

        // A drag (merge key) over several frames is one entry.
        var scale = Row("Scale");
        W.Inspector.Commit(scale, 0, "2", mergeKey: "drag");
        W.Inspector.Commit(scale, 0, "3", mergeKey: "drag");
        W.Inspector.Commit(scale, 0, "4", mergeKey: "drag");
        _editor.Scene.History.EndMerge();
        Assert.Equal(4, a.Scale.X);
        Assert.Equal(4, b.Scale.X);
        Assert.Equal(entries + 2, _editor.Scene.History.Actions.Count);

        _editor.Scene.History.Undo();
        Assert.Equal(1, a.Scale.X);
        Assert.Equal(1, b.Scale.X);
        _editor.Scene.History.Undo();
        Assert.True(a.Visible);
        Assert.False(b.Visible);
    }

    [Fact]
    public void SelectingASingleNodeAgainShowsItsFullInspector()
    {
        var a = Add<Node3D>("A");
        var light = Add<OmniLight3D>("Lamp");
        SelectAll(a, light);
        Assert.Equal(-1, Row("Range"));
        _editor.Scene.Selection.Set(light);
        _editor.Tick();
        Assert.Single(W.Inspector.Targets);
        Assert.True(Row("Range") >= 0);
        Assert.False(W.Inspector.Document.GetElementById("node-name").IsNull);
    }

    [Fact]
    public void CompatibleMethodsFollowTheConnectRule()
    {
        var signal = TypeRegistry.GetRequired(typeof(SignalNode)).FindSignal("Pinged")!;
        var methods = SignalModel.CompatibleMethods(typeof(SignalNode), signal).Select(m => m.Name).ToList();
        Assert.Contains("OnPinged", methods);
        Assert.Contains("Ping", methods);
        Assert.DoesNotContain("get_Received", methods);
        Assert.DoesNotContain("ToString", methods);
        var entered = TypeRegistry.GetRequired(typeof(Node)).FindSignal("ChildEnteredTree")!;
        Assert.DoesNotContain("OnPinged", SignalModel.CompatibleMethods(typeof(SignalNode), entered).Select(m => m.Name));
        Assert.Equal("ChildEnteredTree(Node)", SignalModel.Signature(entered.Name, entered.ParameterTypes));
        Assert.Equal("OnButton1Pinged", SignalModel.SuggestedMethodName(new SignalNode { Name = "Button 1" }, signal));
    }

    [Fact]
    public void TheSignalsTabConnectsThroughTheDialogWithUndoAndTheSceneSavesIt()
    {
        var source = Add<SignalNode>("Source");
        var target = Add<SignalNode>("Target");
        _editor.Scene.Selection.Set(source);
        _editor.Tick();
        W.Inspector.SelectTab(InspectorTab.Signals);
        _editor.Tick();
        Assert.True(W.Inspector.Document.GetElementById("inspector-body").IsClassSet("hidden"));
        Assert.False(W.Inspector.Document.GetElementById("signals-body").IsClassSet("hidden"));
        var pinged = W.Inspector.SignalRows.ToList().FindIndex(r => r.Name == "Pinged");
        Assert.True(pinged >= 0);
        Assert.Contains(W.Inspector.SignalRows, r => r.Name == "Renamed"); // base type signals too

        W.Inspector.ConnectSignal(pinged);
        _editor.Tick();
        Assert.True(W.SignalDialog.Visible);
        Assert.True(W.IsDialogOpen);
        Assert.Equal("OnSourcePinged", W.SignalDialog.MethodName);
        Assert.True(W.SignalDialog.SelectNode(target));
        Assert.Contains("OnPinged", W.SignalDialog.MethodLabels);

        // An unknown method keeps the dialog open with the reason.
        Assert.False(W.SignalDialog.Accept());
        Assert.Contains("OnSourcePinged", W.SignalDialog.Error, StringComparison.Ordinal);
        Assert.True(W.SignalDialog.Visible);

        W.SignalDialog.SelectMethod(W.SignalDialog.MethodLabels.ToList().IndexOf("OnPinged"));
        Assert.True(W.SignalDialog.Accept());
        _editor.Tick();
        Assert.False(W.SignalDialog.Visible);
        Assert.True(source.IsConnected("Pinged", target, "OnPinged"));
        Assert.Single(W.Inspector.SignalRows[pinged].Connections);
        Assert.Equal("Connect Source.Pinged", _editor.Scene.History.UndoAction!.Name);
        source.Ping();
        Assert.Equal(1, target.Received);

        _editor.Scene.History.Undo();
        _editor.Tick();
        Assert.False(source.IsConnected("Pinged", target, "OnPinged"));
        Assert.Empty(W.Inspector.SignalRows[pinged].Connections);
        _editor.Scene.History.Redo();
        _editor.Tick();
        Assert.True(source.IsConnected("Pinged", target, "OnPinged"));

        // Saved with the scene, loaded back.
        var path = Path.Combine(_editor.Directory, "Content", "Scenes", "Signals.mscene");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        W.Session.Save(_editor.Scene, path);
        var json = File.ReadAllText(path);
        Assert.Contains("\"connections\"", json, StringComparison.Ordinal);
        Assert.Contains("\"method\": \"OnPinged\"", json, StringComparison.Ordinal);
        var reopened = PackedScene.Parse(File.ReadAllBytes(path)).Instantiate();
        Assert.True(reopened.GetNode<SignalNode>("Source").IsConnected("Pinged", reopened.GetNode("Target"), "OnPinged"));
        reopened.Free();

        // Disconnect from the tab; undo restores it.
        W.Inspector.DisconnectSignal(pinged, 0);
        _editor.Tick();
        Assert.False(source.IsConnected("Pinged", target, "OnPinged"));
        _editor.Scene.History.Undo();
        Assert.True(source.IsConnected("Pinged", target, "OnPinged"));
    }

    [Fact]
    public void ConnectFlagsAreKeptAndDuplicateConnectionsRefused()
    {
        var source = Add<SignalNode>("Source");
        var target = Add<SignalNode>("Target");
        _editor.Scene.ConnectSignal(source, "Pinged", target, "OnPinged", ConnectFlags.Deferred);
        var connection = Assert.Single(source.GetSignalConnections());
        Assert.Equal(ConnectFlags.Deferred | ConnectFlags.Persist, connection.Flags);
        Assert.Throws<InvalidOperationException>(() => _editor.Scene.ConnectSignal(source, "Pinged", target, "OnPinged"));
        Assert.Throws<ArgumentException>(() => _editor.Scene.ConnectSignal(source, "Nope", target, "OnPinged"));

        Assert.True(_editor.Scene.DisconnectSignal(connection));
        _editor.Scene.History.Undo();
        Assert.Equal(ConnectFlags.Deferred | ConnectFlags.Persist, Assert.Single(source.GetSignalConnections()).Flags);
    }

    [Fact]
    public void SeveralSelectedNodesShowNoSignals()
    {
        var a = Add<SignalNode>("A");
        var b = Add<SignalNode>("B");
        W.Inspector.SelectTab(InspectorTab.Signals);
        SelectAll(a, b);
        Assert.Empty(W.Inspector.SignalRows);
        Assert.Contains("one node at a time", W.Inspector.Document.GetElementById("signals-body").InnerRml, StringComparison.Ordinal);
        Assert.Empty(_editor.RmlMessages);
    }
}
