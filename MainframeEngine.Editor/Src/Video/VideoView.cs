using System.Collections.Frozen;
using System.Numerics;
using MainframeEngine.Editor.Music;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Timelines;
using MainframeEngine.Video.Timelines.Export;
using Silk.NET.Input;
// The game export library (MainframeEngine.Export, a global using) has types of the same names.
using ExportProgress = MainframeEngine.Video.Media.Export.ExportProgress;
using Timeline = MainframeEngine.Video.Timelines.Timeline;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// Video mode's per-frame driver, a <c>[Tool]</c> node of the workspace (docs/design/future/video-nle.md). It publishes the
/// active timeline tab's four sub-viewports to the UI (the Source and Timeline viewers, the timeline canvas and the cut
/// editor), routes the pointer over them, runs every <c>video.*</c> command (Premiere's keys come here through
/// <see cref="EditorWorkspace.VideoShortcutFor"/>), and runs the background jobs: probes, imports and exports. Steady frames
/// allocate nothing.
/// </summary>
[Tool]
public sealed class VideoView : Node
{
    public const string SourceTexture = "video-source";
    public const string ProgramTexture = "video-program";
    public const string TimelineTexture = "video-timeline";
    public const string CutsTexture = "video-cuts";

    private enum Area : byte
    {
        None,
        Timeline,
        Cuts,
        SourceBar,
    }

    private readonly EditorWorkspace _workspace;
    private readonly Dictionary<string, MediaRef> _probed = new(StringComparer.Ordinal);
    private readonly List<(string Path, VideoJob<MediaRef> Job, List<Action<MediaRef>> Then)> _probes = [];
    private readonly SubViewport?[] _registered = new SubViewport?[4];
    private TimelineTab? _tab;
    private MediaCache? _cache;
    private string? _cacheRoot;
    private Vector2 _mouse;
    private Area _dragging;
    private Area _hovering;
    private VideoJob<IReadOnlyList<string>>? _import;
    private Action<IReadOnlyList<string>>? _imported;
    private TempSession? _temp;

    public VideoView(EditorWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Tools = new ClipTools(workspace);
    }

    /// <summary>Cutback's tools on the selected clip, and the media's transcripts (G20d).</summary>
    public ClipTools Tools { get; }

    /// <summary>The timeline tab shown (the active tab in video mode), or null.</summary>
    public TimelineTab? Tab => _tab;

    /// <summary>The project's media cache (waveforms, filmstrips, posters), or null before video mode found ffmpeg.</summary>
    public MediaCache? Cache => _cache;

    /// <summary>No probe or import is running (QA's <c>wait-for video-jobs</c>).</summary>
    public bool IsIdle => _probes.Count == 0 && _import is null;

    /// <summary>True while any viewer or canvas texture is published (tests: a switch away unpublishes them).</summary>
    internal bool PublishesTextures => _registered.Any(r => r is not null);

    /// <summary>Starts preview decodes; tests swap it for a fake so ffmpeg never runs.</summary>
    internal Func<FfmpegLocation, IVideoDecoderFactory> DecoderFactory { get; set; } = static ffmpeg => new FfmpegVideoDecoderFactory(ffmpeg.FfmpegPath);

    /// <summary>Probes a file into a <see cref="MediaRef"/>; tests swap it.</summary>
    internal Func<FfmpegLocation, string, string, string, CancellationToken, Task<MediaRef>> Prober { get; set; } = MediaRefs.ProbeAsync;

    /// <summary>Runs one export; tests swap it so ffmpeg never starts.</summary>
    internal Func<FfmpegLocation, TempSession, TimelineExportRunner> ExporterFactory { get; set; } =
        static (ffmpeg, temp) => new TimelineExporter(ffmpeg, temp).ExportAsync;

    /// <summary>The ffmpeg video mode uses (Editor Settings › Video), or null after telling the user it is missing.</summary>
    public FfmpegLocation? FfmpegOrReport()
    {
        try
        {
            return _workspace.Commands.LocateFfmpeg(_workspace.Settings.VideoFfmpegPath);
        }
        catch (FfmpegMissingException e)
        {
            Log.Error($"[Editor] The video editor needs ffmpeg: {e.Message}");
            _workspace.Message.Show(new MessageRequest
            {
                Title = "ffmpeg not found",
                Message = $"The video editor needs ffmpeg:\n{e.Message}",
                Buttons = ["Video Settings…", "OK"],
                CancelButton = 1,
                Callback = (button, _) =>
                {
                    if (button == 0)
                        _workspace.Commands.Execute("video.settings");
                },
            });
            return null;
        }
    }

    public IVideoDecoderFactory Decoders(FfmpegLocation ffmpeg) => DecoderFactory(ffmpeg);

    /// <summary>The open project's media cache, made for <paramref name="ffmpeg"/> (one per project).</summary>
    public MediaCache? CacheFor(FfmpegLocation? ffmpeg)
    {
        var root = _workspace.Session.ProjectRoot;
        if (root is null)
            return null;
        if (_cache is null || _cacheRoot is null || !EditorSession.PathsEqual(_cacheRoot, root))
        {
            _cache?.Dispose();
            _cache = new MediaCache(Path.Combine(root, ".mainframe", "cache", "video"));
            _cacheRoot = root;
            Tools.Transcripts.Clear();
        }

        _cache.Ffmpeg ??= ffmpeg;
        return _cache;
    }

    // ── Every frame ──────────────────────────────────────────────────────────────────────────────────────────

