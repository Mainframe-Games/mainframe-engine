using System.Diagnostics;
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
    }

    public string Root { get; }

    public string ScenePath => Abs("Content/Scenes/Main.mscene");

    public string DocumentPath => Abs("Content/UI/hud.rml");

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
