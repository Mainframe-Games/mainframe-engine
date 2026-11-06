using System.Globalization;
using System.Text.RegularExpressions;
using MainframeEngine.Serialization;
using MainframeEngine.Trees;

namespace MainframeEngine.Editor.Tests;

/// <summary>G8b.3: the <see cref="Tree3D"/> inspector — preset, import, seed dice, style, LOD table, Bake and its status.</summary>
[Collection(nameof(SerialEditor))]
public sealed partial class Tree3DInspectorTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();
    private readonly AssetDatabase _previous = AssetDatabase.Current;

    public Tree3DInspectorTests()
    {
        System.IO.Directory.CreateDirectory(Path.Combine(_editor.Directory, AssetDatabase.ContentFolder));
        AssetDatabase.Current = new AssetDatabase(_editor.Directory);
    }

    public void Dispose()
    {
        AssetDatabase.Current = _previous;
        _editor.Dispose();
    }

    private EditorWorkspace W => _editor.Workspace;

    private Tree3D AddTree()
    {
        var tree = (Tree3D)TypeRegistry.CreateNode(nameof(Tree3D));
        tree.Name = "Tree";
        NewNodeDefaults.Apply(tree);
        _editor.Scene.AddNode(tree, _editor.Scene.Root);
        _editor.Tick(2);
        _editor.Scene.Selection.Set(tree);
        _editor.Tick();
        return tree;
    }

    private static string Header(Tree3D tree) => CustomInspectors.Find(typeof(Tree3D))!.GetHeaderRml(tree)!;

    [Fact]
    public void ANewTreeIsAnEditableCopyOfOakMedium()
    {
        var tree = AddTree();
        Assert.IsType<Tree3DInspector>(CustomInspectors.Find(typeof(Tree3D)));
        Assert.Equal("Oak Medium", tree.Preset);
        Assert.NotNull(tree.Options);
        Assert.Equal(3, tree.LodCount);
        Assert.Equal("Oak Medium", Tree3DInspector.PresetOf(tree));
        var header = Header(tree);
        Assert.Contains("<option value=\"Oak Medium\" selected=\"\">", header, StringComparison.Ordinal);
        Assert.Contains("Pine Medium", header, StringComparison.Ordinal);
        Assert.Contains("Custom", header, StringComparison.Ordinal);
        foreach (var name in TreePresets.Names)
            Assert.Contains($"value=\"{name}\"", header, StringComparison.Ordinal);

        // The header edits these; the options' rows stay.
        var rows = W.Inspector.Rows.Select(r => r.Name).ToList();
        Assert.DoesNotContain(nameof(Tree3D.Preset), rows);
        Assert.DoesNotContain(nameof(Tree3D.Seed), rows);
        Assert.DoesNotContain(nameof(Tree3D.Style), rows);
        Assert.Contains(nameof(Tree3D.Options), rows);

        // An edit to the options makes it Custom (from Oak Medium).
        tree.Options!.Level[0].Length += 1;
        Assert.Null(Tree3DInspector.PresetOf(tree));
        Assert.Contains("Custom (from Oak Medium)", Header(tree), StringComparison.Ordinal);
    }

    [Fact]
    public void APresetIsOneUndoStep()
    {
        var tree = AddTree();
        var oak = tree.Options;
        var history = _editor.Scene.History.Position;
        W.Inspector.RunHeaderAction(Tree3DInspector.PresetInput + "=Pine Medium");
        _editor.Tick();
        Assert.Equal("Pine Medium", tree.Preset);
        Assert.NotSame(oak, tree.Options);
        Assert.Equal(TreeType.Evergreen, tree.Options!.Type);
        Assert.Equal(history + 1, _editor.Scene.History.Position);
        Assert.Same(tree.Options, tree.Mesh!.Options); // regenerated at once for the LOD table
        Assert.Contains("<option value=\"Pine Medium\" selected=\"\">", Header(tree), StringComparison.Ordinal);

        _editor.Scene.History.Undo();
        _editor.Tick();
        Assert.Equal("Oak Medium", tree.Preset);
        Assert.Same(oak, tree.Options);
        _editor.Scene.History.Redo();
        Assert.Equal("Pine Medium", tree.Preset);

        W.Inspector.RunHeaderAction(Tree3DInspector.PresetInput + "=");
        Assert.Equal("", tree.Preset);
        Assert.NotNull(tree.Options);
        Assert.Contains("<option value=\"\" selected=\"\">Custom</option>", Header(tree), StringComparison.Ordinal);
        W.Inspector.RunHeaderAction(Tree3DInspector.PresetInput + "=No Such Tree");
        Assert.Equal("", tree.Preset);
    }

    [Fact]
    public void TheDiceRollsANewSeedAsOneUndoStep()
    {
        var tree = AddTree();
        var inspector = (Tree3DInspector)CustomInspectors.Find(typeof(Tree3D))!;
        inspector.Dice = new Random(5);
        var expected = new Random(5).Next(0, 65536);
        var before = tree.Mesh;
        W.Inspector.RunHeaderAction(Tree3DInspector.DiceAction);
        Assert.Equal(expected, tree.Seed);
        Assert.NotSame(before, tree.Mesh);
        Assert.Equal(expected, tree.Mesh!.Seed);
        Assert.Contains($"value=\"{expected}\"", Header(tree), StringComparison.Ordinal);
        _editor.Scene.History.Undo();
        Assert.Equal(-1, tree.Seed);

        W.Inspector.RunHeaderAction(Tree3DInspector.SeedInput + "=4242");
        Assert.Equal(4242, tree.Seed);
        W.Inspector.RunHeaderAction(Tree3DInspector.SeedInput + "=oak"); // not a number: nothing
        Assert.Equal(4242, tree.Seed);
    }

    [Fact]
    public void TheStyleToggleSwitchesTheMeshes()
    {
        var tree = AddTree();
        Assert.Contains("tool-button active\" data-action=\"" + Tree3DInspector.RealisticAction, Header(tree), StringComparison.Ordinal);
        var realistic = Tree3DInspector.Counts(tree.Mesh!.Lods[0]).Triangles;
        W.Inspector.RunHeaderAction(Tree3DInspector.LowPolyAction);
        Assert.Equal(TreeStyle.LowPoly, tree.Style);
        Assert.Equal(TreeStyle.LowPoly, tree.Mesh!.Style);
        Assert.True(Tree3DInspector.Counts(tree.Mesh.Lods[0]).Triangles < realistic);
        Assert.Contains("tool-button active\" data-action=\"" + Tree3DInspector.LowPolyAction, Header(tree), StringComparison.Ordinal);
        _editor.Scene.History.Undo();
        Assert.Equal(TreeStyle.Realistic, tree.Style);
    }

    [Fact]
    public void TheLodTableCountsEachLevelAndItsEyeForcesIt()
    {
        var tree = AddTree();
        var header = Header(tree);
        for (var l = 0; l < 3; l++)
        {
            var (triangles, vertices) = Tree3DInspector.Counts(tree.Mesh!.Lods[l]);
            Assert.Contains($">{triangles.ToString("N0", CultureInfo.InvariantCulture)}</span>", header, StringComparison.Ordinal);
            Assert.Contains($">{vertices.ToString("N0", CultureInfo.InvariantCulture)}</span>", header, StringComparison.Ordinal);
            Assert.Contains(Tree3DInspector.LodAction + l, header, StringComparison.Ordinal);
        }

        Assert.Contains("Impostor", header, StringComparison.Ordinal);
        Assert.Contains("0–30 m", header, StringComparison.Ordinal);

        var history = _editor.Scene.History.Position;
        W.Inspector.RunHeaderAction(Tree3DInspector.LodAction + "2");
        Assert.Equal(2, tree.ForceLod);
        Assert.True(GeometryInstance3D.IsInVisibilityRange(1f, tree.GetLodNode(2).VisibilityRangeBegin, tree.GetLodNode(2).VisibilityRangeEnd));
        Assert.Contains("Level 2 forced", Header(tree), StringComparison.Ordinal);
        Assert.Equal(history, _editor.Scene.History.Position); // view state, not an edit
        W.Inspector.RunHeaderAction(Tree3DInspector.LodAction + "2");
        Assert.Equal(-1, tree.ForceLod);

        W.Inspector.RunHeaderAction(Tree3DInspector.WindAction);
        Assert.True(tree.GetWorld3D()!.PreviewWind);
        Assert.Contains("Wind Preview — on", Header(tree), StringComparison.Ordinal);
        W.Inspector.RunHeaderAction(Tree3DInspector.WindAction);
        Assert.False(tree.GetWorld3D()!.PreviewWind);
    }

    [Fact]
    public void BakeWritesTheMfresAndTheStatusFollowsTheEdits()
    {
        var tree = AddTree();
        tree.Seed = 321;
        Assert.StartsWith("Not baked", Tree3DInspector.Status(tree), StringComparison.Ordinal);
        Assert.Equal("Assets/Trees/oak_medium_321_realistic.mfres", Tree3DInspector.BakePath(tree));
        Assert.Contains("Bake meshes to .mfres", Header(tree), StringComparison.Ordinal);

        W.Inspector.RunHeaderAction(Tree3DInspector.BakeAction);
        var file = Path.Combine(_editor.Directory, "Assets", "Trees", "oak_medium_321_realistic.mfres");
        Assert.True(File.Exists(file));
        Assert.NotNull(tree.BakedMesh);
        Assert.Equal(TreeBakeStatus.UpToDate, tree.GetBakeStatus());
        Assert.True(tree.DrawsBake);
        Assert.StartsWith("Baked, up to date: oak_medium_321_realistic.mfres", Tree3DInspector.Status(tree), StringComparison.Ordinal);
        Assert.Contains("icon-circle-check", Header(tree), StringComparison.Ordinal);

        // An options edit: stale, and the view shows the live tree while the bake waits.
        var length = TypeRegistry.GetRequired(typeof(TreeLevel)).FindProperty(nameof(TreeLevel.Length))!;
        _editor.Scene.SetProperty(tree.Options!.Level[1], length, tree.Options.Level[1].Length * 0.7);
        _editor.Tick(2);
        Assert.Equal(TreeBakeStatus.Stale, tree.GetBakeStatus());
        Assert.False(tree.DrawsBake);
        Assert.StartsWith("Stale", Tree3DInspector.Status(tree), StringComparison.Ordinal);
        Assert.Contains("icon-alert-triangle", Header(tree), StringComparison.Ordinal);

        Assert.Equal("Assets/Trees/oak_medium_321_realistic.mfres", Tree3DInspector.BakePath(tree)); // a re-bake overwrites its file
        W.Inspector.RunHeaderAction(Tree3DInspector.BakeAction);
        Assert.Equal(TreeBakeStatus.UpToDate, tree.GetBakeStatus());
        Assert.Single(System.IO.Directory.GetFiles(Path.GetDirectoryName(file)!, "*.mfres"));
        var rebaked = tree.BakedMesh;
        ResourceLoader.ClearCache();
        var loaded = ResourceLoader.Load<TreeMesh>("Assets/Trees/oak_medium_321_realistic.mfres");
        Assert.Equal(rebaked!.BakeHash, loaded.BakeHash);
        Assert.True(loaded.IsCurrentFor(tree.Options, 321, TreeStyle.Realistic));

        _editor.Scene.History.Undo(); // the second bake
        Assert.Equal(TreeBakeStatus.Stale, tree.GetBakeStatus());
        _editor.Scene.History.Undo(); // the edit: the first bake is current again
        Assert.Equal(TreeBakeStatus.UpToDate, tree.GetBakeStatus());
        _editor.Scene.History.Undo(); // the first bake
        Assert.Equal(TreeBakeStatus.NotBaked, tree.GetBakeStatus());
        Assert.Null(tree.BakedMesh);
    }

    [Fact]
    public void ImportReadsEzTreeJsonAsOneUndoStep()
    {
        var tree = AddTree();
        var context = _editor.Scene;
        const string json = """{ "seed": 9, "type": "evergreen", "branch": { "levels": 2, "length": { "0": 30 } }, "leaves": { "count": 3 } }""";
        Assert.True(Tree3DInspector.ImportEzTree(tree, json, "my-pine", context, out var error), error);
        Assert.Equal("", tree.Preset);
        Assert.Equal(TreeType.Evergreen, tree.Options!.Type);
        Assert.Equal(2, tree.Options.Levels);
        Assert.Equal(30, tree.Options.Level[0].Length);
        Assert.Equal("my-pine", tree.Options.ResourceName);
        Assert.Null(Tree3DInspector.PresetOf(tree));
        _editor.Scene.History.Undo();
        Assert.Equal("Oak Medium", tree.Preset);

        Assert.False(Tree3DInspector.ImportEzTree(tree, "not json", "x", context, out error));
        Assert.NotNull(error);
        Assert.Equal("Oak Medium", tree.Preset);
    }

    [Fact]
    public void LevelsAreNamedAndOnlyTheirFieldsShow()
    {
        var tree = AddTree();
        var options = tree.Options!;
        Assert.Equal("Level 0 · trunk", TreeOptionsInspector.LevelLabel(options, 0));
        Assert.Equal("Level 2", TreeOptionsInspector.LevelLabel(options, 2));
        Assert.StartsWith($"{options.Level[0].Children} children · length", TreeOptionsInspector.LevelSummary(options, 0), StringComparison.Ordinal);
        Assert.Contains("°", TreeOptionsInspector.LevelSummary(options, 1), StringComparison.Ordinal);
        Assert.DoesNotContain("children", TreeOptionsInspector.LevelSummary(options, 3), StringComparison.Ordinal);
        options.Levels = 2;
        Assert.Equal("Level 3 · unused", TreeOptionsInspector.LevelLabel(options, 3));
        Assert.Equal("", TreeOptionsInspector.LevelSummary(options, 3));
        Assert.DoesNotContain("children", TreeOptionsInspector.LevelSummary(options, 2), StringComparison.Ordinal);
        options.Levels = 3;

        var level = TypeRegistry.GetRequired(typeof(TreeLevel));
        var inspector = CustomInspectors.Find(typeof(TreeLevel))!;
        bool Shows(int index, string name) => inspector.ShowProperty(options.Level[index], level.FindProperty(name)!);
        Assert.False(Shows(0, nameof(TreeLevel.Angle)));
        Assert.False(Shows(0, nameof(TreeLevel.Start)));
        Assert.True(Shows(0, nameof(TreeLevel.Children)));
        Assert.True(Shows(1, nameof(TreeLevel.Angle)));
        Assert.True(Shows(2, nameof(TreeLevel.Children)));
        Assert.False(Shows(3, nameof(TreeLevel.Children)));
        Assert.True(Shows(3, nameof(TreeLevel.Start)));

        // In the panel: Options › Branches › Level [0] shows the trunk's rows under its name, without Angle.
        W.Inspector.RunAction(W.Inspector.Rows.ToList().FindIndex(r => r.Name == nameof(Tree3D.Options)), "res-edit");
        W.Inspector.ToggleSection("nested:TreeOptions/Branches");
        var levelRow = W.Inspector.Rows.ToList().FindIndex(r => r.Name == nameof(TreeOptions.Level));
        Assert.True(levelRow >= 0);
        W.Inspector.RunAction(levelRow, "elem-edit", 0);
        _editor.Tick();
        var names = W.Inspector.Rows.Select(r => r.Name).ToList();
        Assert.Contains(nameof(TreeLevel.Gnarliness), names);
        Assert.DoesNotContain(nameof(TreeLevel.Angle), names);
        var body = W.Inspector.Document.GetElementById("inspector-body").InnerRml;
        Assert.Contains("Level 0 · trunk", body, StringComparison.Ordinal);
        Assert.Contains(TreeOptionsInspector.LevelSummary(options, 0), body, StringComparison.Ordinal);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void EveryHeaderControlIsAnIconWithATooltip()
    {
        var tree = AddTree();
        var header = Header(tree);
        var buttons = Button().Matches(header);
        Assert.True(buttons.Count >= 8, $"{buttons.Count} buttons");
        foreach (Match button in buttons)
        {
            Assert.Contains("data-tooltip=\"", button.Value, StringComparison.Ordinal);
            Assert.Matches("<span class=\"icon icon-sm icon-[a-z0-9-]+\"></span></button>$", button.Value);
            foreach (Match icon in IconName().Matches(button.Value))
                IconAtlas.AssertExists(icon.Groups[1].Value, "Tree3DInspector");
        }

        foreach (var icon in new[] { "file-import", "dice-5", "photo", "vector-triangle", "wind", "eye-off", "package" })
            Assert.Contains("icon-" + icon + "\"", header, StringComparison.Ordinal);

        // The panel shows it without RmlUi warnings, every control titled.
        var body = W.Inspector.Document.GetElementById("inspector-body");
        Assert.Contains("tree-insp", body.InnerRml, StringComparison.Ordinal);
        Assert.Empty(_editor.RmlMessages);
    }

    [GeneratedRegex("<button[^>]*>.*?</button>")]
    private static partial Regex Button();

    [GeneratedRegex("icon-([a-z0-9-]+)\"")]
    private static partial Regex IconName();
}