    protected override void OnProcess(in GameTime gameTime)
    {
        _cache?.Pump();
        PollJobs();
        var tab = _workspace.Mode == EditorMode.Video ? _workspace.Session.ActiveTab as TimelineTab : null;
        if (!ReferenceEquals(tab, _tab))
            Switch(tab);
        tab?.SyncCutEditor(); // first: the panel's cut editor title and Inspector read the clip it shows
        var panel = _workspace.VideoPanel;
        panel.Tick(tab);
        _workspace.Toolbar.UpdateVideo(tab);
        if (tab is null)
            return;

        var scale = _workspace.Host.PixelScale;
        var timeline = panel.TimelineRect;
        var cuts = panel.CutsRect;
        var showTimeline = timeline.Width > 1 && timeline.Height > 1;
        var showCuts = cuts.Width > 1 && cuts.Height > 1;
        if (showTimeline)
        {
            tab.View.Width = timeline.Width;
            tab.View.Height = timeline.Height;
        }

        if (showCuts)
            tab.CutView.Width = cuts.Width;
        TimelineHost.Place(tab.CanvasViewport, tab.Canvas, timeline, scale, showTimeline);
        TimelineHost.Place(tab.CutViewport, tab.CutCanvas, cuts, scale, showCuts);
        var showProgram = panel.ProgramRect.Width > 1;
        var showSource = panel.SourceRect.Width > 1 && tab.Source.Preview is not null;
        SetMode(tab.Preview.Viewport, showProgram);
        if (tab.Source.Preview is { } source)
            SetMode(source.Viewport, showSource);
        Publish(0, SourceTexture, showSource ? tab.Source.Preview?.Viewport : null);
        Publish(1, ProgramTexture, showProgram ? tab.Preview.Viewport : null);
        Publish(2, TimelineTexture, showTimeline ? tab.CanvasViewport : null);
        Publish(3, CutsTexture, showCuts ? tab.CutViewport : null);
        panel.SetImages(_registered[0] is not null, _registered[1] is not null, _registered[2] is not null, _registered[3] is not null);
    }

    private static void SetMode(SubViewport viewport, bool shown)
    {
        var mode = shown ? SubViewportUpdateMode.Always : SubViewportUpdateMode.Disabled;
        if (viewport.UpdateMode != mode)
            viewport.UpdateMode = mode;
    }

    private void Publish(int slot, string name, SubViewport? viewport)
    {
        if (ReferenceEquals(_registered[slot], viewport) || Tree?.Servers.Get<UiServer>() is not { } ui)
            return;
        if (viewport is null)
            ui.UnregisterTexture(name);
        else
            ui.RegisterTexture(name, viewport);
        _registered[slot] = viewport;
    }

    // The shown tab changed: the old one's gestures end, and the UI stops sampling its targets.
    private void Switch(TimelineTab? tab)
    {
        if (_dragging != Area.None)
        {
            _tab?.Controller.CancelGesture();
            _tab?.CutController.CancelGesture();
        }

        _dragging = Area.None;
        _hovering = Area.None;
        _tab = tab;
        if (Tree?.Servers.Get<UiServer>() is { } ui)
            for (var i = 0; i < _registered.Length; i++)
                if (_registered[i] is not null)
                {
                    ui.UnregisterTexture(i switch { 0 => SourceTexture, 1 => ProgramTexture, 2 => TimelineTexture, _ => CutsTexture });
                    _registered[i] = null;
                }

        _workspace.VideoPanel.SetImages(false, false, false, false);
        if (tab is not null)
        {
            tab.Controller.ShowMenu = (clip, x, y) => ShowClipMenu(tab, clip, x, y);
            tab.CutController.ShowSegmentMenu = (_, x, y) => ShowCutMenu(tab, x, y);
        }
    }

    private void ShowClipMenu(TimelineTab tab, Clip? clip, float x, float y)
    {
        var rect = _workspace.VideoPanel.TimelineRect;
        List<MenuItem> items = clip is null
            ?
            [
                new MenuItem("Add Video Track", "video.add_video_track", Icon: "plus"),
                new MenuItem("Add Audio Track", "video.add_audio_track", Icon: "plus"),
            ]
            :
            [
                new MenuItem(clip.LinkGroup is null ? "Link" : "Unlink", "video.link", "Ctrl+L", Icon: "link"),
                new MenuItem(clip.Enabled ? "Disable" : "Enable", "video.toggle_enabled", Icon: "eye-off"),
                MenuItem.Separator,
                new MenuItem("Detect Silence", "video.detect_silence", Icon: "volume-off"),
                new MenuItem("Detect Idle Gameplay", "video.detect_idle", null, ClipTools.HasCapture(tab, clip), Icon: "hourglass"),
                new MenuItem("Transcribe", "video.transcribe", Icon: "file-text"),
                new MenuItem("Remove Filler Words", "video.remove_fillers", Icon: "eraser"),
                MenuItem.Separator,
                new MenuItem("Delete (Lift)", "video.lift", "Del", Icon: "trash"),
                new MenuItem("Ripple Delete", "video.ripple_delete", "Shift+Del", Icon: "trash-x"),
            ];
        _workspace.Popup.Show(items, rect.X + x, rect.Y + y, onCommand: id =>
        {
            if (id is not null)
                RunCommand(id);
        });
    }

