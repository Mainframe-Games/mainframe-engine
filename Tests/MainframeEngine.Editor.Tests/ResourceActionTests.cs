using System.Numerics;

namespace MainframeEngine.Editor.Tests;

/// <summary>
/// G7.2: the inspector's resource More menu — Save as .mres (every slot follows, undo restores the inline resource and
/// the scene's bytes), Make Unique (shallow shares sub-resources, deep copies them), Open, Show in FileSystem, the
/// "inline ×N" label and the disabled states.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class ResourceActionTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public ResourceActionTests()
    {
        AssetDatabase.Current = new AssetDatabase(_editor.Directory);
        Directory.CreateDirectory(Path.Combine(_editor.Directory, "Assets", "Materials"));
    }

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    private int Row(string name) => W.Inspector.Rows.ToList().FindIndex(r => r.Name == name);

    private MenuItem Item(string command) => W.Inspector.ResourceMenu(Row("Mesh")).Single(i => i.Command == command);

    private (MeshInstance3D A, MeshInstance3D B, BoxMesh Mesh) TwoCratesSharingAMesh()
    {
        var mesh = new BoxMesh { Size = new Vector3(2, 1, 1), Material = new StandardMaterial3D { AlbedoColor = System.Drawing.Color.FromArgb(255, 204, 77, 51) } };
        var a = new MeshInstance3D { Name = "CrateA", Mesh = mesh };
        var b = new MeshInstance3D { Name = "CrateB", Mesh = mesh, Position = new Vector3(3, 0, 0) };
        _editor.Scene.AddNode(a, _editor.Scene.Root);
        _editor.Scene.AddNode(b, _editor.Scene.Root);
        _editor.Scene.Selection.Set(a);
        _editor.Tick();
        return (a, b, mesh);
    }

    [Fact]
    public void TheLabelCountsInlineUsesAndTheMenuFollowsTheValue()
    {
        var (a, _, _) = TwoCratesSharingAMesh();
        var row = Row("Mesh");
        Assert.True(row >= 0);
        Assert.Equal("BoxMesh (inline ×2)", W.Inspector.ResourceLabel(W.Inspector.Rows[row], a.Mesh));
        Assert.True(Item("res-save").Enabled);
        Assert.True(Item("res-unique").Enabled); // shared by two slots
        Assert.True(Item("res-unique-deep").Enabled); // it holds an inline material
        Assert.False(Item("res-open").Enabled); // inline: no file
        Assert.False(Item("res-show").Enabled);

        W.Inspector.RunAction(row, "res-more");
        _editor.Tick();
        Assert.True(W.Popup.Visible);
        Assert.Contains(W.Popup.Items, i => i.Command == "res-unique-deep");
        W.Popup.Close();
    }

    [Fact]
    public void SaveAsMovesEverySlotToTheFileAndUndoRestoresTheSceneBytes()
    {
        var (a, b, mesh) = TwoCratesSharingAMesh();
        var before = SceneSaver.ToJson(_editor.Scene.Root);
        var path = Path.Combine(_editor.Directory, "Assets", "Materials", "crate.mres");

        Assert.True(W.Inspector.SaveResourceAs(_editor.Scene, mesh, path));
        var saved = Assert.IsType<BoxMesh>(a.Mesh);
        Assert.Same(saved, b.Mesh);
        Assert.True(saved.IsExternal);
        Assert.StartsWith("res_", saved.Uid, StringComparison.Ordinal);
        Assert.NotSame(mesh.Material, saved.Material); // the inline material went into the file (a deep copy)
        Assert.True(File.Exists(path));
        _editor.Tick();
        Assert.True(Item("res-open").Enabled);
        Assert.True(Item("res-show").Enabled);
        Assert.False(Item("res-save").Enabled);

        _editor.Scene.History.Undo();
        Assert.Same(mesh, a.Mesh);
        Assert.Same(mesh, b.Mesh);
        Assert.Equal(before, SceneSaver.ToJson(_editor.Scene.Root)); // byte-identical after undo
        _editor.Scene.History.Redo();
        Assert.Same(saved, b.Mesh);
    }

    [Fact]
    public void SaveAsRefusesAFileWhoseResourceIsLoaded()
    {
        var (_, _, mesh) = TwoCratesSharingAMesh();
        var path = Path.Combine(_editor.Directory, "Assets", "Materials", "used.mres");
        ResourceSaver.Save(new BoxMesh(), path); // registered and cached: in use
        var history = _editor.Scene.History.Actions.Count;
        Assert.False(W.Inspector.SaveResourceAs(_editor.Scene, mesh, path));
        Assert.Equal(history, _editor.Scene.History.Actions.Count);
        W.Message.Answer(0); // the error box
    }

    [Fact]
    public void MakeUniqueGivesOneSlotACopyShallowOrDeep()
    {
        var (a, b, mesh) = TwoCratesSharingAMesh();
        W.Inspector.RunAction(Row("Mesh"), "res-unique");
        var copy = Assert.IsType<BoxMesh>(a.Mesh);
        Assert.NotSame(mesh, copy);
        Assert.Same(mesh, b.Mesh);
        Assert.Same(mesh.Material, copy.Material); // shallow: the sub-resource is shared
        Assert.Equal(mesh.Size, copy.Size);
        Assert.Equal("Make BoxMesh Unique", _editor.Scene.History.UndoAction!.Name);
        _editor.Scene.History.Undo();
        Assert.Same(mesh, a.Mesh);

        W.Inspector.RunAction(Row("Mesh"), "res-unique-deep");
        var deep = Assert.IsType<BoxMesh>(a.Mesh);
        Assert.NotSame(mesh.Material, deep.Material); // deep: copied too
        Assert.Equal(((StandardMaterial3D)mesh.Material!).AlbedoColor, ((StandardMaterial3D)deep.Material!).AlbedoColor);
        _editor.Tick();
        Assert.False(Item("res-unique").Enabled); // now used by this slot only
    }

    [Fact]
    public void MakeUniqueCopiesAResourceFileIntoTheSlotAndOpenInspectsTheFile()
    {
        var path = Path.Combine(_editor.Directory, "Assets", "Materials", "shared.mres");
        ResourceSaver.Save(new BoxMesh { Size = new Vector3(4, 4, 4) }, path);
        var file = ResourceLoader.Load<BoxMesh>("Assets/Materials/shared.mres");
        var node = new MeshInstance3D { Name = "Crate", Mesh = file };
        _editor.Scene.AddNode(node, _editor.Scene.Root);
        _editor.Scene.Selection.Set(node);
        _editor.Tick();
        Assert.True(Item("res-unique").Enabled);
        Assert.False(Item("res-unique-deep").Enabled); // no inline sub-resources

        W.Inspector.RunAction(Row("Mesh"), "res-show");
        W.Inspector.RunAction(Row("Mesh"), "res-open");
        Assert.NotNull(W.Inspector.InspectedResource);
        Assert.Same(file, W.Inspector.InspectedResource!.Resource);
        W.Inspector.CloseResource();
        _editor.Tick();

        W.Inspector.RunAction(Row("Mesh"), "res-unique");
        var copy = Assert.IsType<BoxMesh>(node.Mesh);
        Assert.False(copy.IsExternal);
        Assert.Equal(new Vector3(4, 4, 4), copy.Size);
        Assert.True(file.IsExternal); // the file is untouched
        file.Release();
    }

    [Fact]
    public void DeepCopiesReachResourcesInListsAndArrays()
    {
        var level = new TestSettings { Strength = 2f };
        var extra = new TestSettings { Label = "bonus" };
        var set = new TestLevelSet { Levels = [level], Extras = [extra] };
        Assert.True(ResourceActions.HasInlineSubResources(set)); // only in a list and an array

        var shallow = (TestLevelSet)set.Duplicate(deep: false);
        Assert.NotSame(set.Levels, shallow.Levels); // a list of its own…
        Assert.NotSame(set.Extras, shallow.Extras);
        Assert.Same(level, shallow.Levels[0]); // …sharing the elements
        Assert.Same(extra, shallow.Extras[0]);

        var deep = (TestLevelSet)set.Duplicate(deep: true);
        Assert.NotSame(level, deep.Levels[0]);
        Assert.NotSame(extra, deep.Extras[0]);
        Assert.Equal(2f, deep.Levels[0].Strength);
        Assert.Equal("bonus", deep.Extras[0].Label);
        Assert.False(ResourceActions.HasInlineSubResources(new TestLevelSet()));
    }

    [Fact]
    public void MakeUniqueRefusedWhileSimulatingMarksNoFile()
    {
        var path = Path.Combine(_editor.Directory, "Assets", "Materials", "box.mres");
        ResourceSaver.Save(new BoxMesh { Material = new StandardMaterial3D() }, path);
        var file = ResourceLoader.Load<BoxMesh>("Assets/Materials/box.mres");
        var node = new MeshInstance3D { Name = "Crate", Mesh = file };
        _editor.Scene.AddNode(node, _editor.Scene.Root);
        _editor.Tick();
        var materialSlot = Serialization.TypeRegistry.GetRequired(typeof(BoxMesh)).FindProperty("Material")!;
        Assert.True(W.Simulation.StartNow(_editor.Scene));
        Assert.Null(_editor.Scene.MakeUnique(file, materialSlot, deep: false)); // the history is locked
        W.Simulation.Stop();
        Assert.DoesNotContain(file, _editor.Scene.EditedResourceFiles);
        Assert.NotNull(_editor.Scene.MakeUnique(file, materialSlot, deep: false));
        Assert.Contains(file, _editor.Scene.EditedResourceFiles);
        file.Release();
    }

    [Fact]
    public void ImportedAndSceneResourcesCannotBeMadeUniqueOrSaved()
    {
        Assert.False(ResourceActions.CanMakeUnique(new PackedScene(), 2, out var reason));
        Assert.Contains("PackedScene", reason, StringComparison.Ordinal);
        Assert.False(ResourceActions.CanSaveAsFile(new PackedScene()));
        Assert.True(ResourceActions.CanSaveAsFile(new BoxMesh()));
        Assert.False(ResourceActions.CanMakeUnique(new BoxMesh(), 1, out reason)); // inline, one slot: already unique
        Assert.True(ResourceActions.CanMakeUnique(new BoxMesh(), 2, out _));
    }
}
