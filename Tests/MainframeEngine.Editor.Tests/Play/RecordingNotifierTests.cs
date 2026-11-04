using MainframeEngine.EditorLink;

namespace MainframeEngine.Editor.Tests.Play;

public sealed class RecordingNotifierTests
{
    private sealed class Harness
    {
        public readonly List<MessageRequest> Shown = [];
        public readonly List<string> Revealed = [];
        public readonly List<string> VideoEditorOpened = [];
        public string? ExistingDirectory;

        public RecordingNotifier Create() => new(Shown.Add, VideoEditorOpened.Add, Revealed.Add, d => d == ExistingDirectory);
    }

    private static EditorLinkRecording Saved(string path = "/u/recordings/a.mp4") =>
        new(EditorLinkRecordingState.Stopped, path, 83.5f, 0, 2, "");

    [Fact]
    public void ASavedRecordingOffersTheVideoEditorAndTheFolder()
    {
        var h = new Harness();
        h.Create().OnFinished(Saved());

        var dialog = Assert.Single(h.Shown);
        Assert.Equal("Recording saved", dialog.Title);
        Assert.Equal(["Open in Video Editor", "Show in Folder", "Close"], dialog.Buttons);
        Assert.Equal(0, dialog.DefaultButton);
        Assert.Equal(2, dialog.CancelButton);
        Assert.Contains("a.mp4", dialog.Message, StringComparison.Ordinal);
        Assert.Contains("1:23", dialog.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenInVideoEditorOpensTheRecordingInTheEditor()
    {
        var h = new Harness();
        h.Create().OnFinished(Saved());

        h.Shown[0].Callback!(0, "");

        Assert.Equal(["/u/recordings/a.mp4"], h.VideoEditorOpened);
        Assert.Empty(h.Revealed);
        Assert.Single(h.Shown);
    }

    [Fact]
    public void ShowInFolderRevealsTheFile()
    {
        var h = new Harness();
        h.Create().OnFinished(Saved());

        h.Shown[0].Callback!(1, "");

        Assert.Equal(["/u/recordings/a.mp4"], h.Revealed);
        Assert.Empty(h.VideoEditorOpened);
    }

    [Fact]
    public void CloseDoesNothing()
    {
        var h = new Harness();
        h.Create().OnFinished(Saved());

        h.Shown[0].Callback!(2, "");

        Assert.Empty(h.Revealed);
        Assert.Empty(h.VideoEditorOpened);
        Assert.Single(h.Shown);
    }

    [Fact]
    public void AFailedRecordingShowsTheError()
    {
        var h = new Harness();
        h.Create().OnFinished(new EditorLinkRecording(EditorLinkRecordingState.Failed, "/u/a.mp4", 0, 0, 0,
            "The encoder failed. The partial recording is in /u/a.mp4.partial."));

        var dialog = Assert.Single(h.Shown);
        Assert.Equal("Recording failed", dialog.Title);
        Assert.Contains("encoder failed", dialog.Message, StringComparison.Ordinal);
        Assert.Equal(["Show in Folder", "Close"], dialog.Buttons);

        dialog.Callback!(0, "");
        Assert.Equal(["/u/a.mp4.partial"], h.Revealed);
    }

    [Fact]
    public void AFailureWithoutAPartialRecordingOnlyOffersClose()
    {
        var h = new Harness();
        h.Create().OnFinished(new EditorLinkRecording(EditorLinkRecordingState.Failed, "/u/a.mp4", 0, 0, 0, "ffmpeg was not found."));

        var dialog = Assert.Single(h.Shown);
        Assert.Equal(["Close"], dialog.Buttons);
        dialog.Callback!(0, "");
        Assert.Empty(h.Revealed);
    }

    [Fact]
    public void AnAbandonedRecordingWithAPartialFolderOffersShowInFolder()
    {
        var h = new Harness { ExistingDirectory = "/u/a.mp4.partial" };
        h.Create().OnFinished(new EditorLinkRecording(EditorLinkRecordingState.Failed, "/u/a.mp4", 0, 0, 0,
            "The game exited before the recording was saved."));

        var dialog = Assert.Single(h.Shown);
        Assert.Equal(["Show in Folder", "Close"], dialog.Buttons);
        dialog.Callback!(0, "");
        Assert.Equal(["/u/a.mp4.partial"], h.Revealed);
    }

    [Fact]
    public void AFailureWithoutAMessageStillSaysSomething()
    {
        var h = new Harness();
        h.Create().OnFinished(new EditorLinkRecording(EditorLinkRecordingState.Failed, "/u/a.mp4", 0, 0, 0, ""));

        Assert.NotEmpty(Assert.Single(h.Shown).Message);
    }

    [Fact]
    public void AStartedRecordingShowsNothing()
    {
        var h = new Harness();
        h.Create().OnFinished(new EditorLinkRecording(EditorLinkRecordingState.Started, "/u/a.mp4", 0, 0, 0, ""));

        Assert.Empty(h.Shown);
    }

    [Fact]
    public void TwoFinishedRecordingsQueueTwoDialogs()
    {
        var h = new Harness();
        var notifier = h.Create();
        notifier.OnFinished(Saved("/u/a.mp4"));
        notifier.OnFinished(Saved("/u/b.mp4"));
        Assert.Equal(2, h.Shown.Count); // MessageDialog itself queues requests that arrive while one is open
    }
}