    private void ShowCutMenu(TimelineTab tab, float x, float y)
    {
        var rect = _workspace.VideoPanel.CutsRect;
        _workspace.Popup.Show([new MenuItem("Delete Region", "edit.delete", "Del", Icon: "trash")], rect.X + x, rect.Y + y, onCommand: id =>
        {
            if (id == "edit.delete")
                Guard("Delete failed", () => tab.CutController.DeleteHovered());
        });
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────────────────────

    protected override void OnInput(InputEvent inputEvent)
    {
        if (_tab is not { } tab || _workspace.IsDialogOpen || _workspace.Popup.Visible)
            return;
        var panel = _workspace.VideoPanel;
        var scale = _workspace.Host.PointerScale;
        switch (inputEvent)
        {
            case InputEventMouseButton { Button: MouseButton.Left } button:
                _mouse = button.Position * scale;
                if (button.Pressed)
                    Press(tab, panel);
                else
                    Release(tab, panel);
                break;
            case InputEventMouseButton { Button: MouseButton.Right, Pressed: true } right:
                _mouse = right.Position * scale;
                if (_dragging != Area.None)
                    return;
                if (In(panel.TimelineRect))
                {
                    Guard("Timeline edit failed", () => tab.Controller.RightClick(Local(panel.TimelineRect)));
                    Handled();
                }
                else if (In(panel.CutsRect))
                {
                    Guard("Cut edit failed", () => tab.CutController.RightClick(Local(panel.CutsRect)));
                    Handled();
                }

                break;
            case InputEventMouseMotion motion:
                _mouse = motion.Position * scale;
                Move(tab, panel);
                break;
            case InputEventMouseWheel wheel:
                if (In(panel.TimelineRect))
                {
                    tab.Controller.Wheel(Local(panel.TimelineRect), wheel.Delta, _workspace.Modifiers);
                    Handled();
                }
                else if (In(panel.CutsRect))
                {
                    tab.CutController.Wheel(Local(panel.CutsRect), wheel.Delta, _workspace.Modifiers);
                    Handled();
                }

                break;
        }
    }

    private void Press(TimelineTab tab, VideoPanel panel)
    {
        if (In(panel.SourceRect) || In(panel.SourceBarRect))
            tab.SourceFocused = true;
        else if (In(panel.ProgramRect) || In(panel.TimelineRect))
            tab.SourceFocused = false;

        if (In(panel.TimelineRect))
        {
            _dragging = Area.Timeline;
            Guard("Timeline edit failed", () => tab.Controller.Press(Local(panel.TimelineRect), _workspace.Modifiers));
            Handled();
        }
        else if (In(panel.CutsRect))
        {
            _dragging = Area.Cuts;
            Guard("Cut edit failed", () => tab.CutController.Press(Local(panel.CutsRect), _workspace.Modifiers));
            Handled();
        }
        else if (In(panel.SourceBarRect) && tab.Source.Media is not null)
        {
            _dragging = Area.SourceBar;
            ScrubSource(tab, panel);
            Handled();
        }
    }

    private void Release(TimelineTab tab, VideoPanel panel)
    {
        // A media pool row dragged here: placed where it is let go (overwrite; Cmd inserts).
        if (_workspace.FileDrag is { } file)
        {
            _workspace.EndFileDrag();
            if (In(panel.TimelineRect))
            {
                var local = Local(panel.TimelineRect);
                PlaceAt(tab, [file], local, insert: (_workspace.Modifiers & EditorModifiers.Command) != 0);
                Handled();
            }

            return;
        }

        switch (_dragging)
        {
            case Area.Timeline:
                Guard("Timeline edit failed", () => tab.Controller.Release(Local(panel.TimelineRect)));
                Handled();
                break;
            case Area.Cuts:
                Guard("Cut edit failed", () => tab.CutController.Release(Local(panel.CutsRect)));
                Handled();
                break;
            case Area.SourceBar:
                Handled();
                break;
        }

        _dragging = Area.None;
    }

    private void Move(TimelineTab tab, VideoPanel panel)
    {
        switch (_dragging)
        {
            case Area.Timeline:
                Guard("Timeline edit failed", () => tab.Controller.Drag(Local(panel.TimelineRect), _workspace.Modifiers));
                Handled();
                return;
            case Area.Cuts:
                Guard("Cut edit failed", () => tab.CutController.Drag(Local(panel.CutsRect), _workspace.Modifiers));
                Handled();
                return;
            case Area.SourceBar:
                ScrubSource(tab, panel);
                Handled();
                return;
        }

        var over = In(panel.TimelineRect) ? Area.Timeline : In(panel.CutsRect) ? Area.Cuts : Area.None;
        if (over == Area.Timeline)
            tab.Controller.Hover(Local(panel.TimelineRect));
        else if (_hovering == Area.Timeline)
            tab.View.HoverEdge = null;
        if (over == Area.Cuts)
            tab.CutController.Hover(Local(panel.CutsRect));
        else if (_hovering == Area.Cuts)
            tab.CutController.Hover(new Vector2(-1, -1));
        _hovering = over;
    }

    private void ScrubSource(TimelineTab tab, VideoPanel panel)
    {
        var bar = panel.SourceBarRect;
        if (tab.Source.Media is not { } media || bar.Width <= 0)
            return;
        tab.Source.Seek(Math.Clamp((_mouse.X - bar.X) / bar.Width, 0, 1) * media.Duration);
        panel.SourceChanged();
    }

    /// <summary>
    /// The keys only this view knows: Delete/Backspace over the cut editor dissolve the hovered region. Everything else is a
    /// command (<see cref="EditorWorkspace.VideoShortcutFor"/>).
    /// </summary>
    public bool HandleKey(Key key, EditorModifiers modifiers)
    {
        if (_workspace.Mode != EditorMode.Video || _tab is not { } tab || _hovering != Area.Cuts)
            return false;
        var handled = false;
        Guard("Cut edit failed", () => handled = tab.CutController.Key(key, modifiers));
        return handled;
    }

    private bool In(LayoutRect rect) => rect.Width > 0 && rect.Contains(_mouse.X, _mouse.Y);

    private Vector2 Local(LayoutRect rect) => new(_mouse.X - rect.X, _mouse.Y - rect.Y);

    private void Handled() => GetViewport()?.SetInputAsHandled();

    private void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            _workspace.Commands.ReportError(what, e);
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs a <c>video.*</c> command. False when it is not one (the caller runs it as usual).</summary>
    // The commands Run knows: one outside this set warns "Unknown command" as any other does.
    private static readonly FrozenSet<string> TimelineCommands = FrozenSet.ToFrozenSet(
    [
        "video.add_audio_track",
        "video.add_edit",
        "video.add_edit_all",
        "video.add_video_track",
        "video.clear_marks",
        "video.deselect",
        "video.end",
        "video.export",
        "video.frame_back",
        "video.frame_forward",
        "video.home",
        "video.insert",
        "video.lift",
        "video.link",
        "video.mark_in",
        "video.mark_out",
        "video.next_edit",
        "video.overwrite",
        "video.play",
        "video.prev_edit",
        "video.ripple_delete",
        "video.ripple_end",
        "video.ripple_start",
        "video.select_all",
        "video.shuttle_back",
        "video.shuttle_forward",
        "video.shuttle_stop",
        "video.snap",
        "video.source_play",
        "video.stop",
        "video.toggle_enabled",
        "video.detect_silence",
        "video.detect_idle",
        "video.transcribe",
        "video.remove_fillers",
        "video.add_title",
        "video.add_dissolve",
        "video.add_dip",
        "video.remove_transition",
        "video.create_captions",
        "video.show_transcript",
        "video.show_inspector",
        "video.tool_razor",
        "video.tool_select",
        "video.zoom_fit",
        "video.zoom_in",
        "video.zoom_out",
    ], StringComparer.Ordinal);

    public bool RunCommand(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        switch (id)
        {
            case "video.import": Import(); return true;
            case "video.new_timeline": NewTimeline(); return true;
            case "video.new_folder": NewFolder(); return true;
            case "video.settings": return false;
        }

        if (!TimelineCommands.Contains(id))
            return false;
        if (_workspace.Session.ActiveTab is not TimelineTab tab)
            return true; // a video command without a timeline: its buttons show disabled; nothing to do
        Guard("Timeline edit failed", () => Run(tab, id));
        return true;
    }

    private void Run(TimelineTab tab, string id)
    {
        var doc = tab.Document;
        var view = tab.View;
        var timeline = tab.Timeline;
        var playhead = view.Playhead;
        switch (id)
        {
            case "video.play":
                if (tab.SourceFocused && tab.Source.Media is not null)
                {
                    tab.Pause();
                    tab.Source.TogglePlay();
                }
                else
                {
                    tab.TogglePlay();
                }

                break;
            case "video.source_play":
                tab.SourceFocused = true;
                tab.Pause();
                tab.Source.TogglePlay();
                break;
            case "video.stop":
                tab.Source.Pause();
                tab.Pause();
                tab.Seek(0);
                break;
            case "video.shuttle_forward":
                if (tab.SourceFocused && tab.Source.Media is not null)
                {
                    if (!tab.Source.IsPlaying)
                        tab.Source.TogglePlay();
                }
                else if (!tab.Preview.IsPlaying)
                {
                    tab.TogglePlay();
                }

                break;
            case "video.shuttle_stop":
                tab.Pause();
                tab.Source.Pause();
                break;
            case "video.shuttle_back":
                tab.Pause();
                tab.Source.Pause();
                if (tab.SourceFocused && tab.Source.Media is not null)
                    tab.Source.Seek(tab.Source.Position - 1);
                else
                    tab.Seek(playhead - 1);
                _workspace.VideoPanel.SourceChanged();
                break;
            case "video.frame_back":
                tab.Pause();
                tab.Seek(playhead - timeline.Rate.FrameDuration);
                break;
            case "video.frame_forward":
                tab.Pause();
                tab.Seek(playhead + timeline.Rate.FrameDuration);
                break;
            case "video.home":
                tab.Seek(0);
                break;
            case "video.end":
                tab.Seek(TimelineLayout.Duration(timeline));
                break;
            case "video.prev_edit":
                tab.Seek(EditPoint(timeline, playhead, forward: false));
                break;
            case "video.next_edit":
                tab.Seek(EditPoint(timeline, playhead, forward: true));
                break;
            case "video.tool_select":
                view.Tool = TimelineTool.Select;
                break;
            case "video.tool_razor":
                view.Tool = TimelineTool.Razor;
                break;
            case "video.snap":
                view.Snapping = !view.Snapping;
                break;
            case "video.zoom_in":
                view.ZoomAbout(view.TimeToX(playhead), 2);
                break;
            case "video.zoom_out":
                view.ZoomAbout(view.TimeToX(playhead), -2);
                break;
            case "video.zoom_fit":
                view.Fit(TimelineLayout.Duration(timeline));
                break;
            case "video.select_all":
                view.Selection.Clear();
                foreach (var clip in timeline.Clips)
                    view.Selection.Add(clip.Id);
                break;
            case "video.deselect":
                view.Selection.Clear();
                view.SelectedTransition = null;
                break;
            case "video.add_title":
                AddTitle(tab, playhead);
                break;
            case "video.add_dissolve":
            case "video.add_dip":
                AddTransitionAtPlayhead(tab, id == "video.add_dissolve" ? TransitionKind.Dissolve : TransitionKind.DipToBlack);
                break;
            case "video.create_captions":
                CreateCaptions(tab);
                break;
            case "video.add_edit":
                doc.Apply("Add Edit", t => TimelineEdits.Razor(t, playhead, EditTracks(tab)));
                break;
            case "video.add_edit_all":
                doc.Apply("Add Edit", t => TimelineEdits.Razor(t, playhead));
                break;
            case "video.ripple_start":
            case "video.ripple_end":
                var edge = id == "video.ripple_start" ? TrimEdge.Start : TrimEdge.End;
                doc.Apply("Ripple Trim", t => RippleToPlayhead(tab, t, playhead, edge));
                if (edge == TrimEdge.Start && TimelineLayout.ClipAt(doc.Timeline, EditTracks(tab).FirstOrDefault() ?? "", playhead) is null)
                    tab.Seek(playhead); // the frame after the ripple
                break;
            case "video.lift" when view.SelectedTransition is not null:
            case "video.remove_transition":
                if (view.SelectedTransition is { } transition)
                    doc.Apply("Remove Transition", t => TimelineEdits.RemoveTransition(t, transition));
                view.SelectedTransition = null;
                break;
            case "video.lift":
                doc.Apply("Delete", t => TimelineEdits.Lift(t, [.. view.Selection]));
                break;
            case "video.ripple_delete":
                doc.Apply("Ripple Delete", t => TimelineEdits.RippleDelete(t, [.. view.Selection]));
                break;
            case "video.link":
                var selected = view.Selection.ToList();
                var linked = selected.Any(s => timeline.FindClip(s)?.LinkGroup is not null);
                doc.Apply(linked ? "Unlink" : "Link", t => linked ? TimelineEdits.Unlink(t, selected) : TimelineEdits.Link(t, selected));
                break;
            case "video.toggle_enabled":
                if (tab.Primary is { } primary)
                {
                    var ids = timeline.LinkedWith(primary).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
                    doc.Apply("Enable Clip", t => t with { Clips = [.. t.Clips.Select(c => ids.Contains(c.Id) ? c with { Enabled = !primary.Enabled } : c)] });
                }

                break;
            case "video.mark_in":
                if (tab.SourceFocused && tab.Source.Media is not null)
                    tab.Source.MarkIn();
                else
                    doc.Apply("Mark In", t => TimelineEdits.SetInOut(t, playhead, t.OutPoint is { } o && o > playhead ? o : null));
                _workspace.VideoPanel.SourceChanged();
                break;
            case "video.mark_out":
                if (tab.SourceFocused && tab.Source.Media is not null)
                    tab.Source.MarkOut();
                else
                    doc.Apply("Mark Out", t => TimelineEdits.SetInOut(t, t.InPoint is { } i && i < playhead ? i : null, playhead));
                _workspace.VideoPanel.SourceChanged();
                break;
            case "video.clear_marks":
                if (tab.SourceFocused && tab.Source.Media is not null)
                    tab.Source.ClearMarks();
                else
                    doc.Apply("Clear In/Out", t => TimelineEdits.SetInOut(t, null, null));
                _workspace.VideoPanel.SourceChanged();
                break;
            case "video.insert":
            case "video.overwrite":
                PlaceFromSource(tab, insert: id == "video.insert");
                break;
            case "video.add_video_track":
                doc.Apply("Add Track", t => TimelineEdits.AddTrack(t, TrackKind.Video));
                break;
            case "video.add_audio_track":
                doc.Apply("Add Track", t => TimelineEdits.AddTrack(t, TrackKind.Audio));
                break;
            case "video.export":
                Export(tab);
                break;
            case "video.detect_silence":
                Tools.DetectSilence(tab);
                break;
            case "video.detect_idle":
                Tools.DetectIdle(tab);
                break;
            case "video.transcribe":
                Tools.Transcribe(tab);
                break;
            case "video.remove_fillers":
                Tools.RemoveFillers(tab);
                break;
            case "video.show_transcript":
                tab.ShowsTranscript = true;
                break;
            case "video.show_inspector":
                tab.ShowsTranscript = false;
                break;
        }
    }

    // Add Edit cuts the tracks of the selected clips, or every unlocked track when nothing is selected.
    private static List<string> EditTracks(TimelineTab tab)
    {
        var tracks = new List<string>();
        foreach (var id in tab.View.Selection)
            if (tab.Timeline.FindClip(id) is { } clip && !tracks.Contains(clip.TrackId))
                tracks.Add(clip.TrackId);
        if (tracks.Count == 0)
            tracks.AddRange(tab.Timeline.Tracks.Where(t => !t.Locked).Select(t => t.Id));
        return tracks;
    }

    // Q / W: the selected clips under the playhead (else every clip there) ripple-trim their start or end to it.
    private static Timeline RippleToPlayhead(TimelineTab tab, Timeline timeline, double playhead, TrimEdge edge)
    {
        var tracks = EditTracks(tab);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var result = timeline;
        foreach (var trackId in tracks)
        {
            if (TimelineLayout.ClipAt(result, trackId, playhead) is not { } clip || done.Contains(clip.LinkGroup ?? clip.Id))
                continue;
            done.Add(clip.LinkGroup ?? clip.Id);
            result = TimelineEdits.RippleTrim(result, clip.Id, edge, playhead);
        }

        return result;
    }

    /// <summary>The nearest clip start or end before or after <paramref name="time"/> (0 and the end at the extremes).</summary>
    internal static double EditPoint(Timeline timeline, double time, bool forward)
    {
        var best = forward ? TimelineLayout.Duration(timeline) : 0;
        foreach (var clip in timeline.Clips)
        {
            foreach (var t in (ReadOnlySpan<double>)[clip.Start, TimelineLayout.End(clip, timeline.Rate)])
            {
                if (forward && t > time + 1e-6 && t < best)
                    best = t;
                else if (!forward && t < time - 1e-6 && t > best)
                    best = t;
            }
        }

        return best;
    }

    // ── Media: probe, Source viewer, placing ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The <see cref="MediaRef"/> of the project file at <paramref name="fullPath"/>, probed once (in the background the
    /// first time) and passed to <paramref name="then"/> on the main thread.
    /// </summary>
    public void WithMedia(string fullPath, Action<MediaRef> then)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        ArgumentNullException.ThrowIfNull(then);
        var full = Path.GetFullPath(fullPath);
        if (_probed.TryGetValue(full, out var known))
        {
            then(known);
            return;
        }

        foreach (var pending in _probes)
            if (pending.Path == full)
            {
                pending.Then.Add(then);
                return;
            }

        if (FfmpegOrReport() is not { } ffmpeg)
            return;
        var database = AssetDatabase.Current;
        var stored = MediaImporter.IsInProject(full, database.ProjectRoot) ? database.ToProjectPath(full) : full;
        var uid = stored != full ? database.ReadOrCreateMeta(stored, create: true)?.Uid ?? "" : "";
        var prober = Prober;
        _probes.Add((full, VideoJob.Start("probe", (_, ct) => prober(ffmpeg, full, stored, uid, ct)), [then]));
    }

