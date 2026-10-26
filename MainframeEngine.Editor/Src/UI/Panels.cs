using System.Globalization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The menu bar: a click opens the menu's <see cref="PopupMenu"/>. Game mode shows File, Edit, View, Project, Run and Help;
/// video mode File, Edit, View, Video and Help; music mode File, Edit, View, Song and Help (<see cref="ApplyMode"/>).
/// </summary>
public sealed class MenuBarPanel : EditorDocument
{
    public MenuBarPanel(EditorWorkspace workspace)
        : base(workspace, "menubar.rml")
    {
    }

    private static readonly string[] MenuIds = ["menu-file", "menu-edit", "menu-view", "menu-project", "menu-run", "menu-video", "menu-song", "menu-help"];

    /// <summary>Opens menu <paramref name="menu"/> (file, edit, view, project, run, video, song, help) under its title.</summary>
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

    /// <summary>Shows the menu titles of <paramref name="mode"/> (Project and Run are game-only, Video video-only, Song music-only).</summary>
    public void ApplyMode(EditorMode mode)
    {
        if (!IsLoaded)
            return;
        var game = mode == EditorMode.Game;
        Document.GetElementById("menu-project").SetClass("hidden", !game);
        Document.GetElementById("menu-run").SetClass("hidden", !game);
        Document.GetElementById("menu-video").SetClass("hidden", mode != EditorMode.Video);
        Document.GetElementById("menu-song").SetClass("hidden", mode != EditorMode.Music);
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
/// video tools; in music mode the song transport (<see cref="ApplyMode"/>). Each ends with the Game|Music|Video toggle,
/// the docs button and the frame stats.
/// </summary>
public sealed class ToolbarPanel : EditorDocument
{
    private RmlEventListener? _snapListener;
    private readonly RmlEventListener?[] _tempoListeners = new RmlEventListener?[3];

    public ToolbarPanel(EditorWorkspace workspace)
        : base(workspace, "toolbar.rml")
    {
    }

    private bool? _shownLocal;
    private GizmoPivot? _shownPivot;

    /// <summary>The pivot button's tooltip for <paramref name="pivot"/>.</summary>
    internal static string PivotTooltip(GizmoPivot pivot) => pivot switch
    {
        GizmoPivot.Active => "Pivot: Active Node — rotate and scale around the last selected node (click to change)",
        GizmoPivot.Individual => "Pivot: Individual Origins — each node rotates and scales around its own origin (click to change)",
        _ => "Pivot: Median Point — rotate and scale around the selection's centre (click to change)",
    };

    protected override void OnAttach(RmlDocument document)
    {
        _shownLocal = null;
        _shownPivot = null;
        _badgeVersion = null;
        _thumbSlot = -1;
        ResetVideo();
        ResetMusic();
        _snapListener?.Remove();
        _snapListener = document.GetElementById("snap-step").AddEventListener("change", OnSnapStep);
        AttachTempo(document);
        Refresh();
        RefreshUpdate();
        ApplyMode(Workspace.Mode);
    }

    /// <summary>Shows <paramref name="mode"/>'s tools and marks its button of the Game|Music|Video toggle (its thumb slides there).</summary>
    public void ApplyMode(EditorMode mode)
    {
        if (!IsLoaded)
            return;
        var document = Document;
        document.GetElementById("game-tools").SetClass("hidden", mode != EditorMode.Game);
        document.GetElementById("video-tools").SetClass("hidden", mode != EditorMode.Video);
        document.GetElementById("music-tools").SetClass("hidden", mode != EditorMode.Music);
        document.GetElementById("mode-game").SetClass("active", mode == EditorMode.Game);
        document.GetElementById("mode-video").SetClass("active", mode == EditorMode.Video);
        document.GetElementById("mode-music").SetClass("active", mode == EditorMode.Music);
        // The thumb stretches there (its RCSS transitions, picked by the direction); only the toggle animates.
        var slot = ModeSlot(mode);
        var thumb = document.GetElementById("mode-thumb");
        if (_thumbSlot >= 0 && slot != _thumbSlot)
        {
            thumb.SetClass("to-right", slot > _thumbSlot);
            thumb.SetClass("to-left", slot < _thumbSlot);
        }

        _thumbSlot = slot;
        thumb.SetClass("at-music", mode == EditorMode.Music);
        thumb.SetClass("at-video", mode == EditorMode.Video);
    }

    // The thumb's slot in the toggle, left to right (-1: not placed since the document loaded, so it jumps there).
    private int _thumbSlot = -1;

    private static int ModeSlot(EditorMode mode) => mode switch
    {
        EditorMode.Music => 1,
        EditorMode.Video => 2,
        _ => 0,
    };

    /// <summary>Where a menu opened from toolbar element <paramref name="id"/> goes: just below it (window dp).</summary>
    public (float X, float Y) Below(string id)
    {
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        if (!IsLoaded || Document.GetElementById(id) is not { IsNull: false } element)
            return (80, EditorLayout.MenuBarHeight + EditorLayout.ToolbarHeight);
        var b = element.Bounds;
        return (b.X / scale, (b.Y + b.Height) / scale + 2);
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
            badge.SetAttribute("data-tooltip", $"Mainframe Editor v{release.Version} is available — click for the release notes and Update and restart");
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
        if (_shownPivot != gizmo.Pivot)
        {
            _shownPivot = gizmo.Pivot;
            document.GetElementById("tool-pivot").SetAttribute("data-tooltip", PivotTooltip(gizmo.Pivot));
            var icon = document.GetElementById("tool-pivot-icon");
            icon.SetClass("icon-point", gizmo.Pivot == GizmoPivot.Median);
            icon.SetClass("icon-target", gizmo.Pivot == GizmoPivot.Active);
            icon.SetClass("icon-circles", gizmo.Pivot == GizmoPivot.Individual);
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

    // ── Music mode ───────────────────────────────────────────────────────────────────────────────────────────────

    // The controls that need a song tab (the tempo field is handled on its own).
    private static readonly string[] MusicTabButtons =
        ["song-rewind", "song-play", "song-stop", "song-record", "song-loop", "song-sig", "song-snap", "song-quantize", "song-render", "song-render-menu", "song-view-roll", "song-view-mixer"];

    // What the music tools show, so UpdateMusic writes only what changed (null / -2 / NaN: not written since the last load).
    private Music.SongTab? _musicTab;
    private bool? _musicEnabled;
    private bool? _musicPlaying;
    private bool? _musicRecording;
    private bool? _musicLoop;
    private bool? _musicReadOnly;
    private bool? _musicEditable;
    private bool? _musicRollShown;
    private bool? _musicMixerShown;
    private bool? _musicBrowserShown;
    private double _musicTempo = double.NaN;
    private int _musicBeats = -1;
    private int _musicUnit = -1;
    private int _musicSnap = -1;
    private int _musicQuantize = -1;
    private long _musicPosition = -2;
    private int _musicUnderruns = -2;
    private int _musicRender = -3;
    private bool _tempoFocused;

    private void ResetMusic()
    {
        _musicEnabled = null;
        _musicPlaying = null;
        _musicRecording = null;
        _musicLoop = null;
        _musicReadOnly = null;
        _musicEditable = null;
        _musicRollShown = null;
        _musicMixerShown = null;
        _musicBrowserShown = null;
        _musicTempo = double.NaN;
        _musicBeats = -1;
        _musicUnit = -1;
        _musicSnap = -1;
        _musicQuantize = -1;
        _musicPosition = -2;
        _musicUnderruns = -2;
        _musicRender = -3;
        _tempoFocused = false;
    }

    private void AttachTempo(RmlDocument document)
    {
        foreach (var listener in _tempoListeners)
            listener?.Remove();
        var tempo = document.GetElementById("song-tempo");
        _tempoListeners[0] = tempo.AddEventListener("change", OnTempoChange);
        _tempoListeners[1] = tempo.AddEventListener("focus", _ => _tempoFocused = true);
        _tempoListeners[2] = tempo.AddEventListener("blur", _ =>
        {
            _tempoFocused = false;
            _musicTab?.Document.History.EndMerge();
            _musicTempo = double.NaN; // show the tempo as stored
        });
    }

    // Typing a tempo sets it while the field has focus (one merged undo step until it loses focus).
    private void OnTempoChange(RmlEvent e)
    {
        if (!_tempoFocused || _musicTab is not { Document.IsReadOnly: false } tab ||
            !double.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm) || bpm is < 20 or > 999 ||
            Math.Abs(bpm - tab.Document.Song.Tempo) <= 1e-6)
            return;
        try
        {
            tab.Document.SetTempo(bpm, "song-tempo");
            _musicTempo = bpm;
        }
        catch (Exception ex) when (EditorCommands.IsRecoverable(ex))
        {
            Workspace.Commands.ReportError("Could not set the tempo", ex);
        }
    }

    /// <summary>
    /// Every frame in music mode (from the <see cref="Music.SongView"/>): the transport's disabled and active states, the
    /// tempo, time signature, position (<c>003.2.240</c>), snap, input quantize, underruns, the read-only badge and
    /// "Cancel n %" on Render while it runs, and which bottom pane the view toggles light. Only what changed is written;
    /// steady frames allocate nothing.
    /// </summary>
    public void UpdateMusic(Music.SongTab? tab)
    {
        if (!IsLoaded)
            return;
        var document = Document;
        SetActive(document, "song-view-browser", Workspace.Layout.Settings.MusicShowBrowser, ref _musicBrowserShown); // with or without a tab
        if (!ReferenceEquals(tab, _musicTab))
        {
            _musicTab = tab;
            var enabled = _musicEnabled;
            var focused = _tempoFocused;
            ResetMusic();
            _musicEnabled = enabled;
            _tempoFocused = focused;
        }

        var hasTab = tab is not null;
        if (_musicEnabled != hasTab)
        {
            _musicEnabled = hasTab;
            foreach (var id in MusicTabButtons)
                document.GetElementById(id).SetClass("disabled", !hasTab);
        }

        if (tab is null)
        {
            if (_musicPosition != -1)
            {
                _musicPosition = -1;
                var pos = document.GetElementById("song-pos");
                pos.SetClass("empty", true);
                pos.SetInnerRml("");
                document.GetElementById("song-tempo").SetValue("");
                document.GetElementById("song-xruns").SetInnerRml("");
                document.GetElementById("song-readonly").SetClass("hidden", true);
            }

            SetActive(document, "song-play", false, ref _musicPlaying);
            SetActive(document, "song-record", false, ref _musicRecording);
            SetActive(document, "song-loop", false, ref _musicLoop);
            SetActive(document, "song-view-roll", false, ref _musicRollShown);
            SetActive(document, "song-view-mixer", false, ref _musicMixerShown);
            SetEditable(false);
            return;
        }

        var song = tab.Document.Song;
        SetEditable(!tab.Document.IsReadOnly);
        SetActive(document, "song-play", tab.Player.IsPlaying, ref _musicPlaying);
        SetActive(document, "song-record", tab.Midi.IsRecording, ref _musicRecording);
        SetActive(document, "song-loop", song.Loop.Enabled, ref _musicLoop);
        SetActive(document, "song-view-roll", tab.Panel == Music.SongBottomPanel.PianoRoll, ref _musicRollShown);
        SetActive(document, "song-view-mixer", tab.Panel == Music.SongBottomPanel.Mixer, ref _musicMixerShown);

        if (!_tempoFocused && song.Tempo != _musicTempo)
        {
            _musicTempo = song.Tempo;
            document.GetElementById("song-tempo").SetValue(song.Tempo.ToString("0.##", CultureInfo.InvariantCulture));
        }

        var beats = song.TimeSignature.ElementAtOrDefault(0);
        var unit = song.TimeSignature.ElementAtOrDefault(1);
        if (beats != _musicBeats || unit != _musicUnit)
        {
            _musicBeats = beats;
            _musicUnit = unit;
            document.GetElementById("song-sig").SetInnerRml(string.Create(CultureInfo.InvariantCulture, $"{beats} / {unit}"));
        }

        if ((int)tab.Snap != _musicSnap)
        {
            _musicSnap = (int)tab.Snap;
            SetText("song-snap-label", Music.SongGrid.Label(tab.Snap));
        }

        if ((int)tab.Midi.Quantize != _musicQuantize)
        {
            _musicQuantize = (int)tab.Midi.Quantize;
            SetText("song-quantize-label", SongPanel.QuantizeLabel(tab.Midi.Quantize));
        }

        if (_musicReadOnly != tab.Document.IsReadOnly)
        {
            _musicReadOnly = tab.Document.IsReadOnly;
            var badge = document.GetElementById("song-readonly");
            badge.SetClass("hidden", !tab.Document.IsReadOnly);
            badge.SetAttribute("data-tooltip", tab.Document.ReadOnlyNotice ?? "This song is read-only");
        }

        UpdateMusicPosition(tab);
        var underruns = tab.Player.Underruns + tab.Player.PluginXruns;
        if (underruns != _musicUnderruns)
        {
            _musicUnderruns = underruns;
            var xruns = document.GetElementById("song-xruns");
            xruns.SetClass("bad", underruns > 0);
            // Formatted on the stack: a burst of xruns while playing must not allocate.
            Span<char> text = stackalloc char[32];
            underruns.TryFormat(text, out var digits, provider: CultureInfo.InvariantCulture);
            var word = underruns == 1 ? " xrun" : " xruns";
            word.CopyTo(text[digits..]);
            xruns.SetInnerRml(text[..(digits + word.Length)]);
        }

        // Render's button bakes an adaptive song (its split menu has Render timeline); either shows "Cancel n %" while it runs.
        var adaptive = tab.Document.Song.Adaptive is not null;
        var job = Workspace.SongView.RenderOf(tab.Document.FilePath);
        var bake = adaptive ? Workspace.SongView.BakeOf(tab.Document.FilePath) : null;
        var running = bake is not null || job is not null;
        var percent = bake is not null ? (int)(bake.Progress * 100) : job is not null ? (int)(job.Progress * 100) : -1;
        var render = (adaptive ? 1000 : 0) + (bake is not null ? 500 : 0) + percent;
        if (render != _musicRender)
        {
            _musicRender = render;
            var button = document.GetElementById("song-render");
            button.SetClass("busy", running);
            var baking = adaptive && job is null;
            button.SetAttribute("data-command", baking ? "song.bake" : "song.render");
            document.GetElementById("song-render-icon").SetClassNames("icon icon-sm icon-" + (baking ? "package" : "download"));
            SetText("song-render-label", running ? string.Create(CultureInfo.InvariantCulture, $"Cancel {percent} %") : baking ? "Bake" : "Render");
            button.SetAttribute("data-tooltip", bake is not null
                ? string.Create(CultureInfo.InvariantCulture, $"Baking {tab.Document.Name}… {bake.Status} — click to cancel (the previous bake stays)")
                : job is not null
                    ? string.Create(CultureInfo.InvariantCulture, $"Rendering {tab.Document.Name}… {percent} % — click to cancel (the previous output stays)")
                    : baking
                        ? "Bake (Ctrl+B) — the adaptive song's stems and its .mmusic manifest, what the game plays (Render timeline is in the menu beside it)"
                        : SongPanel.RenderTooltip(tab) + " (Ctrl+E)");
        }
    }

    // The tempo field and the time signature take edits only on an editable song.
    private void SetEditable(bool editable)
    {
        if (_musicEditable == editable)
            return;
        _musicEditable = editable;
        var tempo = Document.GetElementById("song-tempo");
        if (editable)
            tempo.RemoveAttribute("disabled");
        else
            tempo.SetAttribute("disabled", "");
    }

    private static void SetActive(RmlDocument document, string id, bool active, ref bool? shown)
    {
        if (shown == active)
            return;
        shown = active;
        document.GetElementById(id).SetClass("active", active);
    }

    private void UpdateMusicPosition(Music.SongTab tab)
    {
        var song = tab.Document.Song;
        var ticks = (long)tab.Player.PositionTicks;
        var shown = ticks - ticks % Math.Max(1, song.Ppq / 64);
        if (shown == _musicPosition)
            return;
        var element = Document.GetElementById("song-pos");
        if (element.IsNull)
            return;
        if (_musicPosition < 0)
            element.SetClass("empty", false);
        _musicPosition = shown;
        Span<char> text = stackalloc char[24];
        var n = Music.SongGrid.FormatPosition(text, ticks, song);
        element.SetInnerRml(text[..n]);
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
