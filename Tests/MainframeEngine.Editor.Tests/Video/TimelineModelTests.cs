using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Timelines;
using static MainframeEngine.Editor.Tests.Video.TimelineTestKit;
using Clip = MainframeEngine.Video.Timelines.Clip;
using Timeline = MainframeEngine.Video.Timelines.Timeline;

namespace MainframeEngine.Editor.Tests.Video;

/// <summary>
/// <see cref="TimelineDocument"/>: every edit is one undo step in the editor's history, a drag merges into one, an edit
/// that changes nothing records nothing, and dirty follows the save marker.
/// </summary>
public sealed class TimelineDocumentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mf-timeline-doc").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void DragIsOneUndoStep()
    {
        var doc = Document(Append(Landscape(), 0, 4), _dir);
        var start = doc.Timeline;
        var clip = start.ClipsOn(V1(start))[0];

        // A drag re-applies its edit to the timeline as it was at the press, under one merge key.
        foreach (var x in new[] { 1.0, 2.0, 3.0 })
            Assert.True(doc.ApplyFrom(start, "Move Clip", t => TimelineEdits.Move(t, clip.Id, x), mergeKey: "timeline.move"));
        doc.History.EndMerge();

        Assert.Equal(3, doc.Timeline.FindClip(clip.Id)!.Start, 6);
        Assert.Single(doc.History.Actions);
        doc.History.Undo();
        Assert.Same(start, doc.Timeline);
    }

    [Fact]
    public void AnEditThatChangesNothingRecordsNothing()
    {
        var doc = Document(Landscape(), _dir);
        var version = doc.Version;

        Assert.False(doc.Apply("Razor", t => TimelineEdits.Razor(t, 1))); // nothing to cut

        Assert.Empty(doc.History.Actions);
        Assert.Equal(version, doc.Version);
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void DirtyAgainstSaveMarker()
    {
        var doc = Document(Landscape(), _dir);
        var flips = 0;
        doc.DirtyChanged += () => flips++;

        doc.Apply("Append", t => Append(t, 0, 2));
        Assert.True(doc.IsDirty);
        doc.Save();
        Assert.False(doc.IsDirty);
        doc.History.Undo();
        Assert.True(doc.IsDirty); // back before the save
        doc.History.Redo();
        Assert.False(doc.IsDirty); // at the save again
        Assert.Equal(4, flips);

        var reloaded = TimelineDocument.Load(doc.FilePath, _dir);
        Assert.Equal(doc.Timeline.Clips.Length, reloaded.Timeline.Clips.Length);
        Assert.Equal(doc.Timeline.Uid, reloaded.Timeline.Uid);
    }

    [Fact]
    public void EveryChangeBumpsTheVersion()
    {
        var doc = Document(Landscape(), _dir);
        var changes = 0;
        doc.Changed += () => changes++;

        doc.Apply("Append", t => Append(t, 0, 2));
        doc.History.Undo();
        doc.History.Redo();

        Assert.Equal(3, changes);
        Assert.Equal(3, doc.Version);
    }

    [Fact]
    public void MediaResolvesRelativeToTheProject()
    {
        var doc = Document(Landscape(), _dir);
        Assert.Equal(Path.Combine(_dir, "Assets", "Video", "Media", "a.mp4"), doc.Resolve(Recording("Assets/Video/Media/a.mp4") with { Uid = "" }));
    }
}

/// <summary>The media pool lists <c>Assets/Video</c> as media only: video, audio, images, timelines and folders.</summary>
public sealed class MediaPoolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mf-media-pool").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void ShowsMediaOnly()
    {
        Write("Assets/Video/Media/run.mp4");
        Write("Assets/Video/Media/run.mp4.mfcap.json"); // the recording's sidecar
        Write("Assets/Video/Media/run.mp4.mfmeta");
        Write("Assets/Video/Media/music.ogg");
        Write("Assets/Video/Media/hit.wav");
        Write("Assets/Video/Media/logo.png");
        Write("Assets/Video/Media/copying.mp4.tmp"); // an import in progress
        Write("Assets/Video/Media/Notes.cs");
        Write("Assets/Video/Media/readme.md");
        Write("Assets/Video/Timelines/Trailer.mftimeline");
        Write("Assets/Scenes/Main.mfscene"); // outside the pool's root

        var pool = new MediaPool(_root);
        Assert.True(pool.Refresh());
        Assert.Equal(Path.Combine(_root, "Assets", "Video"), pool.RootFolder);
        Assert.False(pool.CanGoUp);
        Assert.Contains(pool.Items, i => i.Name == "Media" && i.Kind == MediaPoolKind.Folder);
        Assert.Contains(pool.Items, i => i.Name == "Timelines" && i.Kind == MediaPoolKind.Folder);

        pool.Enter(Path.Combine(pool.RootFolder, "Media"));
        Assert.True(pool.CanGoUp);
        Assert.Equal(
            ["hit.wav:Audio", "logo.png:Image", "music.ogg:Audio", "run.mp4:Video"],
            pool.Items.Where(i => i.Kind != MediaPoolKind.Folder).Select(i => $"{i.Name}:{i.Kind}").Order(StringComparer.Ordinal));

        pool.Up();
        pool.Enter(Path.Combine(pool.RootFolder, "Timelines"));
        Assert.Equal(MediaPoolKind.Timeline, Assert.Single(pool.Items, i => i.Kind != MediaPoolKind.Folder).Kind);
    }

    [Fact]
    public void AnEmptyProjectHasAnEmptyPool()
    {
        var pool = new MediaPool(_root);
        pool.Refresh();
        Assert.DoesNotContain(pool.Items, i => i.Kind != MediaPoolKind.Folder);
    }
}