    /// <summary>Shows a media file in the active timeline's Source viewer (double-click in the pool, File › Open).</summary>
    public void ShowInSource(string fullPath)
    {
        if (_workspace.Session.ActiveTab is not TimelineTab tab)
        {
            _workspace.Message.Show(new MessageRequest
            {
                Title = "Source viewer",
                Message = "Open or create a timeline first: the Source viewer belongs to a timeline tab.",
                Buttons = ["New Timeline", "OK"],
                CancelButton = 1,
                Callback = (button, _) =>
                {
                    if (button == 0)
                        NewTimeline();
                },
            });
            return;
        }

        WithMedia(fullPath, media =>
        {
            tab.Source.Load(media, fullPath);
            tab.SourceFocused = true;
            _workspace.VideoPanel.SourceChanged();
        });
    }

    // , and .: the Source viewer's marked range at the playhead, on the target tracks.
    private static void PlaceFromSource(TimelineTab tab, bool insert)
    {
        if (tab.Source.Media is not { } media)
        {
            tab.Notice = "Show a clip in the Source viewer first (double-click it in the media pool).";
            return;
        }

        var (i, o) = tab.Source.Range;
        Place(tab, media, i, o, tab.View.Playhead, null, insert);
        tab.SourceFocused = false;
    }

    // Media dropped on the timeline (from the pool or Finder): each file at the drop time, one after another, on the track
    // under the pointer when it is of the file's kind, else the target track.
    private void PlaceAt(TimelineTab tab, IReadOnlyList<string> files, Vector2 local, bool insert)
    {
        var at = Math.Max(0, tab.View.XToTime(local.X));
        if (tab.View.Snapping)
            at = Snapping.Snap(at, Snapping.Candidates(tab.Timeline, tab.View.Playhead), TimelineViewState.SnapPixels * tab.View.SecondsPerPixel).Time;
        var lane = tab.View.LaneAt(tab.Timeline, local.Y);
        PlaceSequence(tab, files, 0, at, lane, insert);
    }

