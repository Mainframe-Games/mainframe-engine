using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor.Tests;

/// <summary>
/// ADR 0169 in the editor: a <see cref="WorldEnvironment"/>'s <see cref="PostProcessProfile"/> edited inline (collapsible
/// groups, live), created and saved as a <c>.mres</c>, a resource file edited in place and saved with the scene, and the
/// viewport's Preview Post-Processing toggle.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class PostProcessEditorTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    private int Row(string name, object? target = null) =>
        W.Inspector.Rows.ToList().FindIndex(r => r.Name == name && (target is null || ReferenceEquals(r.Target, target)));

    private RmlElement Find(string selector) => W.Inspector.Document.GetElementById("inspector-body").QuerySelector(selector);

    private WorldEnvironment AddEnvironment(PostProcessProfile? profile, CameraAttributesPractical? lens = null)
    {
        var environment = new WorldEnvironment { Name = "Environment", PostProcess = profile, CameraAttributes = lens };
        _editor.Scene.AddNode(environment, _editor.Scene.Root);
        _editor.Tick();
        _editor.Scene.Selection.Set(environment);
        _editor.Tick();
        return environment;
    }

    [Fact]
    public void TheProfileOpensInlineWithCollapsibleGroupsAndEditsApplyLive()
    {
        var profile = new PostProcessProfile();
        var environment = AddEnvironment(profile);
        Assert.True(Row("PostProcess") >= 0);
        Assert.True(Row("CameraAttributes") >= 0); // the lens next to it, in the Post-Processing group

        W.Inspector.RunAction(Row("PostProcess"), "res-edit");
        _editor.Tick();
        foreach (var group in (string[])["Tonemap", "Auto Exposure", "Glow", "Light Shafts", "SSAO", "Adjustments"])
            Assert.False(Find($"[data-section=\"nested:PostProcessProfile/{group}\"]").IsNull, group);
        Assert.Equal(-1, Row("SsaoEnabled", profile)); // groups of a nested resource start closed

        W.Inspector.ToggleSection("nested:PostProcessProfile/SSAO");
        _editor.Tick();
        var ssao = Row("SsaoEnabled", profile);
        Assert.True(ssao > Row("PostProcess"));
        Assert.Equal(-1, Row("GlowEnabled", profile)); // only the opened group

        var changes = 0;
        profile.Changed += () => changes++;
        W.Inspector.Commit(Row("SsaoRadius", profile), 0, "0.75");
        Assert.Equal(0.75f, profile.SsaoRadius);
        Assert.Equal(0.75f, environment.PostProcessSettings.SsaoRadius); // live: the next frame renders it
        Assert.True(changes > 0);
        Assert.Matches("(?i)ssao ?radius", _editor.Scene.History.UndoAction!.Name);

        _editor.Scene.History.Undo();
        _editor.Tick();
        Assert.Equal(1f, profile.SsaoRadius);

        W.Inspector.ToggleSection("nested:PostProcessProfile/SSAO");
        _editor.Tick();
        Assert.Equal(-1, Row("SsaoRadius", profile));
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void NewCreatesAProfileAndSaveAsMovesItToAFileEveryUserShares()
    {
        var environment = AddEnvironment(null);
        W.Inspector.RunAction(Row("PostProcess"), "res-new"); // one concrete type: created at once
        _editor.Tick();
        var profile = Assert.IsType<PostProcessProfile>(environment.PostProcess);
        Assert.False(profile.IsExternal);
        profile.GlowEnabled = true;

        var second = new WorldEnvironment { Name = "Second", PostProcess = profile }; // the same inline profile, twice
        _editor.Scene.AddNode(second, _editor.Scene.Root);
        _editor.Tick();
        _editor.Scene.Selection.Set(environment);
        _editor.Tick();

        var path = Path.Combine(_editor.Directory, "Content", "PostProcess", "look.mres");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AssetDatabase.Current = new AssetDatabase(_editor.Directory);
        Assert.True(W.Inspector.SaveResourceAs(_editor.Scene, profile, path));
        _editor.Tick();
        Assert.True(File.Exists(path));
        var saved = Assert.IsType<PostProcessProfile>(environment.PostProcess);
        Assert.True(saved.IsExternal);
        Assert.True(saved.GlowEnabled);
        Assert.Same(saved, second.PostProcess); // every slot that held it
        Assert.Contains("look.mres", _editor.Scene.History.UndoAction!.Name, StringComparison.Ordinal);

        _editor.Scene.History.Undo();
        _editor.Tick();
        Assert.Same(profile, environment.PostProcess);
        Assert.Same(profile, second.PostProcess);
        Assert.True(File.Exists(path)); // the file stays, as in Godot
    }

    [Fact]
    public void AProfileFileEditedInPlaceIsSavedWithTheScene()
    {
        AssetDatabase.Current = new AssetDatabase(_editor.Directory);
        var file = Path.Combine(_editor.Directory, "Content", "PostProcess", "forest.mres");
        var uid = ResourceSaver.Save(new PostProcessProfile { AutoExposureEnabled = true }, file);
        var profile = ResourceLoader.Load<PostProcessProfile>(uid);
        AddEnvironment(profile);

        W.Inspector.RunAction(Row("PostProcess"), "res-edit"); // a .mres opens in place too
        _editor.Tick();
        W.Inspector.ToggleSection("nested:PostProcessProfile/Auto Exposure");
        _editor.Tick();
        Assert.True(W.Inspector.Commit(Row("AutoExposureScale", profile), 0, "0.25"));
        Assert.Contains(profile, _editor.Scene.EditedResourceFiles);
        Assert.True(_editor.Scene.IsDirty);

        var scene = Path.Combine(_editor.Directory, "Content", "Scenes", "Look.mscene");
        Directory.CreateDirectory(Path.GetDirectoryName(scene)!);
        W.Session.Save(_editor.Scene, scene);
        Assert.Contains("\"AutoExposureScale\": 0.25", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Contains(uid, File.ReadAllText(file), StringComparison.Ordinal); // the same UID: other scenes keep it
        Assert.DoesNotContain("AutoExposureScale", File.ReadAllText(scene), StringComparison.Ordinal);
        Assert.Empty(_editor.Scene.EditedResourceFiles);
        profile.Release();
    }

    [Fact]
    public void AResourceSlotsNameTakesTheRowsWidthAndItsPathIsInTheTooltip()
    {
        // The Forest's look in the inspector's default width: the name used to be squeezed to "fo" beside its buttons.
        AssetDatabase.Current = new AssetDatabase(_editor.Directory);
        var file = Path.Combine(_editor.Directory, "Content", "PostProcess", "forest.mres");
        var profile = ResourceLoader.Load<PostProcessProfile>(ResourceSaver.Save(new PostProcessProfile(), file));
        var lensFile = Path.Combine(_editor.Directory, "Content", "PostProcess", "a-lens-with-a-name-far-longer-than-the-inspector-is-wide.mres");
        var lens = ResourceLoader.Load<CameraAttributesPractical>(ResourceSaver.Save(new CameraAttributesPractical(), lensFile));
        AddEnvironment(profile, lens);

        foreach (var name in (string[])["PostProcess", "CameraAttributes"])
        {
            var row = Row(name);
            var label = Find($"#p{row}c0");
            var editor = label.Parent;
            var actions = Find($"[data-row=\"{row}\"][data-action=\"res-load\"]").Parent;
            Assert.True(editor.IsClassSet("res"), name);
            Assert.Equal("res-actions", actions.GetAttribute("class"));
            // The name spans the editor column; the buttons wrap onto their own line below it, right-aligned.
            Assert.InRange(label.Bounds.X, editor.Bounds.X - 0.5f, editor.Bounds.X + 0.5f);
            Assert.InRange(label.Bounds.Width, editor.Bounds.Width - 0.5f, editor.Bounds.Width + 0.5f);
            Assert.True(actions.Bounds.Y >= label.Bounds.Y + label.Bounds.Height, $"{name}: the buttons are below the name");
            Assert.InRange(actions.Bounds.X + actions.Bounds.Width, editor.Bounds.X + editor.Bounds.Width - 0.5f, editor.Bounds.X + editor.Bounds.Width + 0.5f);
        }

        // An empty slot's name and its two buttons fit on one line: they stay side by side.
        var sky = Row("Sky");
        var skyLabel = Find($"#p{sky}c0");
        var skyActions = Find($"[data-row=\"{sky}\"][data-action=\"res-load\"]").Parent;
        Assert.True(skyActions.Bounds.X >= skyLabel.Bounds.X + skyLabel.Bounds.Width - 0.5f);
        Assert.True(skyActions.Bounds.Y < skyLabel.Bounds.Y + skyLabel.Bounds.Height);

        // The full path in the tooltip (a long name is cut with an ellipsis, never spilling out of the row).
        Assert.Contains("Content/PostProcess/forest.mres", Find($"#p{Row("PostProcess")}c0").GetAttribute("data-tooltip"), StringComparison.Ordinal);
        Assert.Contains("PostProcessProfile", Find($"#p{Row("PostProcess")}c0").GetAttribute("data-tooltip"), StringComparison.Ordinal);
        Assert.Contains("a-lens-with-a-name-far-longer-than-the-inspector-is-wide.mres",
            Find($"#p{Row("CameraAttributes")}c0").GetAttribute("data-tooltip"), StringComparison.Ordinal);
        Assert.Empty(_editor.RmlMessages);
        profile.Release();
        lens.Release();
    }

    [Fact]
    public void ImportedResourcesDoNotOpenInPlace()
    {
        // A LUT (an imported .cube) keeps its data in the source file: neither Edit nor Save as .mres for it.
        AssetDatabase.Current = new AssetDatabase(_editor.Directory);
        var cube = Path.Combine(_editor.Directory, "Content", "Grading", "look.cube");
        Directory.CreateDirectory(Path.GetDirectoryName(cube)!);
        CubeLut.FromFunction(2, static c => c, "id").Save(cube);
        var lut = ResourceLoader.Load<Texture3D>("Content/Grading/look.cube");
        Assert.True(lut.IsExternal);
        var profile = new PostProcessProfile { AdjustmentColorCorrection = lut };
        AddEnvironment(profile);
        W.Inspector.RunAction(Row("PostProcess"), "res-edit");
        _editor.Tick();
        W.Inspector.ToggleSection("nested:PostProcessProfile/Adjustments");
        _editor.Tick();
        var row = Row("AdjustmentColorCorrection", profile);
        Assert.True(row >= 0);
        Assert.True(Find($"[data-row=\"{row}\"][data-action=\"res-edit\"]").IsNull);
        Assert.True(Find($"[data-row=\"{row}\"][data-action=\"res-save\"]").IsNull);
        Assert.False(Find($"[data-row=\"{Row("PostProcess")}\"][data-action=\"res-save\"]").IsNull); // the inline profile can be saved
        lut.Release();
    }

    [Fact]
    public void ThePreviewToggleSwitchesTheScenesPostProcessing()
    {
        _editor.Tick(2);
        var viewport = _editor.Scene.Viewport;
        Assert.True(W.Viewport.PostPreview);
        Assert.True(viewport.PostProcessing); // on by default in a 3D tab
        Assert.True(W.Toolbar.Document.GetElementById("tool-post").IsClassSet("active"));
        Assert.Contains("Post-Processing", W.Toolbar.Document.GetElementById("tool-post").GetAttribute("data-tooltip") ?? "", StringComparison.Ordinal);

        W.Commands.Execute("view.post");
        _editor.Tick(2);
        Assert.False(W.Viewport.PostPreview);
        Assert.False(viewport.PostProcessing);
        Assert.False(W.Toolbar.Document.GetElementById("tool-post").IsClassSet("active"));

        W.Commands.Execute("view.post");
        _editor.Tick(2);
        Assert.True(viewport.PostProcessing);

        // 2D tabs have no 3D post-processing.
        W.Commands.Execute("view.2d");
        _editor.Tick(2);
        Assert.False(viewport.PostProcessing);
    }
}
