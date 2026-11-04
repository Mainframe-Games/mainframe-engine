using System.Numerics;
using MainframeEngine.Editor.Tests.Projects;
using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Timelines;
using MainframeEngine.Video.Timelines.Export;
using Silk.NET.Input;
using Timeline = MainframeEngine.Video.Timelines.Timeline;

namespace MainframeEngine.Editor.Tests.Video;

/// <summary>
/// Video mode end to end in a game project, with ffmpeg faked at its seams (the prober, the decoders, the exporter): new
/// timelines, the recording dialog's timeline, the Source viewer's marks with insert and overwrite, Premiere's keys,
/// Finder drops on the timeline and the pool, save and reopen, and export through the dialog.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class VideoWorkflowTests : IDisposable
{
    private readonly TestProject _project = new("Clips");
    private readonly string _ffmpeg = Directory.CreateTempSubdirectory("mf-fake-ffmpeg").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("mf-outside-media").FullName;
    private readonly HeadlessEditor _editor;
    private readonly List<(Timeline Timeline, TimelineExportPreset Preset, double Start, double End, string Path)> _exports = [];

    public VideoWorkflowTests()
    {
        var locator = new FfmpegLocator();
        File.WriteAllBytes(Path.Combine(_ffmpeg, locator.ExecutableName("ffmpeg")), []);
        File.WriteAllBytes(Path.Combine(_ffmpeg, locator.ExecutableName("ffprobe")), []);
        _editor = new HeadlessEditor(configure: o => o with
        {
            InitialProject = _project.Root,
            GameBuilder = new FakeGameBuilder(),
            GameLauncher = new FakeGameLauncher(),
        });
        var settings = W.Settings.Clone();
        settings.VideoFfmpegPath = _ffmpeg;
        W.ApplySettings(settings);
        W.VideoView.DecoderFactory = static _ => new VideoGolden.Decoders();
        W.VideoView.Prober = static (_, full, stored, uid, _) => Task.FromResult(Probe(full, stored, uid));
        W.VideoView.ExporterFactory = (_, _) => (timeline, preset, start, end, path, _, _, _) =>
        {
            lock (_exports)
                _exports.Add((timeline, preset, start, end, path));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "mp4");
            return Task.CompletedTask;
        };
        _editor.Tick(3);
    }

    public void Dispose()
    {
        _editor.Dispose();
        _project.Dispose();
        Directory.Delete(_ffmpeg, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private EditorWorkspace W => _editor.Workspace;

    // What ffprobe would say: 10 s 1080p30 video with sound, or 30 s of audio.
    private static MediaRef Probe(string full, string stored, string uid)
    {
        var audio = MediaPool.KindOf(full, isDirectory: false) == MediaPoolKind.Audio;
        return new MediaRef(uid, stored.Replace('\\', '/'), "hash-" + Path.GetFileName(full), audio ? 30 : 10,
            audio ? 0 : 1920, audio ? 0 : 1080, audio ? 0 : 30, HasVideo: !audio, HasAudio: true);
    }

    private void TickUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            _editor.Tick();
            Thread.Sleep(5);
        }

        Assert.True(condition(), $"timed out waiting for {what}");
    }

    private string MediaFile(string name, string folder = "Assets/Video/Media")
    {
        var path = _project.Abs($"{folder}/{name}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "media");
        return path;
    }

    private MediaRef MediaOf(string path)
    {
        MediaRef? media = null;
        W.VideoView.WithMedia(path, m => media = m);
        TickUntil(() => media is not null, "the probe");
        return media!;
    }

    private TimelineTab NewTimeline(string name = "Edit", string? media = null)
    {
        W.SetMode(EditorMode.Video);
        _editor.Tick(2);
        var pool = Assert.IsType<MediaPool>(W.VideoPanel.Pool);
        W.VideoView.CreateTimeline(pool, name, new TimelineSettings(1920, 1080, new FrameRate(30, 1)), media is null ? null : MediaOf(media));
        _editor.Tick(3);
        return Assert.IsType<TimelineTab>(W.Session.ActiveTab);
    }

    private static string V1(TimelineTab tab) => tab.Timeline.TracksOf(TrackKind.Video)[0].Id;

    private static string V2(TimelineTab tab) => tab.Timeline.TracksOf(TrackKind.Video)[1].Id;

    private static string A1(TimelineTab tab) => tab.Timeline.TracksOf(TrackKind.Audio)[0].Id;

    // ── Timelines ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void VideoModeOpensOnTheProjectsMediaPool()
    {
        MediaFile("run.mp4");
        W.SetMode(EditorMode.Video);
        _editor.Tick(2);

        Assert.Equal(EditorMode.Video, W.Mode);
        var pool = Assert.IsType<MediaPool>(W.VideoPanel.Pool);
        Assert.Equal(_project.Root, pool.ProjectRoot);
        TickUntil(() => pool.Items.Any(i => i.Name == "Media"), "the pool to see the new folder"); // its file watcher
        Assert.Null(W.Session.ActiveTab);
    }

    [Fact]
    public void GameModeLeavesTheProjectAloneAndVideoModeMakesItsFolders()
    {
        _editor.Tick(5);
        Assert.False(Directory.Exists(_project.Abs("Assets/Video"))); // a game project opened in game mode is untouched

        W.SetMode(EditorMode.Video);
        _editor.Tick(2);
        foreach (var folder in MediaPool.StandardFolders)
            Assert.True(Directory.Exists(_project.Abs($"Assets/Video/{folder}")), folder);
        Assert.Equal(["Media", "Recordings", "Timelines"], W.VideoPanel.Pool!.Items.Select(i => i.Name));
    }

    [Fact]
    public void NewTimelineWritesTheFileAndOpensItsTab()
    {
        W.SetMode(EditorMode.Video);
        _editor.Tick(2);
        Assert.True(W.Commands.Execute("video.new_timeline"));
        Assert.Equal("New Timeline", W.Message.Current?.Title);
        W.Message.Answer(0); // Vertical 9:16, named "Timeline"
        _editor.Tick(3);

        var tab = Assert.IsType<TimelineTab>(W.Session.ActiveTab);
        Assert.Equal(_project.Abs("Assets/Video/Timelines/Timeline.mftimeline"), tab.FilePath);
        Assert.True(File.Exists(tab.FilePath));
        Assert.Equal((1080, 1920), (tab.Timeline.Settings.Width, tab.Timeline.Settings.Height));
        Assert.False(tab.IsDirty);
        Assert.Equal(EditorMode.Video, W.Mode);

        var second = NewTimeline("Timeline"); // a clash is numbered, never overwritten
        Assert.Equal(_project.Abs("Assets/Video/Timelines/Timeline-2.mftimeline"), second.FilePath);
        Assert.Equal(2, W.Session.Tabs.OfType<TimelineTab>().Count());
    }

    [Fact]
    public void OpenInVideoEditorCreatesTimeline()
    {
        var recording = MediaFile("game-2026-11-04.mp4", "Assets/Video/Recordings");
        Assert.Equal(EditorMode.Game, W.Mode);

        W.VideoView.OpenRecording(recording); // the recording dialog's Open in Video Editor
        TickUntil(() => W.Session.ActiveTab is TimelineTab, "the recording's timeline");

        var tab = (TimelineTab)W.Session.ActiveTab!;
        Assert.Equal(EditorMode.Video, W.Mode);
        Assert.Equal(_project.Abs("Assets/Video/Timelines/game-2026-11-04.mftimeline"), tab.FilePath);
        Assert.Equal((1920, 1080), (tab.Timeline.Settings.Width, tab.Timeline.Settings.Height));
        var video = Assert.Single(tab.Timeline.ClipsOn(V1(tab)));
        var audio = Assert.Single(tab.Timeline.ClipsOn(A1(tab)));
        Assert.NotNull(video.LinkGroup);
        Assert.Equal(video.LinkGroup, audio.LinkGroup);
        Assert.Equal("Assets/Video/Recordings/game-2026-11-04.mp4", video.Media.Path);
        Assert.Equal(10, TimelineLayout.Duration(tab.Timeline), 6);
    }

    [Fact]
    public void SaveAndReopenKeepTheEdit()
    {
        var tab = NewTimeline(media: MediaFile("run.mp4"));
        tab.Seek(4);
        _editor.Key(Key.K, Key.SuperLeft); // add edit
        Assert.True(tab.IsDirty);
        _editor.Key(Key.S, Key.SuperLeft);
        Assert.False(tab.IsDirty);
        var path = tab.FilePath!;

        W.Commands.Execute("file.close");
        _editor.Tick(2);
        Assert.DoesNotContain(tab, W.Session.Tabs);
        W.Commands.OpenTimeline(path);
        _editor.Tick(2);

        var reopened = Assert.IsType<TimelineTab>(W.Session.ActiveTab);
        Assert.Equal(2, reopened.Timeline.ClipsOn(V1(reopened)).Count);
    }

    // ── Keys ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddEditAtPlayhead()
    {
        var tab = NewTimeline(media: MediaFile("run.mp4"));
        var overlay = MediaOf(MediaFile("overlay.mp4"));
        tab.Document.Apply("Overwrite", t => TimelineEdits.Overwrite(t, overlay, 0, 10, 0, V2(tab), null)); // V2, no sound
        int Count(string track) => tab.Timeline.ClipsOn(track).Count;
        tab.Seek(4);

        _editor.Key(Key.K, Key.SuperLeft); // Cmd+K, nothing selected: every unlocked track
        Assert.Equal((2, 2, 2), (Count(V1(tab)), Count(A1(tab)), Count(V2(tab))));
        Assert.Equal(4, tab.Timeline.ClipsOn(V1(tab))[1].Start, 6);

        // With a clip selected, Cmd+K cuts its track (and its linked partner's); Shift+Cmd+K cuts every track.
        tab.View.Selection.Add(tab.Timeline.ClipsOn(V1(tab))[1].Id);
        tab.Seek(6);
        _editor.Key(Key.K, Key.SuperLeft);
        Assert.Equal((3, 3, 2), (Count(V1(tab)), Count(A1(tab)), Count(V2(tab))));
        tab.Seek(8);
        _editor.Key(Key.K, Key.SuperLeft, Key.ShiftLeft);
        Assert.Equal((4, 4, 3), (Count(V1(tab)), Count(A1(tab)), Count(V2(tab))));

        _editor.Key(Key.Z, Key.SuperLeft); // undo is one Add Edit at a time
        Assert.Equal((3, 3, 2), (Count(V1(tab)), Count(A1(tab)), Count(V2(tab))));
    }

    [Fact]
    public void RippleDeleteClosesTheGapAndLiftLeavesIt()
    {
        var tab = NewTimeline(media: MediaFile("run.mp4"));
        tab.Seek(4);
        _editor.Key(Key.K, Key.SuperLeft);
        var first = tab.Timeline.ClipsOn(V1(tab))[0];

        tab.View.Selection.Add(first.Id);
        _editor.Key(Key.Backspace); // lift: a gap
        Assert.Equal(4, Assert.Single(tab.Timeline.ClipsOn(V1(tab))).Start, 6);
        Assert.Single(tab.Timeline.ClipsOn(A1(tab))); // the linked audio went too

        _editor.Key(Key.Z, Key.SuperLeft);
        tab.View.Selection.Add(first.Id);
        _editor.Key(Key.Backspace, Key.ShiftLeft); // ripple delete: no gap
        Assert.Equal(0, Assert.Single(tab.Timeline.ClipsOn(V1(tab))).Start, 6);
        Assert.Equal(0, Assert.Single(tab.Timeline.ClipsOn(A1(tab))).Start, 6);
    }

    [Fact]
    public void ToolKeysAndUnlink()
    {
        var tab = NewTimeline(media: MediaFile("run.mp4"));
        _editor.Key(Key.C);
        Assert.Equal(TimelineTool.Razor, tab.View.Tool);
        _editor.Key(Key.V);
        Assert.Equal(TimelineTool.Select, tab.View.Tool);
        _editor.Key(Key.S);
        Assert.False(tab.View.Snapping);

        tab.View.Selection.Add(tab.Timeline.ClipsOn(V1(tab))[0].Id);
        _editor.Key(Key.L, Key.SuperLeft); // Cmd+L on a linked clip: unlink
        Assert.All(tab.Timeline.Clips, c => Assert.Null(c.LinkGroup));
    }

    [Fact]
    public void TheCutEditorFollowsTheSelectionOnTheFrameItChanges()
    {
        var tab = NewTimeline(media: MediaFile("run.mp4"));
        _editor.Tick(2);
        Assert.Contains("select a clip", W.VideoPanel.CutsTitle, StringComparison.Ordinal);

        tab.View.Selection.Add(tab.Timeline.ClipsOn(V1(tab))[0].Id);
        _editor.Tick();
        Assert.Equal(tab.View.Selection.First(), tab.Cut.ClipId);
        Assert.Contains("run.mp4", W.VideoPanel.CutsTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTrackHeaderMenuAddsAndRemovesTracks()
    {
        var tab = NewTimeline(media: MediaFile("run.mp4"));
        _editor.Tick(2);
        W.VideoPanel.ShowTrackMenu(V2(tab), 10, 10);
        Assert.True(W.Popup.Visible);
        Assert.Contains(W.Popup.Items, i => i.Label == "Remove Video Track V2" && i.Enabled);

        Assert.True(W.Popup.Choose("video.add_video_track"));
        Assert.Equal(3, tab.Timeline.TracksOf(TrackKind.Video).Count);

        W.VideoPanel.ShowTrackMenu(V1(tab), 10, 10);
        Assert.True(W.Popup.Choose("track.remove")); // V1 and its clip go; its linked audio stays on A1
        Assert.Equal(2, tab.Timeline.TracksOf(TrackKind.Video).Count);
        Assert.DoesNotContain(tab.Timeline.Clips, c => tab.Timeline.FindTrack(c.TrackId)?.Kind == TrackKind.Video);
        Assert.Single(tab.Timeline.ClipsOn(A1(tab)));

        _editor.Key(Key.Z, Key.SuperLeft);
        Assert.Single(tab.Timeline.ClipsOn(V1(tab)));

        // The last track of a kind stays.
        foreach (var track in tab.Timeline.TracksOf(TrackKind.Audio).Skip(1).ToList())
            W.VideoPanel.RemoveTrack(tab, track.Id);
        W.VideoPanel.ShowTrackMenu(A1(tab), 10, 10);
        Assert.False(Assert.Single(W.Popup.Items, i => i.Command == "track.remove").Enabled);
        W.Popup.Close();
    }

    // ── Source viewer ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SourceMarksWithOverwriteAndInsertPlaceTheRange()
    {
        var tab = NewTimeline();
        var file = MediaFile("run.mp4");
        W.VideoView.ShowInSource(file);
        TickUntil(() => tab.Source.Media is not null, "the Source viewer");
        Assert.True(tab.SourceFocused);

        tab.Source.Seek(2);
        _editor.Key(Key.I);
        tab.Source.Seek(5);
        _editor.Key(Key.O);
        Assert.Equal((2.0, 5.0), tab.Source.Range);

        _editor.Key(Key.Period); // overwrite at the playhead (0)
        var placed = Assert.Single(tab.Timeline.ClipsOn(V1(tab)));
        Assert.Equal(0, placed.Start, 6);
        Assert.Equal(2, placed.SourceIn, 6);
        Assert.Equal(5, placed.SourceOut, 6);
        Assert.Single(tab.Timeline.ClipsOn(A1(tab)));
        Assert.Equal(3, tab.View.Playhead, 6); // after what was placed

        tab.Seek(0);
        tab.SourceFocused = true;
        _editor.Key(Key.Comma); // insert at 0: the first clip moves along
        var clips = tab.Timeline.ClipsOn(V1(tab));
        Assert.Equal(2, clips.Count);
        Assert.Equal(3, clips[1].Start, 6);
    }

    // ── Finder drops ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OsDropOnTimelineInsertsAtTimeAndTrack()
    {
        var tab = NewTimeline();
        _editor.Tick(3); // the panel lays out the timeline canvas
        var outside = Path.Combine(_outside, "clip.mp4");
        File.WriteAllText(outside, "video");
        var rect = W.VideoPanel.TimelineRect;
        Assert.True(rect.Width > 0, "the timeline canvas has no size");
        var lane = tab.View.Lanes(tab.Timeline).First(l => l.TrackId == V2(tab));
        var local = new Vector2((float)tab.View.TimeToX(2), tab.View.LaneTop(lane) + lane.Height / 2);

        W.DropOsFiles(new FilesDroppedEvent([outside], new Vector2(rect.X, rect.Y) + local));
        TickUntil(() => tab.Timeline.ClipsOn(V2(tab)).Count == 1, "the dropped clip");

        var copy = _project.Abs("Assets/Video/Media/clip.mp4");
        Assert.True(File.Exists(copy)); // imported, not referenced from outside
        var clip = tab.Timeline.ClipsOn(V2(tab))[0];
        Assert.Equal(2, clip.Start, 6);
        Assert.Equal("Assets/Video/Media/clip.mp4", clip.Media.Path);
        Assert.Equal(2, Assert.Single(tab.Timeline.ClipsOn(A1(tab))).Start, 6); // its sound on the audio target
    }

    [Fact]
    public void OsDropOnThePoolImportsIntoItsFolder()
    {
        W.SetMode(EditorMode.Video);
        _editor.Tick(3);
        var outside = Path.Combine(_outside, "music.ogg");
        File.WriteAllText(outside, "audio");
        var pool = W.VideoPanel.PoolRect;
        Assert.True(pool.Width > 0, "the pool has no size");

        W.DropOsFiles(new FilesDroppedEvent([outside], new Vector2(pool.X + pool.Width / 2, pool.Y + pool.Height / 2)));
        TickUntil(() => W.VideoView.IsIdle && W.VideoPanel.Pool!.Items.Any(i => i.Name == "music.ogg"), "the import");

        Assert.True(File.Exists(_project.Abs("Assets/Video/music.ogg")));
        Assert.Null(W.Session.ActiveTab); // no timeline: nothing placed
    }

    // ── Export ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExportDialogPresetAndDefaultPath()
    {
        var tab = NewTimeline(media: MediaFile("run.mp4"));
        _editor.Key(Key.E, Key.SuperLeft);
        var dialog = W.ExportVideo;
        Assert.True(dialog.Visible);
        Assert.Same(tab, dialog.Tab);

        dialog.PresetIndex = TimelineExportPresets.All.ToList().IndexOf(TimelineExportPresets.Vertical30);
        Assert.Equal(Path.Combine(_project.Root, "Exports", "Edit-vertical-30.mp4"), dialog.OutputPath);
        dialog.PresetIndex = TimelineExportPresets.All.ToList().IndexOf(TimelineExportPresets.GifLoop);
        Assert.Equal(Path.Combine(_project.Root, "Exports", "Edit-gif.gif"), dialog.OutputPath); // the default follows

        dialog.PresetIndex = TimelineExportPresets.All.ToList().IndexOf(TimelineExportPresets.Vertical30);
        dialog.Export();
        TickUntil(() => W.Message.Current?.Title == "Export finished", "the export");

        var export = Assert.Single(_exports);
        Assert.Equal(TimelineExportPresets.Vertical30, export.Preset);
        Assert.Equal((0.0, 10.0), (export.Start, export.End));
        Assert.Same(tab.Timeline, export.Timeline); // the snapshot taken when Export was pressed
        Assert.True(File.Exists(export.Path));
        W.Message.Answer(1);

        // The same file again asks before replacing it.
        _editor.Key(Key.E, Key.SuperLeft);
        dialog.OutputPath = export.Path;
        dialog.Export();
        Assert.Equal("Replace file?", W.Message.Current?.Title);
        W.Message.Answer(1); // Cancel
        _editor.Tick(2);
        Assert.Single(_exports);
    }

    [Fact]
    public void AnEmptyTimelineHasNothingToExport()
    {
        var tab = NewTimeline();
        _editor.Key(Key.E, Key.SuperLeft);
        Assert.False(W.ExportVideo.Visible);
        Assert.Equal(TimelineExport.NothingToExport, tab.Notice);
    }

    // ── Inspector ────────────────────────────────────────────────────────────────────────────────────────────

    private void Commit(string field, string value)
    {
        var input = W.VideoPanel.Document.QuerySelector($"input[data-field={field}]");
        Assert.False(input.IsNull, $"no {field} field");
        Assert.True(input.Focus());
        input.SetValue(value);
        input.Blur(); // a field commits on blur (or Enter)
        _editor.Tick();
    }

    [Fact]
    public void TheInspectorReframesTheSelectedClipOneStepPerField()
    {
        var tab = NewTimeline(media: MediaFile("run.mp4"));
        var clip = tab.Timeline.ClipsOn(V1(tab))[0];
        tab.View.Selection.Add(clip.Id);
        _editor.Tick(3); // the Inspector shows the clip

        Commit("scale", "50");
        Commit("x", "120");
        Commit("opacity", "75");
        var transform = tab.Timeline.FindClip(clip.Id)!.Transform;
        Assert.Equal(0.5, transform.Scale, 6);
        Assert.Equal(120, transform.X, 6);
        Assert.Equal(0.75, transform.Opacity, 6);
        Assert.Equal(["Scale", "Position", "Opacity"], tab.Document.History.Actions.Select(a => a.Name).TakeLast(3));

        Commit("scale", "not a number"); // refused: the field shows the value again
        Assert.Equal(0.5, tab.Timeline.FindClip(clip.Id)!.Transform.Scale, 6);
        Assert.Equal(3, tab.Document.History.Actions.Count(a => a.Name is "Scale" or "Position" or "Opacity"));
    }

    [Fact]
    public void WithNothingSelectedTheInspectorEditsTheTimeline()
    {
        var tab = NewTimeline();
        _editor.Tick(3);

        Commit("width", "1081"); // even sizes only
        Commit("height", "1920");
        Assert.Equal((1080, 1920), (tab.Timeline.Settings.Width, tab.Timeline.Settings.Height));
        Assert.Equal((540, 960), (tab.Program.Width, tab.Program.Height)); // the viewer follows the new shape

        _editor.Key(Key.Z, Key.SuperLeft); // one step per field
        Assert.Equal((1080, 1080), (tab.Timeline.Settings.Width, tab.Timeline.Settings.Height));
        _editor.Key(Key.Z, Key.SuperLeft);
        Assert.Equal((1920, 1080), (tab.Timeline.Settings.Width, tab.Timeline.Settings.Height));
    }

    // ── The preview ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheProgramPlaysTheTimelineThroughOneGraphPerVisibleTrack()
    {
        var tab = NewTimeline(media: MediaFile("run.mp4"));
        var b = MediaFile("b.mp4");
        var media = MediaOf(b);
        tab.Document.Apply("Overwrite", t => TimelineEdits.Overwrite(t, media, 0, 3, 1, V2(tab), null));
        _editor.Tick();

        var program = tab.Program;
        Assert.Same(tab.Timeline, program.Timeline); // follows the edit
        Assert.Equal((960, 540), (program.Width, program.Height)); // the preview's long side is 960
        var both = program.VideoArguments(0, 4);
        Assert.Equal(2, both.Count(a => a == "-i")); // one input per clip window (the colour base is a filter source)
        Assert.Contains(both, a => a.Contains("s=960x540", StringComparison.Ordinal));

        tab.Document.Apply("Hide V2", t => TimelineEdits.SetTrack(t, V2(tab), track => track with { Enabled = false }));
        _editor.Tick();
        Assert.Equal(1, tab.Program.VideoArguments(0, 4).Count(a => a == "-i")); // a hidden track is not decoded
    }
}