    private void PlaceSequence(TimelineTab tab, IReadOnlyList<string> files, int index, double at, TimelineLane? lane, bool insert)
    {
        if (index >= files.Count)
            return;
        WithMedia(files[index], media =>
        {
            var end = Place(tab, media, 0, media.Duration, at, lane, insert);
            PlaceSequence(tab, files, index + 1, end, lane, insert);
        });
    }

    // Places [sourceIn, sourceOut) of the media at `at`; the end of what was placed (the next file's start).
    private static double Place(TimelineTab tab, MediaRef media, double sourceIn, double sourceOut, double at, TimelineLane? lane, bool insert)
    {
        var timeline = tab.Timeline;
        var video = lane is { Kind: TrackKind.Video } v ? v.TrackId : tab.View.TargetOf(timeline, TrackKind.Video);
        var audio = lane is { Kind: TrackKind.Audio } a ? a.TrackId : tab.View.TargetOf(timeline, TrackKind.Audio);
        var name = insert ? "Insert" : "Overwrite";
        var still = TimelineFactory.IsImagePath(media.Path);
        if (still)
            (sourceIn, sourceOut) = (0, TimelineEdits.DefaultStillSeconds); // an image is a still: 5 s, longer by trimming
        var placed = video is not null && still
            ? tab.Document.Apply(name, t => TimelineEdits.PlaceStill(t, media, ClipKind.Image, null, at, sourceOut, video, insert))
            : tab.Document.Apply(name, t => insert
                ? TimelineEdits.Insert(t, media, sourceIn, sourceOut, at, video, audio)
                : TimelineEdits.Overwrite(t, media, sourceIn, sourceOut, at, video, audio));
        if (!placed)
        {
            tab.Notice = $"{Path.GetFileName(media.Path)} could not be placed: its track is locked or of the wrong kind.";
            return at;
        }

        var end = at + timeline.Rate.Snap(sourceOut - sourceIn);
        if (TimelineLayout.Duration(timeline) <= 0)
            tab.View.Fit(TimelineLayout.Duration(tab.Timeline));
        tab.Seek(end);
        return end;
    }

