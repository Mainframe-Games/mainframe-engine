using System.Globalization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The menu bar: a click opens the menu's <see cref="PopupMenu"/>. Game mode shows File, Edit, View, Project, Run and Help;
/// video mode File, Edit, View, Video and Help (<see cref="ApplyMode"/>).
/// </summary>
public sealed class MenuBarPanel : EditorDocument
{
    public MenuBarPanel(EditorWorkspace workspace)
        : base(workspace, "menubar.rml")
    {
    }

    private static readonly string[] MenuIds = ["menu-file", "menu-edit", "menu-view", "menu-project", "menu-run", "menu-video", "menu-help"];

    /// <summary>Opens menu <paramref name="menu"/> (file, edit, view, project, run, video, help) under its title.</summary>
    public void OpenMenu(string menu)
    {
        var x = 80f;
        if (EnsureLoaded() && Document.GetElementById($"menu-{menu}") is { IsNull: false } title)
        {
            title.SetClass("open", true);
            x = title.Bounds.X / MathF.Max(0.01f, Workspace.Host.PixelScale);
        }

        Workspace.Popup.Show(Workspace.Commands.MenuItems(menu), x, EditorLayout.MenuBarHeight, onClose: () => CloseMenus());
    }

    private void CloseMenus()
    {
        if (!IsLoaded)
            return;
        foreach (var id in MenuIds)
            Document.GetElementById(id).SetClass("open", false);
    }

    /// <summary>Shows the menu titles of <paramref name="mode"/> (Project and Run are game-only, Video is video-only).</summary>
    public void ApplyMode(EditorMode mode)
    {
        if (!IsLoaded)
            return;
        var video = mode == EditorMode.Video;
        Document.GetElementById("menu-project").SetClass("hidden", video);
        Document.GetElementById("menu-run").SetClass("hidden", video);
        Document.GetElementById("menu-video").SetClass("hidden", !video);
    }

    private string _windowTitle = "";
    private bool _dirty;

    /// <summary>The scene name shown on the right of the bar (scene icon, name, unsaved marker).</summary>
    public string WindowTitle => _windowTitle;

    /// <summary>Whether the bar shows the unsaved marker.</summary>
    public bool ShowsUnsaved => _dirty;

    /// <summary>The scene name shown on the right of the bar, with an unsaved marker when <paramref name="dirty"/>.</summary>
    public void SetWindowTitle(string text, bool dirty)
    {
        _windowTitle = text ?? "";
        _dirty = dirty;
        ApplyTitle();
    }

    private void ApplyTitle()
    {
        SetText("title-text", _windowTitle);
        if (!IsLoaded)
            return;
        Document.GetElementById("title-icon").SetClass("hidden", _windowTitle.Length == 0);
        Document.GetElementById("title-dirty").SetClass("hidden", !_dirty);
    }

    protected override void OnAttach(RmlDocument document)
    {
        ApplyTitle();
        ApplyMode(Workspace.Mode);
    }

    protected override void OnClickElement(RmlEvent e)
    {
        if (FindAttribute(e.Target, "data-menu") is { } menu)
            OpenMenu(menu);
    }
}

/// <summary>
/// The toolbar: in game mode the gizmo mode, local/global, snapping, frame, grid and the play controls; in video mode the
/// video tools (<see cref="ApplyMode"/>). Both end with the Game|Video toggle, the docs button and the frame stats.
/// </summary>
public sealed class ToolbarPanel : EditorDocument
{
    private RmlEventListener? _snapListener;

    public ToolbarPanel(EditorWorkspace workspace)
        : base(workspace, "toolbar.rml")
    {
    }

    private bool? _shownLocal;

    protected override void OnAttach(RmlDocument document)
    {
        _shownLocal = null;
        _badgeVersion = null;
        ResetVideo();
        _snapListener?.Remove();
        _snapListener = document.GetElementById("snap-step").AddEventListener("change", OnSnapStep);
        Refresh();
        RefreshUpdate();
        ApplyMode(Workspace.Mode);
    }

