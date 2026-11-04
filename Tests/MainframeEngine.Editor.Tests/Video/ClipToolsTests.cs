using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Analysis;
using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Models;
using MainframeEngine.Video.Timelines;
using MainframeEngine.Video.Transcript;
using Clip = MainframeEngine.Video.Timelines.Clip;

namespace MainframeEngine.Editor.Tests.Video;

/// <summary>
/// Cutback's tools on the selected clip (G20d), with the silence detector and the transcriber faked: each runs over the
/// clip's window, is one undo step on the clip and its linked audio, and ripples the clips after it; the transcript is
/// made once per media file and shared by its clips; the Transcript tab cuts words.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class ClipToolsTests : VideoEditorTestBase
{
    private readonly List<(double Start, double End)> _silenceRanges = [];
    private int _transcriptions;

    // "so um this is it uh yes": an "um" at 1.0–1.3 s and an "uh" at 6.0–6.2 s.
    private static readonly Word[] Words =
    [
        new("so", 0.4, 0.8, 0.9), new("um", 1.0, 1.3, 0.6), new("this", 1.6, 1.9, 0.9), new("is", 2.0, 2.2, 0.9),
        new("it", 2.3, 2.6, 0.9), new("uh", 6.0, 6.2, 0.6), new("yes", 7.0, 7.5, 0.9),
    ];

    public ClipToolsTests()
    {
        File.WriteAllBytes(Path.Combine(_models, WhisperModelInfo.Default.FileName), [1, 2, 3]); // "downloaded": nothing is fetched
        Tools.SilenceDetector = (_, _, _, _, start, end, _, _) =>
        {
            lock (_silenceRanges)
                _silenceRanges.Add((start, end));
            return Task.FromResult<IReadOnlyList<SilenceSpan>>([new SilenceSpan(2, 3), new SilenceSpan(6, 7)]);
        };
        Tools.TranscriberFactory = (_, _, _) => new FakeTranscriber(() =>
        {
            Interlocked.Increment(ref _transcriptions);
            return Words;
        });
    }

    private ClipTools Tools => W.VideoView.Tools;

    private sealed class FakeTranscriber(Func<IReadOnlyList<Word>> words) : ITranscriber
    {
        public Task<IReadOnlyList<Word>> TranscribeAsync(string mediaPath, IProgress<double>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(words());
    }

    // A timeline with the 10 s recording split at 5 s: A [0, 5) and B [5, 10) on V1, their audio on A1.
    private TimelineTab SplitTimeline()
    {
        var tab = NewTimeline(media: MediaFile("run.mp4"));
        tab.Seek(5);
        W.Commands.Execute("video.add_edit_all");
        Assert.Equal(2, tab.Timeline.ClipsOn(V1(tab)).Count);
        return tab;
    }

    private static Clip ClipAt(TimelineTab tab, string track, int index) => tab.Timeline.ClipsOn(track)[index];

    private void Select(TimelineTab tab, Clip clip)
    {
        tab.View.Selection.Clear();
        tab.View.Selection.Add(clip.Id);
        _editor.Tick();
    }

    private void WaitForJobs(TimelineTab tab) => TickUntil(() => ClipTools.IsIdle(tab), "the clip job");

    // ── Detect Silence ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SilenceRunsOnTheSelectedClipsWindowAsOneStep()
    {
        var tab = SplitTimeline();
        var a = ClipAt(tab, V1(tab), 0);
        Select(tab, a);
        var steps = tab.Document.History.Actions.Count;

        W.Commands.Execute("video.detect_silence");
        Assert.NotNull(tab.SilenceJob);
        WaitForJobs(tab);

        Assert.Equal([(0.0, 5.0)], _silenceRanges); // ffmpeg reads only A's window
        var cut = ClipAt(tab, V1(tab), 0);
        Assert.Contains(cut.Segments, s => !s.Enabled && s.Start >= 2 && s.End <= 3); // the 2–3 s silence
        Assert.DoesNotContain(cut.Segments, s => !s.Enabled && s.Start >= 6); // 6–7 s is outside A's window
        Assert.Equal(cut.Segments, ClipAt(tab, A1(tab), 0).Segments); // the linked audio cut too
        Assert.InRange(ClipAt(tab, V1(tab), 1).Start, 4, 4.99); // B rippled earlier
        Assert.StartsWith("Detected 1 silence, 0:00.", tab.Notice, StringComparison.Ordinal);
        Assert.Equal(steps + 1, tab.Document.History.Actions.Count);

        W.Commands.Execute("edit.undo");
        Assert.Equal(5, ClipAt(tab, V1(tab), 1).Start, 6);
    }

    [Fact]
    public void ToolsNeedASelectedClip()
    {
        var tab = SplitTimeline();
        tab.View.Selection.Clear();
        _editor.Tick();

        W.Commands.Execute("video.detect_silence");

        Assert.Null(tab.SilenceJob);
        Assert.Equal("Select a clip first: Detect Silence runs on the selected clip.", tab.Notice);
        Assert.True(W.Toolbar.Document.GetElementById("video-silence").IsClassSet("disabled"));
    }

    [Fact]
    public void ACutClipThatWasRemovedWhileDetectingIsLeftAlone()
    {
        var release = new TaskCompletionSource<IReadOnlyList<SilenceSpan>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Tools.SilenceDetector = (_, _, _, _, _, _, _, _) => release.Task;
        var tab = SplitTimeline();
        var a = ClipAt(tab, V1(tab), 0);
        Select(tab, a);
        W.Commands.Execute("video.detect_silence");
        W.Commands.Execute("video.ripple_delete"); // A goes while ffmpeg runs
        release.SetResult([new SilenceSpan(2, 3)]);
        WaitForJobs(tab);

        Assert.Equal("The clip was removed while its silence was detected; nothing was cut.", tab.Notice);
    }

    [Fact]
    public void PressingAgainCancels()
    {
        Tools.SilenceDetector = static async (_, _, _, _, _, _, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        };
        var tab = SplitTimeline();
        Select(tab, ClipAt(tab, V1(tab), 0));
        W.Commands.Execute("video.detect_silence");
        _editor.Tick();
        Assert.True(W.Toolbar.Document.GetElementById("video-silence").IsClassSet("busy"));

        W.Commands.Execute("video.detect_silence");
        WaitForJobs(tab);

        Assert.Equal("Silence detection cancelled.", tab.Notice);
        _editor.Tick();
        Assert.False(W.Toolbar.Document.GetElementById("video-silence").IsClassSet("busy"));
    }

    // ── Transcripts ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SharedBySourceHash()
    {
        var tab = SplitTimeline();
        Select(tab, ClipAt(tab, V1(tab), 0));

        W.Commands.Execute("video.transcribe");
        WaitForJobs(tab);

        Assert.Equal(1, _transcriptions);
        Assert.Equal("Transcribed 7 words.", tab.Notice);
        Assert.True(tab.ShowsTranscript);
        var sidecar = _project.Abs("Assets/Video/Media/run.mp4.mftranscript.json");
        Assert.True(File.Exists(sidecar));
        _editor.Tick(2);
        Assert.Equal(["so", "um", "this", "is", "it"], W.VideoPanel.Transcript.Words.Select(w => w.Text)); // A's window

        // B (the same recording) shows its own words without transcribing again, also after the cache is dropped.
        Tools.Transcripts.Clear();
        Select(tab, ClipAt(tab, V1(tab), 1));
        _editor.Tick(2);
        Assert.Equal(["uh", "yes"], W.VideoPanel.Transcript.Words.Select(w => w.Text));
        Assert.Equal(1, _transcriptions);

        // Transcribe on a media file that has one asks first.
        W.Commands.Execute("video.transcribe");
        Assert.Equal("Transcribe", W.Message.Current?.Title);
        W.Message.Answer(2);
        Assert.Equal(1, _transcriptions);
    }

    [Fact]
    public void RemoveFillersTranscribesFirstThenCutsInTheWindow()
    {
        var tab = SplitTimeline();
        Select(tab, ClipAt(tab, V1(tab), 0));

        W.Commands.Execute("video.remove_fillers");
        WaitForJobs(tab);
        _editor.Tick();

        var segments = ClipAt(tab, V1(tab), 0).Segments;
        Assert.Contains(segments, s => !s.Enabled && s.Origin == SegmentOrigin.Filler && s.Start <= 1.0 && s.End >= 1.3); // "um"
        Assert.DoesNotContain(segments, s => !s.Enabled && s.Origin == SegmentOrigin.Filler && s.Start >= 5); // "uh" is in B
        Assert.StartsWith("Removed 1 filler word", tab.Notice, StringComparison.Ordinal);
        Assert.Equal(segments, ClipAt(tab, A1(tab), 0).Segments);
    }

    [Fact]
    public void WordCutAppliesToLinkGroup()
    {
        var tab = SplitTimeline();
        Select(tab, ClipAt(tab, V1(tab), 0));
        W.Commands.Execute("video.transcribe");
        WaitForJobs(tab);
        _editor.Tick(2);
        var panel = W.VideoPanel.Transcript;
        var rebuilds = panel.Rebuilds;

        panel.Press(2, EditorModifiers.None); // "this" … "is"
        panel.Drag(3);
        panel.Release();
        _editor.Tick();

        var video = ClipAt(tab, V1(tab), 0);
        Assert.Contains(video.Segments, s => !s.Enabled && s.Origin == SegmentOrigin.Manual && s.Start <= 1.6 && s.End >= 2.2);
        Assert.Equal(video.Segments, ClipAt(tab, A1(tab), 0).Segments);
        Assert.Equal([false, false, true, true, false], panel.CutStates);
        Assert.Equal(rebuilds, panel.Rebuilds); // an edit flips classes, it does not rebuild
        Assert.True(ClipAt(tab, V1(tab), 1).Start < 5); // B rippled

        panel.Press(2, EditorModifiers.None); // from a struck word: the run is restored
        panel.Drag(3);
        panel.Release();
        _editor.Tick();
        Assert.DoesNotContain(ClipAt(tab, V1(tab), 0).Segments, s => !s.Enabled);
    }

    [Fact]
    public void SteadyFramesWithTheTranscriptTabAllocateNothing()
    {
        var tab = SplitTimeline();
        Select(tab, ClipAt(tab, V1(tab), 0));
        W.Commands.Execute("video.transcribe");
        WaitForJobs(tab);
        tab.Seek(2.1); // "is" is the current word
        _editor.Tick(60);
        Assert.Equal(3, W.VideoPanel.Transcript.Current);

        var before = GC.GetAllocatedBytesForCurrentThread();
        _editor.Tick(100);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"{allocated} B allocated over 100 steady frames with the Transcript tab");
    }

    // ── Detect Idle Gameplay ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IdleNeedsSidecar()
    {
        var tab = NewTimeline(media: MediaFile("run.mp4"));
        Select(tab, ClipAt(tab, V1(tab), 0));
        var idle = W.Toolbar.Document.GetElementById("video-idle");
        Assert.True(idle.IsClassSet("disabled"));
        Assert.Contains("needs the recording's Mainframe data", idle.GetAttribute("data-tooltip"), StringComparison.Ordinal);
        Assert.False(Assert.Single(W.Commands.MenuItems("video"), i => i.Command == "video.detect_idle").Enabled);

        File.WriteAllText(_project.Abs("Assets/Video/Media/run.mp4.mfcap.json"), """
            { "format": "mainframe-capture", "version": 1, "video": "run.mp4", "engineVersion": "0.0.0", "game": "t", "scene": "s",
              "mode": "live", "frameRate": 30, "width": 1920, "height": 1080, "durationSeconds": 10.0, "droppedFrames": 0,
              "input": [ { "start": 0.0, "end": 3.0 }, { "start": 8.5, "end": 10.0 } ],
              "loading": [ { "start": 5.0, "end": 7.0 } ], "paused": [], "keep": [], "skip": [], "markers": [] }
            """);
        Select(tab, ClipAt(tab, A1(tab), 0)); // the selection changes: the sidecar is looked for again
        Select(tab, ClipAt(tab, V1(tab), 0));
        Assert.False(idle.IsClassSet("disabled"));

        W.Commands.Execute("video.detect_idle");

        var segments = ClipAt(tab, V1(tab), 0).Segments;
        Assert.Contains(segments, s => !s.Enabled && s.Origin == SegmentOrigin.Gameplay && s.Start <= 5.0 && s.End >= 7.0); // loading
        Assert.StartsWith("Cut ", tab.Notice, StringComparison.Ordinal);
    }

    // ── The cut editor ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RestoreRipplesLater()
    {
        var tab = SplitTimeline();
        var a = ClipAt(tab, V1(tab), 0);
        tab.Document.Apply("Cut", t => TimelineEdits.SetClipCuts(t, a.Id, list => list.SetRange(2, 3, enabled: false)));
        Select(tab, ClipAt(tab, V1(tab), 0));
        tab.SyncCutEditor();
        Assert.Equal(4, ClipAt(tab, V1(tab), 1).Start, 6);

        var cut = tab.Cut.Segments.IndexAt(2.5);
        Assert.True(tab.Cut.Toggle(cut)); // the cut editor's click on the greyed region

        Assert.Equal(5, TimelineLayout.End(ClipAt(tab, V1(tab), 0), tab.Timeline.Rate), 6); // A lengthens
        Assert.Equal(5, ClipAt(tab, V1(tab), 1).Start, 6); // B moves later
    }
}
