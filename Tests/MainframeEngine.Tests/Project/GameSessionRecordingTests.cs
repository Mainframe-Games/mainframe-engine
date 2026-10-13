using MainframeEngine.EditorLink;
using MainframeEngine.Tests.Recording;

namespace MainframeEngine.Tests.Project;

/// <summary>Editor-link recording commands drive GameRecorder, and its events go back to the editor in order.</summary>
public sealed class GameSessionRecordingTests
{
    private static ProjectSettings Settings() => new() { Name = "T" };

    private static (GameSession Session, GameRecorder Recorder, FakeFrames Frames, List<EditorLinkRecording> Sent) Create()
    {
        var tree = new SceneTree();
        var session = new GameSession(tree, Settings());
        var (recorder, _, frames, _, _) = RecorderFakes.Create();
        var sent = new List<EditorLinkRecording>();
        session.RecordingSink = sent.Add;
        session.AttachRecorder(recorder);
        session.Start();
        return (session, recorder, frames, sent);
    }

    [Fact]
    public void StartRecordingCommandStartsTheRecorderAndReportsStarted()
    {
        var (session, recorder, _, sent) = Create();
        using var _ = session;

        session.Apply(new EditorLinkCommand(EditorCommandKind.StartRecording, "/r/a.mp4"));

        Assert.True(recorder.IsRecording);
        var started = Assert.Single(sent);
        Assert.Equal(EditorLinkRecordingState.Started, started.State);
        Assert.EndsWith("a.mp4", started.Path);
    }

    [Fact]
    public void StopRecordingCommandReportsStoppedOnceFinalised()
    {
        var (session, recorder, frames, sent) = Create();
        using var _ = session;
        session.Apply(new EditorLinkCommand(EditorCommandKind.StartRecording, "/r/a.mp4"));
        frames.Render();

        session.Apply(new EditorLinkCommand(EditorCommandKind.StopRecording));
        recorder.StopAndWait(TimeSpan.FromSeconds(5)); // raises Stopped on this thread

        Assert.Equal([EditorLinkRecordingState.Started, EditorLinkRecordingState.Stopped], sent.Select(s => s.State));
        Assert.Equal("", sent[1].Error);
    }

    [Fact]
    public void StartRecordingWhileRecordingReportsTheRunningRecording()
    {
        var (session, recorder, _, sent) = Create();
        using var _ = session;
        session.Apply(new EditorLinkCommand(EditorCommandKind.StartRecording, "/r/a.mp4"));

        // e.g. --record started it and the editor's F10 crossed the Started on the wire: the editor learns what is running.
        session.Apply(new EditorLinkCommand(EditorCommandKind.StartRecording, "/r/b.mp4"));

        Assert.Equal([EditorLinkRecordingState.Started, EditorLinkRecordingState.Started], sent.Select(s => s.State));
        Assert.EndsWith("a.mp4", sent[^1].Path);
        Assert.Equal(recorder.OutputPath, sent[^1].Path);
        Assert.True(recorder.IsRecording); // the first recording is unaffected
    }

    [Fact]
    public void StartRecordingWhileFinalisingIsReportedAsAFailure()
    {
        var (session, recorder, frames, sent) = Create();
        using var _ = session;
        session.Apply(new EditorLinkCommand(EditorCommandKind.StartRecording, "/r/a.mp4"));
        frames.Render();
        session.Apply(new EditorLinkCommand(EditorCommandKind.StopRecording)); // finalising until the next poll

        session.Apply(new EditorLinkCommand(EditorCommandKind.StartRecording, "/r/b.mp4"));

        Assert.Equal(EditorLinkRecordingState.Failed, sent[^1].State);
        Assert.Contains("finalised", sent[^1].Error);
        Assert.Equal("/r/b.mp4", sent[^1].Path);
        recorder.StopAndWait(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ReportRecordingFailedSendsAFailedReport()
    {
        var (session, _, _, sent) = Create();
        using var _ = session;

        session.ReportRecordingFailed("/r/a.mp4", "ffmpeg was not found.");

        Assert.Equal(new EditorLinkRecording(EditorLinkRecordingState.Failed, "/r/a.mp4", 0f, 0, 0, "ffmpeg was not found."), Assert.Single(sent));
    }

    [Fact]
    public void StartRecordingWithoutARecorderReportsFailure()
    {
        var tree = new SceneTree();
        using var session = new GameSession(tree, Settings());
        var sent = new List<EditorLinkRecording>();
        session.RecordingSink = sent.Add;
        session.Start();

        session.Apply(new EditorLinkCommand(EditorCommandKind.StartRecording, "/r/a.mp4"));

        Assert.Equal(EditorLinkRecordingState.Failed, Assert.Single(sent).State);
    }

    [Fact]
    public void StopCommandWhileRecordingReportsStoppedBeforeQuit()
    {
        var (session, recorder, frames, sent) = Create();
        using var _ = session;
        var quitAfterStopped = false;
        session.QuitRequested += _ => quitAfterStopped = sent.Any(s => s.State == EditorLinkRecordingState.Stopped);
        session.Apply(new EditorLinkCommand(EditorCommandKind.StartRecording, "/r/a.mp4"));
        frames.Render();

        session.Apply(new EditorLinkCommand(EditorCommandKind.Stop));

        // GameHost.OnClose finalises (StopAndWait) before Session.Shutdown; model that here.
        recorder.StopAndWait(TimeSpan.FromSeconds(5));
        Assert.Contains(sent, s => s.State == EditorLinkRecordingState.Stopped);
        Assert.False(quitAfterStopped); // Quit is requested first; the Stopped report follows in OnClose, before Goodbye
    }

    [Fact]
    public void ToLinkMapsAnErrorToFailed()
    {
        var ok = GameSession.ToLink(new RecordingResult { Path = "/r/a.mp4", DurationSeconds = 2, DroppedFrames = 1, DuplicatedFrames = 3 });
        var bad = GameSession.ToLink(new RecordingResult { Path = "/r/a.mp4", Error = "boom" });
        Assert.Equal(new EditorLinkRecording(EditorLinkRecordingState.Stopped, "/r/a.mp4", 2f, 1, 3, ""), ok);
        Assert.Equal(EditorLinkRecordingState.Failed, bad.State);
        Assert.Equal("boom", bad.Error);
    }

    [Fact]
    public void OutputPathIsSetWhileARecordingOwnsItsFilesOnly()
    {
        var (recorder, _, frames, _, _) = RecorderFakes.Create();
        Assert.Null(recorder.OutputPath);

        recorder.Start(new RecordingOptions { Path = "/r/a.mp4" });
        frames.Render();
        Assert.EndsWith("a.mp4", recorder.OutputPath);

        recorder.StopAndWait(TimeSpan.FromSeconds(5));
        Assert.Null(recorder.OutputPath);
    }
}
