using MainframeEngine.Editor.Tests.Projects;
using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Projects;

namespace MainframeEngine.Editor.Tests.Video;

/// <summary>
/// Video mode needs no game project, so opening, closing or switching a project leaves video tabs, and the jobs running on
/// them, alone: only the game mode's tabs are asked about and closed.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class VideoProjectSwitchTests : IDisposable
{
    private static readonly FfmpegLocation NoFfmpeg = new("/x/ffmpeg", "/x/ffprobe", "/x", FfmpegLocationSource.Path);
    private readonly TestProject _project = new();
    private readonly TestProject _other = new("Other");
    private readonly FakeGameBuilder _builder = new();
    private readonly FakeGameLauncher _launcher = new();
    private readonly List<string> _dirs = [];
    private readonly HeadlessEditor _editor;

    public VideoProjectSwitchTests()
    {
        _editor = new HeadlessEditor(configure: o => o with { InitialProject = _project.Root, GameBuilder = _builder, GameLauncher = _launcher });
        _editor.Tick(3);
    }

    private EditorWorkspace W => _editor.Workspace;

    public void Dispose()
    {
        _editor.Dispose();
        _project.Dispose();
        _other.Dispose();
        foreach (var dir in _dirs)
            Directory.Delete(dir, recursive: true);
    }

    // A video tab with a silence job that runs until it is cancelled.
    private (VideoTab Tab, VideoJob<IReadOnlyList<SilenceSpan>> Job) OpenVideoWithAJob()
    {
        var dir = Directory.CreateTempSubdirectory("video-switch-").FullName;
        _dirs.Add(dir);
        var video = Path.Combine(dir, "trim.mp4");
        File.WriteAllBytes(video, [0]);
        var path = Path.Combine(dir, "trim.mfcut");
        ProjectSerializer.SaveAsync(VideoDocumentTests.Project(video), path, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        var tab = W.Session.OpenVideo(path, NoFfmpeg, 100);
        var job = VideoJob.Start<IReadOnlyList<SilenceSpan>>("silence", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        });
        tab.SilenceJob = job;
        _editor.Tick(2);
        return (tab, job);
    }

    private void TickUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            _editor.Tick();
            Thread.Sleep(5);
        }

        Assert.True(condition());
    }

    [Fact]
    public void ClosingAndOpeningProjectsKeepsVideoTabsAndTheirJobs()
    {
        Assert.Equal(_project.Root, W.Session.ProjectRoot);
        var (tab, job) = OpenVideoWithAJob();

        W.Commands.Execute("project.close");
        Assert.True(W.ProjectManager.Visible);
        Assert.Empty(W.Session.Scenes);
        Assert.Same(tab, Assert.Single(W.Session.Tabs));
        Assert.Same(job, tab.SilenceJob);
        Assert.False(job.IsCancelled);

        // Another project opens beside the video tab (the session no longer refuses: no game tab is open).
        W.Commands.OpenProject(_other.Root);
        TickUntil(() => W.Session.Scenes.Count == 1);
        Assert.Equal(_other.Root, W.Session.ProjectRoot);
        Assert.Contains(tab, W.Session.Tabs);
        Assert.False(job.IsCancelled);
        Assert.False(job.IsDone);

        // Through the Project Manager too.
        W.Commands.Execute("project.manager");
        Assert.True(W.ProjectManager.Visible);
        Assert.Contains(tab, W.Session.Tabs);
        Assert.False(job.IsCancelled);
    }

    [Fact]
    public void ADirtySceneStillPromptsButADirtyVideoTabIsNotAskedAbout()
    {
        var (tab, job) = OpenVideoWithAJob();
        Assert.True(tab.Document.Editor.CreateSection(10, 20, anchor: 10));
        Assert.True(tab.IsDirty);
        var scene = W.Session.Scenes[0];
        W.Session.Activate(scene);
        scene.AddNode(new Node3D { Name = "Added" }, scene.Root);
        Assert.True(scene.IsDirty);

        W.Commands.Execute("project.close");
        Assert.Equal("Close Project", W.Message.Current?.Title);
        Assert.Contains(scene.DisplayName, W.Message.Current!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(tab.DisplayName, W.Message.Current.Message, StringComparison.Ordinal);
        W.Message.Answer(1); // Don't Save

        Assert.Empty(W.Session.Scenes);
        Assert.Same(tab, Assert.Single(W.Session.Tabs));
        Assert.True(tab.IsDirty); // neither saved nor discarded
        Assert.False(job.IsCancelled);
    }

    [Fact]
    public void QuitStillAsksAboutADirtyVideoTab()
    {
        var (tab, _) = OpenVideoWithAJob();
        Assert.True(tab.Document.Editor.CreateSection(10, 20, anchor: 10));

        W.RequestQuit();
        Assert.Contains(tab.DisplayName, W.Message.Current!.Message, StringComparison.Ordinal);
        W.Message.Answer(2); // Cancel
    }
}