    // ── Titles, transitions, captions (G20.2) ────────────────────────────────────────────────────────────────

    // A 3 s title at the playhead on the top video track, or on a new one above it when the top one is taken there.
    private static void AddTitle(TimelineTab tab, double at)
    {
        const double Seconds = 3;
        var style = TitleStyle.Default;
        var placed = tab.Document.Apply("Add Title", t =>
        {
            var top = t.TracksOf(TrackKind.Video)[^1];
            var busy = top.Locked || t.ClipsOn(top.Id).Any(c => c.Start < at + Seconds && TimelineLayout.End(c, t.Rate) > at);
            if (busy)
                t = TimelineEdits.AddTrack(t, TrackKind.Video);
            var track = t.TracksOf(TrackKind.Video)[^1].Id;
            return TimelineEdits.PlaceStill(t, TimelineFactory.TitleMedia(style, t.Settings), ClipKind.Title, style, at, Seconds, track, insert: false);
        });
        if (!placed)
            return;
        var title = tab.Timeline.Clips.Where(c => c.Kind == ClipKind.Title && Math.Abs(c.Start - tab.Timeline.Rate.Snap(at)) < 1e-6).LastOrDefault();
        tab.View.Selection.Clear();
        tab.View.SelectedTransition = null;
        if (title is not null)
            tab.View.Selection.Add(title.Id);
        tab.ShowsTranscript = false;
    }

    // Cmd+D (dissolve) / Shift+Cmd+D (dip): at the edit nearest the playhead on the selected clip's track, else the
    // target video track; the reason in the notice when it cannot go there.
    private static void AddTransitionAtPlayhead(TimelineTab tab, TransitionKind kind)
    {
        var timeline = tab.Timeline;
        var track = tab.Primary?.TrackId ?? tab.View.TargetOf(timeline, TrackKind.Video);
        if (track is null)
            return;
        (Clip Out, Clip In)? best = null;
        var bestDistance = double.MaxValue;
        foreach (var clip in timeline.ClipsOn(track))
        {
            if (TimelineEdits.Following(timeline, clip) is not { } next)
                continue;
            var distance = Math.Abs(next.Start - tab.View.Playhead);
            if (distance < bestDistance)
            {
                best = (clip, next);
                bestDistance = distance;
            }
        }

        if (best is not { } edit)
        {
            tab.Notice = "No edit on this track: a transition goes where one clip ends and the next begins.";
            return;
        }

        if (TimelineEdits.TransitionProblem(timeline, edit.Out.Id, edit.In.Id, kind, TimelineEdits.DefaultTransitionSeconds) is { } problem)
        {
            tab.Notice = problem;
            return;
        }

        tab.Document.Apply(kind == TransitionKind.Dissolve ? "Add Cross Dissolve" : "Add Dip to Black",
            t => TimelineEdits.AddTransition(t, edit.Out.Id, edit.In.Id, kind, TimelineEdits.DefaultTransitionSeconds));
        tab.View.Selection.Clear();
        tab.View.SelectedTransition = tab.Timeline.Transitions.FirstOrDefault(x => x.OutClip == edit.Out.Id)?.Id;
    }

    // Timeline › Create Captions: the selected clip's kept transcript words as caption titles on a new track.
    private void CreateCaptions(TimelineTab tab)
    {
        if (tab.Primary is not { } clip || clip.IsStill)
        {
            tab.Notice = "Select a clip of a recording first: its transcript becomes the captions.";
            return;
        }

        if (Tools.WordsOf(tab, clip) is not { Count: > 0 } words)
        {
            tab.Notice = "This clip has no transcript yet: Transcribe it first (the toolbar or the clip's menu).";
            return;
        }

        var captions = CaptionPlanner.Plan(clip, words, tab.Timeline.Rate);
        if (captions.Count == 0)
        {
            tab.Notice = "No kept words in this clip to caption.";
            return;
        }

        tab.Document.Apply("Create Captions", t => TimelineEdits.AddCaptions(t, captions));
        tab.Notice = captions.Count == 1 ? "Made 1 caption." : $"Made {captions.Count} captions.";
    }