    /// <summary>Shows <paramref name="mode"/>'s tools and marks its half of the Game|Video toggle.</summary>
    public void ApplyMode(EditorMode mode)
    {
        if (!IsLoaded)
            return;
        var video = mode == EditorMode.Video;
        var document = Document;
        document.GetElementById("game-tools").SetClass("hidden", video);
        document.GetElementById("video-tools").SetClass("hidden", !video);
        document.GetElementById("mode-game").SetClass("active", !video);
        document.GetElementById("mode-video").SetClass("active", video);
    }

    private ReleaseVersion? _badgeVersion;

    /// <summary>True while the update badge is shown (a newer release is available).</summary>
    public bool UpdateBadgeVisible { get; private set; }

    /// <summary>Shows or hides the update badge (on <see cref="UpdateController.Changed"/>, never per frame).</summary>
    public void RefreshUpdate()
    {
        var release = Workspace.Updates?.Available?.Release;
        UpdateBadgeVisible = release is not null;
        if (!IsLoaded)
            return;
        var badge = Document.GetElementById("update");
        badge.SetClass("shown", UpdateBadgeVisible);
        if (release is not null && _badgeVersion != release.Version)
        {
            _badgeVersion = release.Version;
            badge.SetAttribute("data-tooltip", $"Mainframe Engine v{release.Version} is available — click for the release notes and Update and restart");
        }
    }

    private void OnSnapStep(RmlEvent e)
    {
        if (ValueText.TryParseFloat(e.Value, out var step) && step > 0)
            Workspace.Gizmo.Snap = Workspace.Gizmo.Snap with { Translate = step };
    }

    /// <summary>Re-applies the active states (mode, space, snap, grid).</summary>
    public void Refresh()
    {
        if (!IsLoaded)
            return;
        var gizmo = Workspace.Gizmo;
        var document = Document;
        document.GetElementById("tool-select").SetClass("active", gizmo.Mode == GizmoMode.Select);
        document.GetElementById("tool-translate").SetClass("active", gizmo.Mode == GizmoMode.Translate);
        document.GetElementById("tool-rotate").SetClass("active", gizmo.Mode == GizmoMode.Rotate);
        document.GetElementById("tool-scale").SetClass("active", gizmo.Mode == GizmoMode.Scale);
        var local = document.GetElementById("tool-local");
        local.SetClass("active", gizmo.Local);
        if (_shownLocal != gizmo.Local)
        {
            _shownLocal = gizmo.Local;
            local.SetAttribute("data-tooltip", gizmo.Local
                ? "Local Space (T) — handles follow the node's own axes; click for the world axes"
                : "Global Space (T) — handles follow the world axes; click for the node's local axes");
            var icon = document.GetElementById("tool-local-icon");
            icon.SetClass("icon-world", !gizmo.Local);
            icon.SetClass("icon-cube", gizmo.Local);
        }
        document.GetElementById("tool-snap").SetClass("active", gizmo.Snap.Enabled);
        document.GetElementById("tool-grid").SetClass("active", Workspace.Viewport?.GridVisible ?? true);
        document.GetElementById("tool-post").SetClass("active", Workspace.Viewport?.PostPreview ?? true);
        RefreshPlay();
    }

    private const string RecordPlayTip = "Play and Record (F10) — record gameplay for promo footage; press again to stop";
    private const string RecordStartTip = "Start Recording (F10) — record the running game from now on; press again to stop";
    private const string RecordStopTip = "Stop Recording (F10)";
    private const string RecordSavingTip = PlayController.SavingRecordingText;
    private string _instancesRml = "";
    private string _playStatus = "";
    private string _recordTip = RecordPlayTip;
    private bool _recDot;

