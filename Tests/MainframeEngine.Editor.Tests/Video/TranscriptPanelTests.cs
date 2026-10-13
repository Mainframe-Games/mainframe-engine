using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Models;
using MainframeEngine.Video.Projects;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor.Tests.Video;

/// <summary>The transcript column on the headless editor: the word gestures, the markup following the document, and its cost.</summary>
[Collection(nameof(SerialEditor))]
public sealed class TranscriptPanelTests : IDisposable
{
    private static readonly FfmpegLocation NoFfmpeg = new("/x/ffmpeg", "/x/ffprobe", "/x", FfmpegLocationSource.Path);
    private readonly HeadlessEditor _editor = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("transcript-panel-").FullName;

    private EditorWorkspace W => _editor.Workspace;

    private TranscriptPanel Panel => W.VideoPanel.Transcript;

    public void Dispose()
    {
        _editor.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    // A video tab with the four-word transcript: so (1.0) · um (1.4) · we (2.0) · go (2.3).
    private VideoTab Open(bool transcript = true)
    {
        var video = Path.Combine(_dir, "trim.mp4");
        File.WriteAllBytes(video, [0]);
        var path = Path.Combine(_dir, "trim.mfcut");
        ProjectSerializer.SaveAsync(VideoDocumentTests.Project(video), path, CancellationToken.None).GetAwaiter().GetResult();
        var tab = W.Session.OpenVideo(path, NoFfmpeg, 100);
        if (transcript)
            tab.Document.SetTranscript(VideoDocumentTests.Words());
        _editor.Tick(2);
        return tab;
    }

    private RmlElement Word(int index) => W.VideoPanel.Document.GetElementById("w-" + index);

    private bool IsCut(int index) => Word(index).IsClassSet("cut");

    private static bool Disabled(VideoTab tab, double time) => tab.Document.Segments.Segments.Any(s => !s.Enabled && s.Start <= time && time < s.End);

    [Fact]
    public void AHiddenTranscriptLetsGoOfAClosedTab()
    {
        var tab = Open();
        Assert.Same(tab, Panel.Tab);
        W.Layout.SetVideoShowTranscript(false);
        _editor.Tick(2);
        W.Session.Close(tab);
        _editor.Tick(2);
        Assert.Null(Panel.Tab); // the closed tab (its document, preview and transcript) is not kept alive

        // Shown again, it follows the current tab and rebuilds.
        var next = Open();
        W.Layout.SetVideoShowTranscript(true);
        _editor.Tick(2);
        Assert.Same(next, Panel.Tab);
        Assert.False(Word(1).IsNull);
    }

    [Fact]
    public void ClickingAWordCutsIt()
    {
        var tab = Open();
        Panel.Press(1, EditorModifiers.None);
        Panel.Release();
        Assert.True(Disabled(tab, 1.6));
        Assert.False(Disabled(tab, 1.0));
        Assert.False(Disabled(tab, 2.1));
        Assert.True(tab.Document.Editor.Undo()); // one step
        Assert.Single(tab.Document.Segments.Segments);
        Assert.False(tab.Document.Editor.Undo());
    }

    [Fact]
    public void ClickingAStruckWordRestoresIt()
    {
        var tab = Open();
        Panel.Press(1, EditorModifiers.None);
        Panel.Release();
        Panel.Press(1, EditorModifiers.None);
        Panel.Release();
        Assert.False(Disabled(tab, 1.6));
        Assert.All(tab.Document.Segments.Segments, s => Assert.True(s.Enabled));
    }

    [Fact]
    public void DraggingAcrossWordsCutsTheRun()
    {
        var tab = Open();
        Panel.Press(0, EditorModifiers.None);
        Panel.Drag(1);
        Panel.Drag(2);
        Panel.Release();
        var cut = tab.Document.Segments.Segments.Single(s => !s.Enabled);
        Assert.Equal(1.0, cut.Start, 3);
        Assert.Equal(2.2, cut.End, 3);
        Assert.True(tab.Document.Editor.Undo());
        Assert.Single(tab.Document.Segments.Segments);
    }

    [Fact]
    public void ADragStartedOnAStruckWordRestoresTheRun()
    {
        var tab = Open();
        Panel.Press(0, EditorModifiers.None);
        Panel.Drag(3);
        Panel.Release();
        Assert.Contains(tab.Document.Segments.Segments, s => !s.Enabled);
        Panel.Press(2, EditorModifiers.None); // struck: the run restores
        Panel.Drag(1);
        Panel.Release();
        Assert.True(Disabled(tab, 1.1)); // "so" is outside the run
        Assert.False(Disabled(tab, 1.6));
        Assert.False(Disabled(tab, 2.1));
    }

    [Fact]
    public void TheRunIsMarkedWhileDraggingAndClearedOnRelease()
    {
        Open();
        Panel.Press(0, EditorModifiers.None);
        Panel.Drag(2);
        Assert.True(Word(0).IsClassSet("sel") && Word(1).IsClassSet("sel") && Word(2).IsClassSet("sel"));
        Assert.False(Word(3).IsClassSet("sel"));
        Panel.Drag(1);
        Assert.False(Word(2).IsClassSet("sel"));
        Assert.True(Word(1).IsClassSet("sel"));
        Panel.Release();
        Assert.All(Enumerable.Range(0, 4), i => Assert.False(Word(i).IsClassSet("sel")));
        Assert.False(Panel.IsPressed);
    }

    [Fact]
    public void CommandClickPlaysFromTheWord()
    {
        var tab = Open();
        Panel.Press(2, EditorModifiers.Command);
        Panel.Release();
        Assert.Equal(2.0, tab.State.Playhead, 3);
        Assert.Single(tab.Document.Segments.Segments);
        Assert.False(tab.Document.Editor.IsDirty);
    }

    [Fact]
    public void ReleasingWithoutAPressDoesNothing()
    {
        var tab = Open();
        Panel.Release();
        Panel.Drag(1);
        Assert.Single(tab.Document.Segments.Segments);
    }

    [Fact]
    public void TheMarkupFollowsACutAndRebuildsAfterUndo()
    {
        var tab = Open();
        Assert.False(IsCut(1));
        Panel.Press(1, EditorModifiers.None);
        Panel.Release();
        _editor.Tick();
        Assert.True(IsCut(1));
        Assert.False(IsCut(0));
        Assert.True(W.Commands.Execute("edit.undo"));
        _editor.Tick();
        Assert.False(IsCut(1));
        Assert.Single(tab.Document.Segments.Segments);
    }

    [Fact]
    public void ANewTranscriptShowsThePlaceholderUntilTranscribed()
    {
        var tab = Open(transcript: false);
        var words = W.VideoPanel.Document.GetElementById("video-transcript-words");
        Assert.Contains("No transcript yet. Transcribe to edit by text.", words.InnerRml, StringComparison.Ordinal);
        Assert.True(Word(0).IsNull);

        tab.Document.SetTranscript(VideoDocumentTests.Words());
        _editor.Tick();
        Assert.DoesNotContain("No transcript yet", words.InnerRml, StringComparison.Ordinal);
        Assert.False(Word(3).IsNull);
        Assert.Equal("um", Word(1).InnerRml);

        tab.Document.SetTranscript([]);
        _editor.Tick();
        Assert.Contains("No transcript yet", words.InnerRml, StringComparison.Ordinal);
        Assert.True(Word(0).IsNull);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void AVersionChangeWithTheSameTranscriptKeepsTheSpans()
    {
        var tab = Open();
        var rebuilds = Panel.Rebuilds;
        var first = Word(0);
        var second = Word(2);
        tab.Document.Editor.CreateSection(1.9, 2.5, anchor: 1.9);
        _editor.Tick();
        Assert.Equal(rebuilds, Panel.Rebuilds);
        Assert.Equal(first, Word(0));
        Assert.Equal(second, Word(2));
        Assert.True(IsCut(2));
        Assert.True(IsCut(3));
        Assert.False(IsCut(1));
        Assert.True(tab.Document.Editor.Undo());
        _editor.Tick();
        Assert.Equal(rebuilds, Panel.Rebuilds);
        Assert.Equal(second, Word(2));
        Assert.False(IsCut(2));
    }

    [Fact]
    public void ANewTranscriptListRebuildsTheSpans()
    {
        var tab = Open();
        var rebuilds = Panel.Rebuilds;
        tab.Document.SetTranscript(VideoDocumentTests.Words());
        _editor.Tick();
        Assert.Equal(rebuilds + 1, Panel.Rebuilds);
    }

    [Fact]
    public void NothingIsTouchedDuringABoundaryDragAndItCatchesUpAfter()
    {
        var tab = Open();
        var editor = tab.Document.Editor;
        editor.CreateSection(10, 20, anchor: 10);
        _editor.Tick();
        var rebuilds = Panel.Rebuilds;

        editor.BeginBoundaryDrag();
        editor.UpdateBoundaryDrag(1, 1.5); // the cut now covers "we" and "go"
        _editor.Tick(3);
        Assert.False(IsCut(2));
        Assert.False(IsCut(3));

        editor.EndBoundaryDrag(1, 1.5);
        _editor.Tick();
        Assert.True(IsCut(2));
        Assert.True(IsCut(3));
        Assert.Equal(rebuilds, Panel.Rebuilds);
    }

    [Fact]
    public void TheCurrentWordMovesBetweenTwoSpans()
    {
        var tab = Open();
        tab.Seek(2.0);
        _editor.Tick();
        Assert.Equal(2, tab.CurrentWord);
        Assert.True(Word(2).IsClassSet("cur"));

        tab.Seek(1.5);
        _editor.Tick();
        Assert.True(Word(1).IsClassSet("cur"));
        Assert.False(Word(2).IsClassSet("cur"));

        tab.Seek(30); // past the last word
        _editor.Tick();
        Assert.All(Enumerable.Range(0, 4), i => Assert.False(Word(i).IsClassSet("cur")));
    }

    [Fact]
    public void RightClickingAWordOffersPlayAndCutOrRestore()
    {
        var tab = Open();
        Panel.ShowMenu(1, 100, 100);
        Assert.True(W.Popup.Visible);
        Assert.Equal(["Play from here", "Cut"], W.Popup.Items.Select(i => i.Label));
        Assert.True(W.Popup.Choose("transcript.toggle"));
        Assert.True(Disabled(tab, 1.6));

        _editor.Tick();
        Panel.ShowMenu(1, 100, 100);
        Assert.Equal(["Play from here", "Restore"], W.Popup.Items.Select(i => i.Label));
        Assert.True(W.Popup.Choose("transcript.toggle"));
        Assert.False(Disabled(tab, 1.6));
    }

    [Fact]
    public void PlayFromHereSeeksToTheWord()
    {
        var tab = Open();
        Panel.ShowMenu(2, 100, 100);
        Assert.True(W.Popup.Choose("transcript.play")); // its decoder fails without ffmpeg, which only logs
        Assert.Equal(2.0, tab.State.Playhead, 3);
    }
}