    // ── Import, OS drops, new timelines and folders ──────────────────────────────────────────────────────────

    /// <summary>File › Import Media: a picker; the file is copied into the pool's folder.</summary>
    public void Import()
    {
        if (_workspace.VideoPanel.Pool is not { } pool)
        {
            ShowNeedsProject();
            return;
        }

        var model = new FilePickerModel(FilePickerMode.Open, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["*.mp4", "*.mov", "*.mkv", "*.webm", "*.m4v", "*.wav", "*.ogg", "*.mp3", "*.flac", "*.m4a", "*.png", "*.jpg"]);
        _workspace.FilePicker.Show(model, "Import Media", "Import", path => ImportFiles([path], pool.Folder, null));
    }

    /// <summary>Copies <paramref name="files"/> into <paramref name="folder"/> in the background; <paramref name="then"/> gets the pool's files.</summary>
    public void ImportFiles(IReadOnlyList<string> files, string folder, Action<IReadOnlyList<string>>? then)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (_import is not null)
        {
            _workspace.Message.Show(new MessageRequest { Title = "Import Media", Message = "An import is still copying; try again when it finishes.", Buttons = ["OK"] });
            return;
        }

        var root = _workspace.Session.ProjectRoot;
        if (root is null)
        {
            ShowNeedsProject();
            return;
        }