    /// <summary>
    /// The <see cref="PlayController.RecordStatus"/> of the first recording instance, else of the first saving one; empty
    /// when none is. <paramref name="recording"/>: it is a running recording (the red dot shows).
    /// </summary>
    private static string RecordingStatus(PlayController play, out bool recording)
    {
        PlayInstance? saving = null;
        foreach (var instance in play.Service.Instances)
        {
            if (instance.Recording == PlayRecordingState.Recording)
            {
                recording = true;
                return PlayController.RecordStatus(instance.Recording, instance.RecordingStartedUtc, DateTime.UtcNow);
            }

            if (instance.Recording == PlayRecordingState.Saving)
                saving ??= instance;
        }

        recording = false;
        return saving is null ? "" : PlayController.RecordStatus(saving.Recording, saving.RecordingStartedUtc, DateTime.UtcNow);
    }

    /// <summary>Re-applies the play controls' states and the running instances (on play/project changes, never per frame).</summary>
    public void RefreshPlay()
    {
        if (!IsLoaded)
            return;
        var document = Document;
        var play = Workspace.Play;
        var project = Workspace.Project;
        var canPlay = project.DesktopProject is not null && !play.IsBuilding;
        document.GetElementById("play").SetClass("disabled", !canPlay);
        document.GetElementById("play-scene").SetClass("disabled", !canPlay || Workspace.Session.Active is null);
        var record = document.GetElementById("record");
        record.SetClass("disabled", !play.CanToggleRecording);
        record.SetClass("recording", play.IsRecording); // red only while frames are being recorded, not while saving
        var recordTip = play.IsRecording ? RecordStopTip : play.IsSavingRecording ? RecordSavingTip : play.IsPlaying ? RecordStartTip : RecordPlayTip;
        if (!string.Equals(recordTip, _recordTip, StringComparison.Ordinal))
        {
            _recordTip = recordTip;
            record.SetAttribute("data-tooltip", recordTip);
        }

        document.GetElementById("pause").SetClass("disabled", !play.IsPlaying);
        document.GetElementById("pause").SetClass("active", play.IsPaused);
        document.GetElementById("stop").SetClass("disabled", !play.IsPlaying);
        var build = document.GetElementById("build-reload");
        build.SetClass("disabled", project.GameLibraryProject is null);
        build.SetClass("attention", project.NeedsRebuild && !project.IsBuilding);
        build.SetClass("busy", play.IsBuilding);
        document.GetElementById("user-data").SetClass("disabled", Workspace.Session.Project is null);

        var rml = new System.Text.StringBuilder();
        foreach (var instance in play.Service.Instances)
        {
            var (css, icon) = instance.State switch
            {
                PlayInstanceState.Launching => ("launching", "loader-2"),
                PlayInstanceState.Running => ("running", "player-play"),
                PlayInstanceState.Paused => ("paused", "player-pause"),
                PlayInstanceState.Stopping => ("stopping", "player-stop"),
                PlayInstanceState.Crashed => ("crashed", "alert-octagon"),
                _ => ("exited", "check"),
            };
            rml.Append("<div class=\"instance ").Append(css).Append("\" data-instance=\"").Append(instance.Number)
                .Append("\" data-tooltip=\"").Append(RmlText.Escape($"{instance.Label} #{instance.Number} — {instance.State}. Click to pause, reload its scene or stop it."))
                .Append("\"><span class=\"icon icon-sm icon-").Append(icon).Append("\"></span><span>").Append(RmlText.Escape(instance.Label)).Append("</span></div>");
        }

        var instances = rml.ToString();
        if (!string.Equals(instances, _instancesRml, StringComparison.Ordinal))
        {
            _instancesRml = instances;
            document.GetElementById("instances").SetInnerRml(instances);
        }

        var building = play.IsBuilding || project.IsBuilding;
        var recordingStatus = RecordingStatus(play, out var recordingNow);
        var status = building ? "Building…" : recordingStatus;
        var recDot = recordingNow && !building;
        if (recDot != _recDot)
        {
            _recDot = recDot;
            document.GetElementById("rec-dot").SetClass("on", recDot);
        }

        if (!string.Equals(status, _playStatus, StringComparison.Ordinal))
        {
            _playStatus = status;
            SetText("play-status", status);
        }
    }

