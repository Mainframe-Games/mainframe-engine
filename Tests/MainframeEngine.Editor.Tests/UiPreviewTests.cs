using System.Diagnostics;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests;

/// <summary>A project with a scene and a UI document linking style sheets (rooted, relative, missing, via a template).</summary>
public sealed class UiPreviewProject : IDisposable
{
    public const string Document = """
        <rml>
        <head>
            <title>HUD</title>
            <link type="text/rcss" href="/Content/UI/theme.rcss"/>
            <link type="text/rcss" href="hud.rcss"/>
            <!-- <link type="text/rcss" href="commented.rcss"/> -->
            <link type='text/rcss' href='missing.rcss'/>
            <link type="text/template" href="panel.rml"/>
            <style>body { width: 100%; height: 100%; }</style>
        </head>
        <body><div id="score">Score</div></body>
        </rml>
        """;

    /// <summary>A HUD bound to a data model game code would create.</summary>
    public const string DataDocument = """
        <rml>
        <head>
            <style>body { width: 100%; height: 100%; font-family: LatoLatin; } /* {{ notbound }} */</style>
        </head>
        <body data-model="hud">
            <p id="score">{{ score }}</p>
            <p id="hp">{{ hp | format(1) }}</p>
            <p id="dead" data-if="!alive">dead</p>
            <p id="ok" data-if="alive && ready">alive</p>
            <div id="items"><p data-for="item, i : inventory">{{ item.name }} #{{ i }}</p></div>
            <input id="name" type="text" data-value="player.name"/>
            <input type="range" min="0" max="1" data-value="volume"/>
            <button id="buy" data-event-click="buy(item_count, ev.value); clicks = clicks + 1">Buy</button>
            <p data-attr-title="player.title | to_upper">title</p>
            <select data-value="accent"><option value="#2563eb">Blue</option><option value="#16a34a">Green</option></select>
            <div id="swatch" data-style-background-color="accent" data-style-width="size_px"></div>
            <img data-attr-src="player.icon"/>
        </body>
        </rml>
        """;

    public UiPreviewProject()
    {
        Root = Directory.CreateTempSubdirectory("mf-ui-preview").FullName;
        new ProjectSettings { Name = "PreviewGame", MainScene = "Content/Scenes/Main.mscene" }.Save(Path.Combine(Root, ProjectSettings.FileName));
        Directory.CreateDirectory(Path.Combine(Root, "Content", "Scenes"));
        Directory.CreateDirectory(Path.Combine(Root, "Content", "UI", "widgets"));
        SceneSaver.Save(new Node3D { Name = "Main" }, ScenePath);
        File.WriteAllText(Abs("Content/UI/theme.rcss"), "body { font-family: LatoLatin; }");
        File.WriteAllText(Abs("Content/UI/hud.rcss"), "#score { color: #ffffff; }");
        File.WriteAllText(Abs("Content/UI/panel.rml"),
            """<template name="panel" content="content"><head><link type="text/rcss" href="widgets/panel.rcss"/></head><body><div id="content"/></body></template>""");
        File.WriteAllText(Abs("Content/UI/widgets/panel.rcss"), "div { display: block; }");
        File.WriteAllText(DocumentPath, Document);
        File.WriteAllText(DataDocumentPath, DataDocument);
    }

    public string Root { get; }

    public string ScenePath => Abs("Content/Scenes/Main.mscene");

    public string DocumentPath => Abs("Content/UI/hud.rml");

    public string DataDocumentPath => Abs("Content/UI/hud-data.rml");

