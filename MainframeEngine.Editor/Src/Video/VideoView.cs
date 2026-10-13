using System.Numerics;
using MainframeEngine.Editor.Music;
using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Models;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// The video tab's per-frame driver, a <c>[Tool]</c> node of the workspace beside <see cref="SongView"/>: it sizes the
/// active tab's timeline viewport to the <see cref="VideoPanel"/>'s timeline area, keeps the preview viewport rendering,
/// publishes both to the UI as <c>engine://video-timeline</c> and <c>engine://video-preview</c>, routes the pointer over
/// the timeline and the tab's keys to its <see cref="VideoTimelineController"/>, keeps the toolbar current and applies
/// finished detection jobs on the main thread. Steady-state frames allocate nothing.
/// </summary>
[Tool]
public sealed class VideoView : Node
{
    public const string TimelineTexture = "video-timeline";
    public const string PreviewTexture = "video-preview";

    /// <summary>An off-canvas point: hovering there clears the hovered segment and boundary.</summary>
    private static readonly Vector2 Away = new(-1, -1);

    private readonly EditorWorkspace _workspace;
    private VideoTab? _tab;
    private SubViewport? _timelineRegistered;
    private SubViewport? _previewRegistered;
    private Vector2 _mouse;

    // A left press on the timeline started a gesture: every motion and the release go to the controller until then,
    // wherever the pointer is.
    private bool _dragging;

    // The pointer was over the timeline at the last motion; leaving clears the hover so Delete cannot hit a region the
    // pointer has left.
    private bool _hovering;