/// <summary>Importing copies outside files into the pool's folder, numbering clashes; files already in the project stay put.</summary>
public sealed class MediaImporterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mf-import-project").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("mf-import-outside").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private string Media => Path.Combine(_root, "Assets", "Video", "Media");

    [Fact]
    public async Task NumbersClashAndCopiesSidecar()
    {
        var source = Path.Combine(_outside, "clip.mp4");
        await File.WriteAllTextAsync(source, "video", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(source + MediaImporter.SidecarSuffix, "{}", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Media);
        await File.WriteAllTextAsync(Path.Combine(Media, "clip.mp4"), "older", TestContext.Current.CancellationToken);

        var imported = await MediaImporter.ImportAsync([source], Media, _root, null, TestContext.Current.CancellationToken);

        var target = Assert.Single(imported);
        Assert.Equal(Path.Combine(Media, "clip-2.mp4"), target);
        Assert.Equal("video", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal("{}", await File.ReadAllTextAsync(target + MediaImporter.SidecarSuffix, TestContext.Current.CancellationToken));
        Assert.Equal("older", await File.ReadAllTextAsync(Path.Combine(Media, "clip.mp4"), TestContext.Current.CancellationToken)); // never overwritten
        Assert.Empty(Directory.GetFiles(Media, "*.tmp"));
        Assert.True(File.Exists(source)); // copied, not moved
    }

    [Fact]
    public async Task InProjectFileIsNotCopied()
    {
        var inside = Path.Combine(_root, "Assets", "Video", "Recordings", "run.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        await File.WriteAllTextAsync(inside, "video", TestContext.Current.CancellationToken);

        var imported = await MediaImporter.ImportAsync([inside], Media, _root, null, TestContext.Current.CancellationToken);

        Assert.Equal([inside], imported);
        Assert.False(File.Exists(Path.Combine(Media, "run.mp4")));
    }

    [Fact]
    public async Task FilesThePoolWouldNotShowAreSkipped()
    {
        var script = Path.Combine(_outside, "Player.cs");
        var missing = Path.Combine(_outside, "gone.mp4");
        await File.WriteAllTextAsync(script, "class P {}", TestContext.Current.CancellationToken);

        var imported = await MediaImporter.ImportAsync([script, missing, _outside], Media, _root, null, TestContext.Current.CancellationToken);

        Assert.Empty(imported);
        Assert.False(File.Exists(Path.Combine(Media, "Player.cs")));
    }

    [Fact]
    public void DestinationKeepsTheExtension() =>
        Assert.Equal(Path.Combine("/m", "a-3.mp4"),
            MediaImporter.Destination("/m", "a.mp4", p => p == Path.Combine("/m", "a.mp4") || p == Path.Combine("/m", "a-2.mp4")));
}

/// <summary>
/// The timeline's mouse gestures over a <see cref="TimelineDocument"/> and a <see cref="TimelineViewState"/>: select
/// (with linked partners), move (to another track too), trim, ripple trim, razor, marquee and snapping.
/// </summary>
public sealed class TimelineControllerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mf-timeline-controller").FullName;
    private readonly TimelineDocument _doc;
    private readonly TimelineViewState _view = new() { Width = 1000, Height = 400 };
    private readonly TimelineController _controller;

    public TimelineControllerTests()
    {
        // V1/A1: [0, 4) and [4, 8) of a 10 s recording, linked pairs; 100 px per second.
        _doc = Document(Append(Append(Landscape(), 0, 4), 4, 8), _dir);
        _view.SetView(0, 100);
        _controller = new TimelineController(_doc, _view);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Timeline T => _doc.Timeline;

    private Clip First => T.ClipsOn(V1(T))[0];

    private Clip Second => T.ClipsOn(V1(T))[1];

    private void Gesture(System.Numerics.Vector2 from, System.Numerics.Vector2 to, EditorModifiers modifiers = EditorModifiers.None)
    {
        _controller.Press(from, modifiers);
        _controller.Drag((from + to) / 2, modifiers);
        _controller.Drag(to, modifiers);
        _controller.Release(to);
    }

    [Fact]
    public void ClickSelectsTheClipAndItsLinkedPartner()
    {
        var p = At(_view, T, V1(T), 2);
        _controller.Press(p, EditorModifiers.None);
        _controller.Release(p);

        Assert.Equal(2, _view.Selection.Count);
        Assert.Contains(First.Id, _view.Selection);
        Assert.Contains(T.ClipsOn(A1(T))[0].Id, _view.Selection);

        var empty = At(_view, T, V2(T), 2);
        _controller.Press(empty, EditorModifiers.None);
        _controller.Release(empty);
        Assert.Empty(_view.Selection);
    }

    [Fact]
    public void DraggingMovesTheLinkedPairToAnotherTrackAsOneStep()
    {
        _view.Snapping = false;
        var id = Second.Id;
        Gesture(At(_view, T, V1(T), 6), At(_view, T, V2(T), 7));

        var moved = T.FindClip(id)!;
        Assert.Equal(V2(T), moved.TrackId);
        Assert.Equal(5, moved.Start, 6);
        var partner = T.Clips.Single(c => c.LinkGroup == moved.LinkGroup && c.Id != id);
        Assert.Equal(5, partner.Start, 6); // the audio followed in time, on its own track
        Assert.Equal(A1(T), partner.TrackId);
        Assert.Single(_doc.History.Actions);
    }

    [Fact]
    public void DraggingAnEdgeTrimsAndCommandDragRipples()
    {
        _view.Snapping = false;
        Gesture(At(_view, T, V1(T), 4) with { X = 399 }, At(_view, T, V1(T), 3) with { X = 300 });
        Assert.Equal(3, TimelineLayout.End(First, T.Rate), 6);
        Assert.Equal(4, Second.Start, 6); // a plain trim leaves a gap

        _doc.History.Undo();
        Gesture(At(_view, T, V1(T), 4) with { X = 399 }, At(_view, T, V1(T), 3) with { X = 300 }, EditorModifiers.Command);
        Assert.Equal(3, TimelineLayout.End(First, T.Rate), 6);
        Assert.Equal(3, Second.Start, 6); // the ripple closed it
    }

    [Fact]
    public void TheRazorCutsTheClipAndItsPartnerWhereClicked()
    {
        _view.Snapping = false;
        _view.Tool = TimelineTool.Razor;
        var p = At(_view, T, V1(T), 1.5);
        _controller.Press(p, EditorModifiers.None);
        _controller.Release(p);

        Assert.Equal(3, T.ClipsOn(V1(T)).Count);
        Assert.Equal(3, T.ClipsOn(A1(T)).Count);
        Assert.Equal(1.5, T.ClipsOn(V1(T))[1].Start, 6);
    }

    [Fact]
    public void AMarqueeSelectsEveryClipItTouches()
    {
        var top = At(_view, T, V2(T), 3.5);
        var bottom = At(_view, T, V1(T), 4.5);
        Gesture(top, bottom);

        Assert.Equal(4, _view.Selection.Count); // both pairs: the marquee crosses the cut at 4 s
        Assert.Null(_view.Marquee);
    }

    [Fact]
    public void MovingSnapsToThePlayhead()
    {
        _view.Playhead = 9;
        var id = Second.Id;
        // The clip's start (4 s) dragged to 4.96 s: its end (8.96 s) is 4 px from the playhead, inside the 8 px tolerance.
        _controller.Press(At(_view, T, V1(T), 6), EditorModifiers.None);
        _controller.Drag(At(_view, T, V1(T), 6.5), EditorModifiers.None);
        _controller.Drag(At(_view, T, V1(T), 6.96), EditorModifiers.None);
        Assert.Equal(9, _view.SnapLine);
        _controller.Release(At(_view, T, V1(T), 6.96));

        Assert.Equal(5, T.FindClip(id)!.Start, 6);
    }

    [Fact]
    public void TheRulerScrubs()
    {
        var seen = new List<double>();
        _controller.Seek = seen.Add;
        _controller.Press(new(250, 5), EditorModifiers.None);
        _controller.Drag(new(300, 5), EditorModifiers.None);
        _controller.Release(new(300, 5));

        Assert.Equal([2.5, 3.0], seen);
        Assert.Empty(_doc.History.Actions);
    }

    [Fact]
    public void ALockedTrackRefusesTheMove()
    {
        _view.Snapping = false;
        _doc.Apply("Lock", t => TimelineEdits.SetTrack(t, V1(t), track => track with { Locked = true }));
        var before = T;
        Gesture(At(_view, T, V1(T), 6), At(_view, T, V1(T), 7));

        Assert.Same(before, T);
    }
}