    public string Abs(string projectPath) => Path.Combine(Root, projectPath.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

[Collection(nameof(SerialEditor))]
public sealed class UiPreviewTests : IDisposable
{
    private readonly UiPreviewProject _project = new();
    private readonly HeadlessEditor _editor;

    public UiPreviewTests()
    {
        _editor = new HeadlessEditor(_project.ScenePath);
    }

    public void Dispose()
    {
        _editor.Dispose();
        _project.Dispose();
    }

    private EditorWorkspace W => _editor.Workspace;

    private UiPreview OpenPreview()
    {
        var preview = W.Session.OpenUiPreview(_project.DocumentPath);
        _editor.Tick(2);
        return preview;
    }

    [Fact]
    public void LinksResolveLikeTheUiServerAndIncludeTheTemplatesStyleSheets()
    {
        var links = RmlLinks.Parse(_project.DocumentPath);

        Assert.Equal(["/Content/UI/theme.rcss", "hud.rcss", "missing.rcss", "panel.rml", "widgets/panel.rcss"], links.Links.Select(l => l.Href));
        Assert.Equal(_project.Abs("Content/UI/theme.rcss"), links.Links[0].FullPath);
        Assert.Equal(_project.Abs("Content/UI/hud.rcss"), links.Links[1].FullPath);
        Assert.False(links.Links[2].Exists);
        Assert.Equal(RmlLinkKind.Template, links.Links[3].Kind);
        Assert.Equal(_project.Abs("Content/UI/widgets/panel.rcss"), links.Links[4].FullPath);
        Assert.Equal("panel.rml", links.Links[4].Via);
        Assert.True(links.HasInlineStyle);
        Assert.Same(RmlLinks.Empty, RmlLinks.Parse(_project.Abs("Content/UI/nope.rml")));
    }

    [Fact]
    public void APreviewIsATabBesideTheScenesAndShowsItsDocumentInTheViewArea()
    {
        var scene = W.Session.Active!;
        var preview = OpenPreview();

        Assert.Equal([scene, preview], W.Session.Tabs);
        Assert.Equal([scene], W.Session.Scenes);
        Assert.Same(preview, W.Session.ActiveTab);
        Assert.Null(W.Session.Active); // no scene: the scene tree, inspector and scene commands rest
        Assert.Equal($"hud.rml — PreviewGame — {EditorBrand.NameWithVersion}", _editor.Host.Title);

        // Its layer is a live (not inert) layer between the panels and the dialogs, laid out in the view area.
        Assert.Contains(preview.Layer, _editor.Server.Layers);
        Assert.True(preview.Layer.Visible);
        Assert.Equal(W.PreviewRegion, preview.Layer.Region);
        Assert.True(preview.Document.IsLoaded);
        Assert.Equal("Score", preview.Document.GetElementById("score")!.InnerRml);
        Assert.Equal(W.Layout.PreviewImage.Width, preview.Document.GetElementById("score")!.Bounds.Width, 1);

        // Switching tabs hides it; closing it activates its neighbour.
        W.Session.Activate(scene);
        _editor.Tick();
        Assert.False(preview.Layer.Visible);
        W.Session.Activate(preview);
        _editor.Key(Key.W, Key.ControlLeft);
        Assert.Equal([scene], W.Session.Tabs);
        Assert.Same(scene, W.Session.ActiveTab);
        Assert.True(preview.Layer.IsFreed);
    }

    [Fact]
    public void ThePreviewUsesTheGamesUiScaleAndThePanelsTheDisplays()
    {
        // ADR 0181: the previewed document scales like the game would in a window the preview's size; the editor's own
        // layers stay at the display's scale (the editor's UI server has no game scale).
        var preview = OpenPreview();
        var region = preview.Layer.Region!.Value;
        Assert.Equal(UiScaling.ScaleWithScreenSize, preview.Layer.Scaling); // the new project's default
        Assert.Equal(region.Height / 1080f, preview.Layer.Context!.DensityIndependentPixelRatio, 4);
        Assert.Equal(UiScaling.ConstantPixelSize, _editor.Server.Scaling);
        foreach (var layer in (UiLayer[])[W.PanelLayer, W.DialogLayer, W.TooltipLayer, W.ProjectLayer])
        {
            Assert.Equal(UiScaleMode.Dpi, layer.ScaleMode);
            Assert.Equal(_editor.Server.ContentScale, layer.Context!.DensityIndependentPixelRatio);
        }

        // Saving the project settings applies at once.
        var settings = ProjectSettings.Load(_project.Root);
        settings.Ui.ScaleMode = UiScalingMode.ConstantPixelSize;
        W.Session.SetProjectSettings(settings);
        _editor.Tick();
        Assert.Equal(UiScalingMode.ConstantPixelSize, preview.Layer.Scaling!.Mode);
        Assert.Equal(_editor.Server.ContentScale, preview.Layer.Context.DensityIndependentPixelRatio);
    }

    [Fact]
    public void OpeningAnRmlFromTheFileSystemPreviewsItAndAgainActivatesTheTab()
    {
        var entry = W.FileSystem.Files!.Find("Content/UI/hud.rml")!;
        W.FileSystem.Open(entry);
        _editor.Tick();
        var preview = Assert.IsType<UiPreview>(W.Session.ActiveTab);
        Assert.Equal(_project.DocumentPath, preview.FilePath);

        W.Session.Activate(W.Session.Scenes[0]);
        W.FileSystem.Open(entry);
        Assert.Same(preview, W.Session.ActiveTab);
        Assert.Equal(2, W.Session.Tabs.Count);
    }

    [Fact]
    public void SavingALinkedStyleSheetOrTheDocumentReloadsThePreview()
    {
        var preview = OpenPreview();
        var reloads = 0;
        preview.Document.Reloaded += () => reloads++;

        // The document gains a link: it reloads and the links are re-read.
        File.WriteAllText(_project.DocumentPath, UiPreviewProject.Document.Replace("<div id=\"score\">Score</div>", "<div id=\"score\">42</div>", StringComparison.Ordinal)
            .Replace("<style>", "<link type=\"text/rcss\" href=\"extra.rcss\"/><style>", StringComparison.Ordinal));
        WaitUntil(() => reloads > 0);
        Assert.Equal("42", preview.Document.GetElementById("score")!.InnerRml);
        Assert.Contains(preview.Links.Links, l => l.Href == "extra.rcss");

        // A style sheet: re-read in place (the DOM stays).
        var version = preview.Version;
        File.AppendAllText(_project.Abs("Content/UI/hud.rcss"), "\n#score { color: #ff0000; }");
        _editor.Tick();
        preview.Reload(UiReloadKind.StyleSheets);
        Assert.Equal(1, reloads);
        Assert.Equal(version, preview.Version);
        Assert.DoesNotContain(_editor.RmlMessages, m => !m.Contains("missing.rcss", StringComparison.Ordinal) && !m.Contains("extra.rcss", StringComparison.Ordinal));
    }

    [Fact]
    public void MovingTheDocumentTakesThePreviewAlong()
    {
        var preview = OpenPreview();
        var moved = _project.Abs("Content/UI/hud2.rml");
        File.Move(_project.DocumentPath, moved);
        W.Session.FilesMoved(_project.DocumentPath, moved);
        _editor.Tick();

        Assert.Equal(moved, preview.FilePath);
        Assert.Equal(moved, preview.Document.Source);
        Assert.Equal("hud2.rml", preview.Title);
        Assert.True(preview.Document.IsLoaded);
    }

    [Fact]
    public void TheSceneBehindTheUiIsOffByDefaultAndRendersThroughTheController()
    {
        var scene = W.Session.Active!;
        var preview = OpenPreview();

        Assert.False(preview.ShowBackdrop);
        Assert.Same(scene, preview.BackdropScene); // the last active scene is the default choice
        Assert.Null(W.Viewport.Backdrop);
        Assert.Equal(SubViewportUpdateMode.Disabled, scene.Viewport.UpdateMode);

        W.Session.SetBackdrop(preview, scene, show: true);
        _editor.Tick();
        Assert.Same(scene, W.Viewport.Backdrop);
        Assert.Equal(SubViewportUpdateMode.Always, scene.Viewport.UpdateMode);
        var pixels = W.PreviewRegion;
        Assert.Equal((pixels.Width, pixels.Height), (scene.Viewport.Width, scene.Viewport.Height));

        W.Session.SetBackdrop(preview, scene, show: false);
        _editor.Tick();
        Assert.Null(W.Viewport.Backdrop);
        Assert.Equal(SubViewportUpdateMode.Disabled, scene.Viewport.UpdateMode);

        // Closing the backdrop scene turns it off.
        W.Session.SetBackdrop(preview, scene, show: true);
        _editor.Tick();
        W.Session.Close(scene);
        _editor.Tick();
        Assert.False(preview.ShowBackdrop);
        Assert.Null(preview.BackdropScene);
        Assert.Null(W.Viewport.Backdrop);
    }

    [Fact]
    public void ACodeReloadPutsTheBackdropSceneBack()
    {
        var scene = W.Session.Active!;
        var preview = OpenPreview();
        W.Session.SetBackdrop(preview, scene, show: true);

        var snapshot = W.Session.Suspend(scene);
        Assert.Null(preview.BackdropScene);
        Assert.Equal(0, snapshot.Index);
        var resumed = W.Session.Resume(snapshot);

        Assert.Equal([resumed, preview], W.Session.Tabs);
        Assert.Same(resumed, preview.BackdropScene);
        Assert.True(preview.ShowBackdrop);
        Assert.Same(preview, W.Session.ActiveTab);
    }

    [Fact]
    public void TheScanFindsModelsVariablesShapesAndEvents()
    {
        var model = Assert.Single(RmlBindings.Parse(UiPreviewProject.DataDocument).Models);
        Assert.Equal("hud", model.Name);
        Assert.Equal(["accent", "alive", "clicks", "hp", "inventory", "item_count", "player", "ready", "score", "size_px", "volume"],
            model.Variables.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["buy"], model.Events);

        var inventory = model.Variables["inventory"];
        Assert.Equal(RmlVariableKind.Array, inventory.Kind);
        Assert.Equal(RmlVariableKind.Struct, inventory.Element!.Kind);
        Assert.Equal(["name"], inventory.Element.Members.Keys);
        Assert.Equal(["icon", "name", "title"], model.Variables["player"].Members.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(RmlValueUse.Style, model.Variables["accent"].Use); // a style value reads as text too, not the other way round
        Assert.Equal("background-color", model.Variables["accent"].StyleProperty);
        Assert.Equal(RmlValueUse.Path, model.Variables["player"].Members["icon"].Use);
        Assert.Equal(RmlValueUse.Number, model.Variables["hp"].Use);
        Assert.Equal(RmlValueUse.Number, model.Variables["volume"].Use);
        Assert.Equal(RmlValueUse.Bool, model.Variables["alive"].Use);
        Assert.Equal(RmlValueUse.Text, model.Variables["score"].Use);
        Assert.Empty(RmlBindings.Parse(UiPreviewProject.Document).Models);
    }

    [Fact]
    public void ADataBoundDocumentRendersWithStandInsAndNoBindingErrors()
    {
        var before = _editor.RmlMessages.Count;
        var preview = W.Session.OpenUiPreview(_project.DataDocumentPath);
        _editor.Tick(3);

        Assert.Equal(["hud"], preview.Data.Models.Select(m => m.Name));
        Assert.Empty(_editor.RmlMessages.Skip(before));
        var document = preview.Document;
        Assert.Contains("score", document.GetElementById("score")!.InnerRml, StringComparison.Ordinal);
        Assert.Contains("0.0", document.GetElementById("hp")!.InnerRml, StringComparison.Ordinal);
        Assert.Contains("name", document.GetElementById("items")!.InnerRml, StringComparison.Ordinal);
        Assert.Contains("#2", document.GetElementById("items")!.InnerRml, StringComparison.Ordinal);
        Assert.DoesNotContain($"#{UiPreviewData.ListSize}", document.GetElementById("items")!.InnerRml, StringComparison.Ordinal);
        Assert.Equal("name", document.GetElementById("name")!.Value);
        Assert.Equal(0, document.GetElementById("dead")!.Bounds.Height); // conditions are true: !alive hides it
        Assert.True(document.GetElementById("ok")!.Bounds.Height > 0);

        // The document gains a variable: the stand-ins are rebuilt with it.
        File.WriteAllText(_project.DataDocumentPath, UiPreviewProject.DataDocument.Replace("{{ score }}", "{{ score }} {{ combo }}", StringComparison.Ordinal));
        preview.Reload();
        _editor.Tick(2);
        Assert.Contains("combo", document.GetElementById("score")!.InnerRml, StringComparison.Ordinal);
        Assert.Empty(_editor.RmlMessages.Skip(before));

        W.Session.Close(preview);
        Assert.Empty(preview.Data.Models);
    }

    private void WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "The preview did not reload after its file changed.");
            Thread.Sleep(20);
            _editor.Tick();
        }
    }
}