    public VideoView(EditorWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <summary>The video tab shown (the active tab in video mode), or null.</summary>
    public VideoTab? Tab => _tab;

    /// <summary>True while either texture is published to the UI (tests: a switch away unpublishes both).</summary>
    internal bool PublishesTextures => _timelineRegistered is not null || _previewRegistered is not null;

    // Video Settings (Task 15) will supply these; until then Cutback's defaults.
    private static DetectionSettings Detection => DetectionSettings.Default;

    protected override void OnProcess(in GameTime gameTime)
    {
        var tabs = _workspace.Session.Tabs;
        for (var i = 0; i < tabs.Count; i++)
            if (tabs[i] is VideoTab video)
                PollJobs(video);

        var tab = _workspace.Mode == EditorMode.Video ? _workspace.Session.ActiveTab as VideoTab : null;
        if (!ReferenceEquals(tab, _tab))
            Switch(tab);
        var panel = _workspace.VideoPanel;
        panel.Tick(tab);
        _workspace.Toolbar.UpdateVideo(tab);
        if (tab is null)
            return;

        var scale = _workspace.Host.PixelScale;
        var timeline = panel.TimelineRect;
        var preview = panel.PreviewRect;
        var showTimeline = timeline.Width > 1 && timeline.Height > 1;
        var showPreview = preview.Width > 1 && preview.Height > 1;
        if (showTimeline)
            tab.View.Width = timeline.Width;
        SongView.Place(tab.TimelineViewport, tab.Timeline, timeline, scale, showTimeline);
        // The preview sizes its own viewport (the decode size); it only has to keep rendering while it is shown.
        var mode = showPreview ? SubViewportUpdateMode.Always : SubViewportUpdateMode.Disabled;
        if (tab.Preview.Viewport.UpdateMode != mode)
            tab.Preview.Viewport.UpdateMode = mode;
        Publish(tab, showTimeline, showPreview);
    }

    private void Publish(VideoTab tab, bool timeline, bool preview)
    {
        if (Tree?.Servers.Get<UiServer>() is not { } ui)
            return;
        if (timeline && !ReferenceEquals(_timelineRegistered, tab.TimelineViewport))
        {
            ui.RegisterTexture(TimelineTexture, tab.TimelineViewport);
            _timelineRegistered = tab.TimelineViewport;
        }

        if (preview && !ReferenceEquals(_previewRegistered, tab.Preview.Viewport))
        {
            ui.RegisterTexture(PreviewTexture, tab.Preview.Viewport);
            _previewRegistered = tab.Preview.Viewport;
        }

        _workspace.VideoPanel.SetImages(_previewRegistered is not null && preview, _timelineRegistered is not null && timeline);
    }

    // The shown tab changed (another tab, a mode switch, a close): the old one's gesture ends, the UI stops sampling its
    // targets, and the new one's segment menu opens here.
    private void Switch(VideoTab? tab)
    {
        if (_dragging)
            _tab?.Controller.CancelGesture();
        _dragging = false;
        _hovering = false;
        if (_tab is { } old)
        {
            old.Controller.ShowSegmentMenu = null;
            old.Controller.Hover(Away); // no stale hovered region survives the switch (Delete would hit it on return)
        }

        _tab = tab;
        if (_timelineRegistered is not null || _previewRegistered is not null)
        {
            var ui = Tree?.Servers.Get<UiServer>();
            ui?.UnregisterTexture(TimelineTexture);
            ui?.UnregisterTexture(PreviewTexture);
            _timelineRegistered = null;
            _previewRegistered = null;
        }

        _workspace.VideoPanel.SetImages(false, false);
        if (tab is null)
            return;
        tab.Controller.ShowSegmentMenu = (_, x, y) => ShowSegmentMenu(tab, x, y);
    }

    // Built only when a right-click opens it.
    private void ShowSegmentMenu(VideoTab tab, float x, float y)
    {
        var rect = _workspace.VideoPanel.TimelineRect;
        _workspace.Popup.Show([new MenuItem("Delete", "edit.delete", "Del", Icon: "trash")], rect.X + x, rect.Y + y, onCommand: id =>
        {
            if (id == "edit.delete")
                Guard("Delete failed", () => tab.Controller.DeleteHovered());
        });
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────────────────────────

    protected override void OnInput(InputEvent inputEvent)
    {
        if (_tab is not { } tab || _workspace.IsDialogOpen || _workspace.Popup.Visible)
            return;
        var scale = _workspace.Host.PointerScale;
        switch (inputEvent)
        {
            case InputEventMouseButton { Button: MouseButton.Left } button:
                _mouse = button.Position * scale;
                if (button.Pressed)
                {
                    if (!InTimeline(_mouse))
                        return;
                    _dragging = true;
                    Guard("Timeline edit failed", () => tab.Controller.Press(Local(_mouse), _workspace.Modifiers));
                    Handled();
                }
                else if (_dragging)
                {
                    _dragging = false;
                    Guard("Timeline edit failed", () => tab.Controller.Release(Local(_mouse)));
                    _hovering = InTimeline(_mouse);
                    if (!_hovering)
                        tab.Controller.Hover(Away);
                    Handled();
                }

                break;
            case InputEventMouseButton { Button: MouseButton.Right, Pressed: true } right:
                _mouse = right.Position * scale;
                if (_dragging || !InTimeline(_mouse))
                    return;
                Guard("Timeline edit failed", () => tab.Controller.RightClick(Local(_mouse)));
                Handled();
                break;
            case InputEventMouseMotion motion:
                _mouse = motion.Position * scale;
                if (_dragging)
                {
                    Guard("Timeline edit failed", () => tab.Controller.Drag(Local(_mouse), _workspace.Modifiers));
                    Handled();
                }
                else if (InTimeline(_mouse))
                {
                    _hovering = true;
                    tab.Controller.Hover(Local(_mouse));
                }
                else if (_hovering)
                {
                    _hovering = false;
                    tab.Controller.Hover(Away);
                }

                break;
            case InputEventMouseWheel wheel:
                if (!InTimeline(_mouse))
                    return;
                tab.Controller.Wheel(Local(_mouse), wheel.Delta, _workspace.Modifiers);
                Handled();
                break;
        }
    }

    /// <summary>
    /// The video tab's keys: Space plays and pauses, Home goes to the start, and the controller takes the rest
    /// (Delete/Backspace). False when no video tab is shown or the key is not one of them, so its shortcut runs instead.
    /// </summary>
    public bool HandleKey(Key key, EditorModifiers modifiers)
    {
        if (_workspace.Mode != EditorMode.Video || _workspace.Session.ActiveTab is not VideoTab tab)
            return false;
        if (modifiers == EditorModifiers.None)
        {
            if (key == Key.Space)
            {
                Guard("Playback failed", tab.TogglePlay);
                return true;
            }

            if (key == Key.Home)
            {
                Guard("Playback failed", () => tab.Seek(0));
                return true;
            }
        }

        var handled = false;
        Guard("Timeline edit failed", () => handled = tab.Controller.Key(key, modifiers));
        return handled;
    }

    private bool InTimeline(Vector2 p) => _workspace.VideoPanel.TimelineRect.Contains(p.X, p.Y);

    private Vector2 Local(Vector2 p)
    {
        var rect = _workspace.VideoPanel.TimelineRect;
        return new Vector2(p.X - rect.X, p.Y - rect.Y);
    }

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

    // ── Detection ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Detect Silence: runs ffmpeg's <c>silencedetect</c> over the recording in the background; <see cref="PollJobs"/> lays
    /// out the cuts when it finishes. A second call while it runs cancels it.
    /// </summary>
    public void DetectSilence(VideoTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.SilenceJob is { } running)
        {
            running.Cancel();
            return;
        }

        if (tab.Ffmpeg is not { } ffmpeg)
        {
            _workspace.Commands.ReportError("ffmpeg not found",
                new FfmpegMissingException("Detect Silence needs ffmpeg. Set it in Editor Settings › Video, or install it and restart the editor."));
            return;
        }

        var document = tab.Document;
        var settings = Detection;
        tab.SilenceSettings = settings;
        tab.SilenceJob = VideoJob.Start("silence",
            (p, ct) => new SilenceDetector(ffmpeg).DetectAsync(document.SourcePath, document.Duration, settings, p, ct));
        Log.Info($"[Video] Detecting silence in {Path.GetFileName(document.SourcePath)}…");
    }

    /// <summary>Main thread, every frame: applies <paramref name="tab"/>'s finished silence detection, or reports its end.</summary>
    internal void PollJobs(VideoTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.SilenceJob is not { IsDone: true } job)
            return;
        tab.SilenceJob = null;
        var settings = tab.SilenceSettings ?? Detection; // the ones it detected with
        tab.SilenceSettings = null;
        var task = job.Task;
        if (task.IsCanceled || job.IsCancelled || task.Exception?.GetBaseException() is OperationCanceledException)
            tab.Notice = "Silence detection cancelled.";
        else if (task.Exception?.GetBaseException() is { } error)
            _workspace.Commands.ReportError("Detect Silence failed", error);
        else
            ApplySilence(tab, task.Result, settings);
        job.Dispose();
    }

    // Kept out of PollJobs: a lambda capturing its parameter would allocate a closure on every call, every frame.
    private void ApplySilence(VideoTab tab, IReadOnlyList<SilenceSpan> spans, DetectionSettings settings) =>
        Guard("Detect Silence failed", () =>
        {
            tab.Controller.CancelGesture();
            tab.Notice = tab.Document.ApplySilence(spans, settings);
        });
}
