using System.Net;
using MainframeEngine.Editor.Tests.Projects.Demo;
using MainframeEngine.Editor.Tests.Updates;

namespace MainframeEngine.Editor.Tests.Projects;

/// <summary>The Download Demo dialog headless: validation, download + open, retry, no engine checkout.</summary>
[Collection(nameof(SerialEditor))]
public sealed class DownloadDemoWorkflowTests : IDisposable
{
    private readonly string _directory = GameProjectLayout.RealPath(Directory.CreateTempSubdirectory("mf-dl-demo").FullName);
    private HeadlessEditor? _editor;

    public void Dispose()
    {
        _editor?.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private HeadlessEditor Editor(StubHandler handler) => _editor = new HeadlessEditor(configure: o => o with
    {
        ShowProjectManager = true,
        GameBuilder = new FakeGameBuilder(),
        GameLauncher = new FakeGameLauncher(),
        DemoHttpHandler = () => handler,
        DemoDownloadsDirectory = Path.Combine(_directory, "downloads"),
    });

    private void Click(string id)
    {
        var button = _editor!.Workspace.DownloadDemo.Document.GetElementById(id);
        Assert.False(button.IsNull, id);
        button.Click();
        _editor.Tick();
    }

    private void TickUntil(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            _editor!.Tick();
            Thread.Sleep(5);
        }

        Assert.True(condition(), "Timed out. Output: " + string.Join(" | ", _editor!.Workspace.Output.Messages.TakeLast(12).Select(m => $"[{m.Category}] {m.Text}")));
    }

    private byte[] DemoZip() => File.ReadAllBytes(DemoZips.Write(Path.Combine(_directory, "demo.zip"), DemoZips.ValidProject()));

    [Fact]
    public void TheProjectManagerButtonOpensTheDialog()
    {
        var editor = Editor(StubHandler.Bytes([]));
        var w = editor.Workspace;
        w.ProjectManager.Open();
        editor.Tick(3);
        Assert.False(w.DownloadDemo.Visible);
        w.ProjectManager.Document.GetElementById("pm-download-demo").Click();
        editor.Tick(2);
        Assert.True(w.DownloadDemo.Visible);
        Assert.Empty(editor.RmlMessages);
    }

    [Fact]
    public void DownloadOpensTheDemoAndAddsItToRecentProjects()
    {
        var editor = Editor(StubHandler.Bytes(DemoZip()));
        var w = editor.Workspace;
        w.ProjectManager.Open();
        editor.Tick(3);

        w.DownloadDemo.Open();
        w.DownloadDemo.Location = _directory;
        editor.Tick(2);
        Click("dd-download");
        TickUntil(() => w.Project.Root is not null, seconds: 30);

        var destination = Path.Combine(_directory, "MainframeEngine.Demo");
        Assert.Equal(destination, w.Project.Root);
        Assert.Contains(w.RecentProjects.Items, p => p.Path == destination);
        Assert.False(w.DownloadDemo.Visible);
        Assert.Empty(editor.RmlMessages);
    }

    [Fact]
    public void ExistingNonEmptyDestinationIsRefusedUpFront()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "MainframeEngine.Demo", "x"));
        var handler = StubHandler.Bytes([]);
        var editor = Editor(handler);
        editor.Workspace.DownloadDemo.Open();
        editor.Workspace.DownloadDemo.Location = _directory;
        editor.Tick(3);
        Assert.NotEqual("", editor.Workspace.DownloadDemo.Error);
        Click("dd-download");
        editor.Tick(3);
        Assert.Empty(handler.Requests);
        Assert.False(editor.Workspace.DownloadDemo.Busy);
    }

    [Fact]
    public void AFailedDownloadCanBeRetried()
    {
        var calls = 0;
        var zip = DemoZip();
        var handler = new StubHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
        var editor = Editor(handler);
        var w = editor.Workspace;
        w.DownloadDemo.Open();
        w.DownloadDemo.Location = _directory;
        editor.Tick(2);
        Click("dd-download");
        TickUntil(() => w.DownloadDemo.Failure != "", seconds: 10);
        Assert.False(w.DownloadDemo.Busy);
        Assert.Equal("", w.DownloadDemo.Error); // no validation error: the button stays enabled
        Click("dd-download");
        TickUntil(() => w.Project.Root is not null, seconds: 30);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void WithoutAnEngineCheckoutDownloadIsDisabled()
    {
        var handler = StubHandler.Bytes([]);
        var editor = Editor(handler);
        editor.Workspace.DownloadDemo.EngineCheckoutOverride = ""; // a packaged editor without a checkout
        editor.Workspace.DownloadDemo.Open();
        editor.Tick(3);
        Assert.Contains("engine", editor.Workspace.DownloadDemo.Error, StringComparison.OrdinalIgnoreCase);
        Click("dd-download");
        editor.Tick(3);
        Assert.Empty(handler.Requests);
    }
}