    protected override void OnClickElement(RmlEvent e)
    {
        if (FindAttribute(e.Target, "data-instance") is not { } text ||
            !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ||
            Workspace.Play.Service.Instances.FirstOrDefault(i => i.Number == number) is not { } instance)
            return;
        var element = FindWithAttribute(e.Target, "data-instance");
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        ShowInstanceMenu(instance, element.Bounds.X / scale, (element.Bounds.Y + element.Bounds.Height) / scale);
    }

    /// <summary>The per-instance menu: pause/resume, reload its scene, stop, clear finished instances.</summary>
    public void ShowInstanceMenu(PlayInstance instance, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var service = Workspace.Play.Service;
        var paused = service.IsPaused(instance);
        Workspace.Popup.Show(
        [
            MenuItem.Header($"{instance.Label} #{instance.Number} · {instance.State}"),
            new MenuItem(paused ? "Resume" : "Pause", "pause", null, instance.IsAlive && instance.HasConnected, Icon: paused ? "player-play" : "player-pause"),
            new MenuItem("Reload Scene", "reload", null, instance.IsAlive && instance.HasConnected, Icon: "refresh"),
            new MenuItem("Stop", "stop", null, instance.IsAlive, Icon: "player-stop"),
            MenuItem.Separator,
            new MenuItem("Clear Finished", "clear", null, service.Instances.Any(i => !i.IsAlive), Icon: "trash-x"),
        ], x, y, command =>
        {
            switch (command)
            {
                case "pause" when paused:
                    service.Resume(instance);
                    break;
                case "pause":
                    service.Pause(instance);
                    break;
                case "reload":
                    Workspace.Commands.SaveAll();
                    service.ReloadScene(instance);
                    break;
                case "stop":
                    service.Stop(instance);
                    break;
                case "clear":
                    service.ClearExited();
                    break;
            }

            RefreshPlay();
        });
    }

    // ── Video mode ───────────────────────────────────────────────────────────────────────────────────────────────

    private const string IdleTip =
        "Detect Idle Gameplay — cut loading, pauses and stretches without input, from the recording's Mainframe Engine data";
    private const string IdleNeedsSidecarTip = "Needs a Mainframe Engine recording (its .mfcap.json sidecar)";
    private const string PlayTip = "Play (Space) — the kept footage from the playhead, skipping the cuts";
    private const string PauseTip = "Pause (Space)";

    // The buttons that need a video tab; Detect Idle also needs a sidecar and is handled on its own.
    private static readonly string[] VideoTabButtons = ["video-play", "video-stop", "video-transcribe", "video-fillers", "video-silence", "video-export"];

    // What the video tools show, so UpdateVideo writes only what changed (null / -2: not written since the last load).
    private bool? _videoEnabled;
    private int _videoIdle = -1;
    private bool? _videoPlaying;
    private long _videoPosition = -2;
    private long _videoDuration = -2;
    private string? _videoKept;
    private int _videoTranscribe = -2;
    private int _videoSilence = -2;
    private int _videoExport = -2;

    private void ResetVideo()
    {
        _videoEnabled = null;
        _videoIdle = -1;
        _videoPlaying = null;
        _videoPosition = -2;
        _videoDuration = -2;
        _videoKept = null;
        _videoTranscribe = -2;
        _videoSilence = -2;
        _videoExport = -2;
    }