        _imported = then;
        _import = VideoJob.Start<IReadOnlyList<string>>("import", (p, ct) => MediaImporter.ImportAsync(files, folder, root, p, ct));
        Log.Info($"[Video] Importing {files.Count} file(s) into {Path.GetRelativePath(root, folder)}…");
    }

    /// <summary>
    /// Files dropped from Finder (<see cref="SceneTree.FilesDropped"/> through the workspace): over the timeline they are
    /// imported and placed at the time and track there; anywhere else in video mode they are imported into the pool.
    /// </summary>
    public void DropOsFiles(IReadOnlyList<string> paths, Vector2 position)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var panel = _workspace.VideoPanel;
        if (panel.Pool is not { } pool)
        {
            ShowNeedsProject();
            return;
        }

        _mouse = position * _workspace.Host.PointerScale;
        var tab = _tab;
        var overTimeline = tab is not null && In(panel.TimelineRect);
        var local = Local(panel.TimelineRect);
        var folder = In(panel.PoolRect) ? pool.Folder : Path.Combine(pool.RootFolder, "Media");
        ImportFiles(paths, folder, imported =>
        {
            if (overTimeline && tab is not null && _workspace.Session.Tabs.Contains(tab))
                PlaceAt(tab, [.. imported.Where(p => MediaPool.KindOf(p, false) is MediaPoolKind.Video or MediaPoolKind.Audio or MediaPoolKind.Image)], local, insert: false);
        });
    }

    /// <summary>File › New Timeline: asks for a name and a shape, writes it in <c>Assets/Video/Timelines</c> and opens it.</summary>
    public void NewTimeline()
    {
        if (_workspace.VideoPanel.Pool is not { } pool)
        {
            ShowNeedsProject();
            return;
        }

        _workspace.Message.Show(new MessageRequest
        {
            Title = "New Timeline",
            Message = "Name the timeline and choose its frame (you can change the size and rate in the Inspector later):",
            Input = NextTimelineName(pool),
            Buttons = ["Vertical 9:16", "Landscape 16:9", "Square 1:1", "Cancel"],
            DefaultButton = 0,
            CancelButton = 3,
            Icon = "timeline",
            Callback = (button, name) =>
            {
                if (button is < 0 or > 2)
                    return;
                var (w, h) = VideoPanel.SizePresets[button];
                CreateTimeline(pool, name, new TimelineSettings(w, h, new FrameRate(30, 1)), null);
            },
        });
    }

    private static string NextTimelineName(MediaPool pool)
    {
        var folder = Path.Combine(pool.RootFolder, "Timelines");
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? "Timeline" : $"Timeline {n}";
            if (!File.Exists(Path.Combine(folder, name + TimelineSerializer.FileExtension)))
                return name;
        }
    }

    /// <summary>Writes a new timeline (with <paramref name="media"/> on V1 and A1, when given) and opens it.</summary>
    internal void CreateTimeline(MediaPool pool, string? name, TimelineSettings settings, MediaRef? media)
    {
        var clean = string.Join("_", (name ?? "").Trim().Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0)
            clean = "Timeline";
        var folder = Path.Combine(pool.RootFolder, "Timelines");
        Directory.CreateDirectory(folder);
        var path = MediaImporter.Destination(folder, clean + TimelineSerializer.FileExtension, File.Exists);
        var timeline = TimelineFactory.New(settings);
        if (media is not null)
        {
            var tracks = timeline.Tracks;
            timeline = TimelineEdits.Append(timeline, media, 0, media.Duration, tracks[0].Id, tracks.First(t => t.Kind == TrackKind.Audio).Id);
        }

        Guard("Could not create the timeline", () =>
        {
            TimelineSerializer.SaveAsync(timeline, path, CancellationToken.None).GetAwaiter().GetResult();
            AssetDatabase.Current.Register(timeline.Uid, path);
            _workspace.VideoPanel.RefreshPool();
            _workspace.Commands.OpenTimeline(path);
        });
    }

    /// <summary>
    /// The recording dialog's Open in Video Editor: a timeline named after the recording, at its size and rate, with it on
    /// V1 and A1, in <c>Assets/Video/Timelines</c>, opened in video mode.
    /// </summary>
    public void OpenRecording(string recording)
    {
        ArgumentException.ThrowIfNullOrEmpty(recording);
        _workspace.SetMode(EditorMode.Video);
        if (_workspace.VideoPanel.Pool is not { } pool)
        {
            ShowNeedsProject();
            return;
        }

        WithMedia(recording, media => CreateTimeline(pool, Path.GetFileNameWithoutExtension(recording), TimelineFactory.SettingsFor(media), media));
    }

    private void NewFolder()
    {
        if (_workspace.VideoPanel.Pool is not { } pool)
        {
            ShowNeedsProject();
            return;
        }

        _workspace.Message.Show(new MessageRequest
        {
            Title = "New Folder",
            Message = $"A folder in {pool.DisplayPath}:",
            Input = "New Folder",
            Buttons = ["Create", "Cancel"],
            DefaultButton = 0,
            CancelButton = 1,
            Callback = (button, name) =>
            {
                if (button != 0 || string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return;
                Guard("Could not create the folder", () => Directory.CreateDirectory(Path.Combine(pool.Folder, name.Trim())));
                _workspace.VideoPanel.RefreshPool();
            },
        });
    }

    private void ShowNeedsProject() => _workspace.Message.Show(new MessageRequest
    {
        Title = "Video",
        Message = "Video mode edits a game project's video: open a project first.",
        Buttons = ["OK"],
    });

    // ── Export ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>File › Export Video: opens the dialog, or cancels the export that is running.</summary>
    public void Export(TimelineTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.ExportJob is { } running)
        {
            running.Cancel();
            return;
        }

        if (TimelineLayout.Duration(tab.Timeline) <= 0)
        {
            tab.Notice = TimelineExport.NothingToExport;
            return;
        }

        tab.Controller.CancelGesture();
        _workspace.ExportVideo.Open(tab);
    }

    /// <summary>
    /// Starts the export job on the timeline as it is now (an immutable snapshot, with every media file's path resolved
    /// here on the main thread) and pauses the viewers; <see cref="PollJobs"/> reports how it ends.
    /// </summary>
    public void StartExport(TimelineTab tab, TimelineExportPreset preset, double start, double end, string path)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(preset);
        if (tab.ExportJob is not null || !_workspace.Session.Tabs.Contains(tab))
            return;
        if (FfmpegOrReport() is not { } ffmpeg)
            return;
        var timeline = tab.Timeline;
        var paths = timeline.Clips.Select(c => c.Media).Distinct().ToDictionary(m => m, tab.Document.Resolve);
        tab.Pause();
        tab.Source.Pause();
        var runner = ExporterFactory(ffmpeg, _temp ??= new TempSession());
        tab.ExportJob = VideoJob.Start("export", async (p, ct) =>
        {
            await runner(timeline, preset, start, end, path, m => paths[m], new ProgressSink(p), ct).ConfigureAwait(false);
            return path;
        });
        Log.Info($"[Video] Exporting {Path.GetFileName(tab.FilePath)} to {path} ({preset.Name})…");
    }

    // ── Jobs ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Main thread, every frame: finished probes, imports and exports.</summary>
    internal void PollJobs()
    {
        for (var i = _probes.Count - 1; i >= 0; i--)
        {
            var (path, job, then) = _probes[i];
            if (!job.IsDone)
                continue;
            _probes.RemoveAt(i);
            if (job.Task.IsCompletedSuccessfully)
            {
                _probed[path] = job.Task.Result;
                foreach (var action in then)
                    Guard("Could not use " + Path.GetFileName(path), () => action(job.Task.Result));
            }
            else if (job.Task.Exception?.GetBaseException() is { } error)
            {
                _workspace.Commands.ReportError($"Could not read {Path.GetFileName(path)}", error);
            }

            job.Dispose();
        }

        if (_import is { IsDone: true } import)
        {
            _import = null;
            var then = _imported;
            _imported = null;
            if (import.Task.IsCompletedSuccessfully)
            {
                _workspace.VideoPanel.RefreshPool();
                var files = import.Task.Result; // a block local: a lambda over `import` would allocate on every call
                Log.Info($"[Video] Imported {files.Count} file(s).");
                if (then is not null)
                    Guard("Could not place the imported media", () => then(files));
            }
            else if (import.Task.Exception?.GetBaseException() is { } error)
            {
                _workspace.Commands.ReportError("Import failed", error);
            }

            import.Dispose();
        }

        var tabs = _workspace.Session.Tabs;
        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i] is not TimelineTab tab)
                continue;
            if (tab.ExportJob is { IsDone: true } export)
                FinishExport(tab, export);
            Tools.Poll(tab);
        }
    }

    private void FinishExport(TimelineTab tab, VideoJob<string> job)
    {
        tab.ExportJob = null;
        var task = job.Task;
        if (task.IsCompletedSuccessfully)
            ShowExported(tab, task.Result);
        else if (task.IsCanceled || task.Exception?.GetBaseException() is OperationCanceledException)
            tab.Notice = "Export cancelled.";
        else if (task.Exception?.GetBaseException() is { } error)
            _workspace.Commands.ReportError("Export failed", error);
        job.Dispose();
    }

    private void ShowExported(TimelineTab tab, string path)
    {
        var name = Path.GetFileName(path);
        Log.Info($"[Video] Exported {path}");
        tab.Notice = $"Exported {name}.";
        var reveal = _workspace.Options.Reveal ?? OsShell.Reveal;
        _workspace.Message.Show(new MessageRequest
        {
            Title = "Export finished",
            Message = $"{name}\n{Path.GetDirectoryName(path)}",
            Buttons = ["Show in Folder", "Close"],
            DefaultButton = 0,
            CancelButton = 1,
            Icon = "check",
            Callback = (button, _) =>
            {
                if (button == 0)
                    reveal(path);
            },
        });
    }

    // Stores the exporter's progress as the job's 0…1, without Progress<T>'s synchronisation context.
    private sealed class ProgressSink(IProgress<double> inner) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => inner.Report(value.Fraction);
    }

    protected override void OnExitTree()
    {
        var running = new List<Task>();
        foreach (var tab in _workspace.Session.Tabs)
            if (tab is TimelineTab { ExportJob: { } job })
            {
                job.Cancel();
                running.Add(job.Task);
            }

        if (running.Count > 0 && Task.WaitAny([Task.WhenAll(running)], TimeSpan.FromSeconds(2)) < 0)
            Log.Warning("[Video] An export did not stop within 2 s of closing the editor; its scratch folder is removed anyway.");
        _import?.Cancel();
        _temp?.Dispose();
        _temp = null;
        _cache?.Dispose();
        _cache = null;
        base.OnExitTree();
    }
}