    /// <summary>
    /// Every frame (from the <see cref="Video.VideoView"/>): the video tools' disabled states, the play/pause icon, the
    /// position (<c>1:02.3 / 6:00.0</c>), "Cancel n %" on a running job's button, and the kept time. Only what changed is
    /// written; the position is formatted on the stack, so playing allocates nothing either.
    /// </summary>
    public void UpdateVideo(Video.VideoTab? tab)
    {
        if (!IsLoaded)
            return;
        var document = Document;
        var enabled = tab is not null;
        if (_videoEnabled != enabled)
        {
            _videoEnabled = enabled;
            foreach (var id in VideoTabButtons)
                document.GetElementById(id).SetClass("disabled", !enabled);
        }

        // 0: no tab, 1: a recording without a sidecar (the tooltip says why it is disabled), 2: Detect Idle can run.
        var idle = tab is null ? 0 : tab.Document.Capture is null ? 1 : 2;
        if (_videoIdle != idle)
        {
            _videoIdle = idle;
            var button = document.GetElementById("video-idle");
            button.SetClass("disabled", idle != 2);
            button.SetAttribute("data-tooltip", idle == 1 ? IdleNeedsSidecarTip : IdleTip);
        }

        var playing = tab?.Preview.IsPlaying == true;
        if (_videoPlaying != playing)
        {
            _videoPlaying = playing;
            var icon = document.GetElementById("video-play-icon");
            icon.SetClass("icon-player-play", !playing);
            icon.SetClass("icon-player-pause", playing);
            document.GetElementById("video-play").SetAttribute("data-tooltip", playing ? PauseTip : PlayTip);
        }

        UpdateVideoPosition(tab);
        var kept = Workspace.VideoPanel.Kept;
        if (!ReferenceEquals(kept, _videoKept))
        {
            _videoKept = kept;
            SetText("video-kept", kept);
        }

        UpdateJobButton("video-transcribe", "Transcribe", tab is null ? -1 : TranscribePercent(tab), ref _videoTranscribe);
        UpdateJobButton("video-silence", "Detect silence", tab is null ? -1 : Percent(tab.SilenceJob), ref _videoSilence);
        UpdateJobButton("video-export", "Export", tab is null ? -1 : Percent(tab.ExportJob), ref _videoExport);
    }

    private void UpdateVideoPosition(Video.VideoTab? tab)
    {
        var position = tab is null ? -1 : (long)Math.Round(Math.Max(0, tab.State.Playhead) * 10, MidpointRounding.AwayFromZero);
        var duration = tab is null ? -1 : (long)Math.Round(tab.Document.Duration * 10, MidpointRounding.AwayFromZero);
        if (position == _videoPosition && duration == _videoDuration)
            return;
        _videoPosition = position;
        _videoDuration = duration;
        var element = Document.GetElementById("video-pos");
        if (element.IsNull)
            return;
        element.SetClass("empty", tab is null);
        if (tab is null)
        {
            element.SetInnerRml("");
            return;
        }

        Span<char> text = stackalloc char[40];
        var n = Video.VideoFormat.FormatClock(text, tab.State.Playhead);
        " / ".CopyTo(text[n..]);
        n += 3;
        n += Video.VideoFormat.FormatClock(text[n..], tab.Document.Duration);
        element.SetInnerRml(text[..n]);
    }

    // A running job's progress in whole percent, or -1 when there is none.
    private static int Percent<T>(Video.VideoJob<T>? job) => job is null || job.IsDone ? -1 : (int)(job.Progress * 100);

    // Transcribing is two phases when the model has to be fetched first: the job's first half (progress under 0.5) is the
    // download, which the button names. Encoded as DownloadingOffset + n so the cached value changes with the phase too.
    private const int DownloadingOffset = 1000;

    private static int TranscribePercent(Video.VideoTab tab)
    {
        if (tab.TranscribeJob is not { IsDone: false } job)
            return -1;
        var progress = job.Progress;
        return tab.TranscribeRun is { Downloads: true } && progress < 0.5
            ? DownloadingOffset + (int)(progress * 200) // the download's own 0–100 %
            : (int)(progress * 100);
    }

    // A job button reads its label, or "Cancel n %" while its job runs (pressing it then cancels); a download phase
    // (DownloadingOffset + n) reads "Downloading model n %".
    private void UpdateJobButton(string id, string label, int percent, ref int shown)
    {
        if (percent == shown)
            return;
        shown = percent;
        var button = Document.GetElementById(id);
        if (button.IsNull)
            return;
        button.SetClass("busy", percent >= 0);
        button.SetInnerRml(percent switch
        {
            < 0 => label,
            >= DownloadingOffset => string.Create(CultureInfo.InvariantCulture, $"Downloading model {percent - DownloadingOffset} %"),
            _ => string.Create(CultureInfo.InvariantCulture, $"Cancel {percent} %"),
        });
    }

    /// <summary>Writes the frame stats without allocating (stack-formatted span into the element).</summary>
    public void UpdateStats(float fps, float ms)
    {
        if (!IsLoaded || !Workspace.Options.ShowFrameStats)
            return;
        var element = Document.GetElementById("stats");
        if (element.IsNull)
            return;
        Span<char> text = stackalloc char[48];
        // "60 fps  16.6 ms". Integers only (custom float format strings allocate in the runtime's number formatting),
        // written with int.TryFormat rather than an interpolated handler: the handler's generic AppendFormatted<int>
        // boxes each value while it still runs as tier-0 code, which a busy process (parallel tests, the editor's
        // start-up) can keep it at for a long time.
        var tenths = Math.Max(0, (int)MathF.Round(ms * 10f));
        var length = 0;
        if (Append(text, ref length, (int)MathF.Round(fps)) && Append(text, ref length, " fps  ") &&
            Append(text, ref length, tenths / 10) && Append(text, ref length, ".") && Append(text, ref length, tenths % 10) &&
            Append(text, ref length, " ms"))
            element.SetInnerRml(text[..length]);
    }

    private static bool Append(Span<char> text, ref int length, int value)
    {
        if (!value.TryFormat(text[length..], out var written, default, CultureInfo.InvariantCulture))
            return false;
        length += written;
        return true;
    }

    private static bool Append(Span<char> text, ref int length, ReadOnlySpan<char> value)
    {
        if (!value.TryCopyTo(text[length..]))
            return false;
        length += value.Length;
        return true;
    }
}

/// <summary>The four splitter handles (full-window overlay, mouse only over the handles; RCSS <c>drag: drag</c>).</summary>
public sealed class SplittersOverlay : EditorDocument
{
    private static readonly (string Id, Splitter Splitter)[] Handles =
    [
        ("split-left", Splitter.Left),
        ("split-right", Splitter.Right),
        ("split-bottom", Splitter.Bottom),
        ("split-leftdock", Splitter.LeftDock),
    ];

    public SplittersOverlay(EditorWorkspace workspace)
        : base(workspace, "splitters.rml")
    {
    }

    protected override void OnAttach(RmlDocument document)
    {
        foreach (var (id, splitter) in Handles)
        {
            var element = document.GetElementById(id);
            if (element.IsNull)
                continue;
            element.AddEventListener("drag", e => OnDrag(splitter, e));
            element.AddEventListener("dragstart", e => e.CurrentElement.SetClass("dragging", true));
            element.AddEventListener("dragend", e =>
            {
                e.CurrentElement.SetClass("dragging", false);
                Workspace.SaveLayout();
            });
        }

        Apply(Workspace.Layout);
    }

    private void OnDrag(Splitter splitter, RmlEvent e)
    {
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var x = e.GetParameter("mouse_x", 0.0) / scale;
        var y = e.GetParameter("mouse_y", 0.0) / scale;
        if (Workspace.Layout.DragSplitter(splitter, (float)x, (float)y))
            Workspace.ApplyLayout();
    }

    /// <summary>Positions the handles from <paramref name="layout"/>; a splitter the mode lacks (empty rectangle) is hidden.</summary>
    public void Apply(EditorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!IsLoaded)
            return;
        foreach (var (id, splitter) in Handles)
        {
            var element = Document.GetElementById(id);
            if (element.IsNull)
                continue;
            var rect = layout.SplitterRect(splitter);
            element.SetProperty("display", rect.Width <= 0 || rect.Height <= 0 ? "none" : "block");
            element.SetProperty("left", RmlText.Dp(rect.X));
            element.SetProperty("top", RmlText.Dp(rect.Y));
            element.SetProperty("width", RmlText.Dp(rect.Width));
            element.SetProperty("height", RmlText.Dp(rect.Height));
        }
    }
}
