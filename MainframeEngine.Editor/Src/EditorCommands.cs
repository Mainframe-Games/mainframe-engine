using MainframeEngine.Editor.Music;
using MainframeEngine.Editor.Video;
using MainframeEngine.Serialization;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Models;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MainframeEngine.Editor;

/// <summary>
/// Every user-facing editor action by id (menus, toolbar buttons with <c>data-command</c>, shortcuts, QA scripts).
/// <see cref="Execute"/> never lets an exception escape: failures are logged to the Output panel and shown in a
/// message box, and the editor keeps running with the scene unchanged.
/// </summary>
public sealed class EditorCommands
{
    /// <summary>How many recordings <see cref="EditorSettings.RecentVideos"/> keeps.</summary>
    internal const int RecentVideoLimit = 10;

    /// <summary>How many of them the video-mode File menu lists.</summary>
    private const int RecentVideoMenuItems = 5;

    private const string RecentVideoCommand = "video.recent:";
    private const string RecentSongCommand = "song.recent:";

    /// <summary>What File › Open accepts as a recording: the videos and the project files.</summary>
    private static readonly string[] VideoFilters = ["*.mp4", "*.mov", "*.mkv", "*.webm", "*.mfcut", "*.cutback"];

    private readonly EditorWorkspace _workspace;

    public EditorCommands(EditorWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    private EditorSession Session => _workspace.Session;
    private EditedScene? Active => Session.Active;

    /// <summary>Raised after a command ran (QA scripts and tests wait on it).</summary>
    public event Action<string>? Executed;

    /// <summary>Runs command <paramref name="id"/>; false when it is unknown or failed.</summary>
    public bool Execute(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        // While the mouse drives the viewport (a gizmo drag applies live, committed on release), commands that change
        // the scene or its history would interleave with the drag's own entry: ignore them until it ends.
        if (_workspace.Viewport?.IsInteracting == true && ChangesScene(id))
        {
            Log.Info($"[Editor] '{id}' ignored while dragging in the viewport.");
            return false;
        }

        // Simulate (G7.5) locks the scene: edits wait until Stop (saving and opening files still work).
        if (_workspace.Simulation?.IsActive == true && ChangesScene(id) && !id.StartsWith("file.", StringComparison.Ordinal))
        {
            Log.Info($"[Editor] '{id}' ignored while the scene simulates (Alt+F6 stops).");
            return false;
        }

        try
        {
            var known = Run(id);
            if (!known)
                Log.Warning($"[Editor] Unknown command '{id}'.");
            Executed?.Invoke(id);
            return known;
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            ReportError($"'{id}' failed", e);
            return false;
        }
    }

    private static bool ChangesScene(string id) =>
        id.StartsWith("edit.", StringComparison.Ordinal) || id.StartsWith("node.", StringComparison.Ordinal) ||
        id.StartsWith("scene.", StringComparison.Ordinal) || id.StartsWith("file.", StringComparison.Ordinal);

    /// <summary>
    /// Exceptions the editor survives (everything but process-fatal ones), so the video errors — a probe, project or
    /// partition failure, ffmpeg failing or missing, a bad sidecar — are reported, never fatal.
    /// </summary>
    public static bool IsRecoverable(Exception e) => e is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    /// <summary>Logs an error and shows it in a message box.</summary>
    public void ReportError(string what, Exception e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Log.Error($"[Editor] {what}: {e.Message}\n{e.StackTrace}");
        _workspace.Message.Show(new MessageRequest { Title = "Error", Message = $"{what}:\n{e.Message}", Buttons = ["OK"] });
    }

    private bool Run(string id)
    {
        if (Session.ActiveTab is SongTab song && RunSong(song, id))
            return true;
        if (Session.ActiveTab is VideoTab video && RunVideo(video, id))
            return true;
        if (id.StartsWith("tilemap.", StringComparison.Ordinal))
            return _workspace.TileMap.RunCommand(id);
        switch (id)
        {
            case "file.new" when _workspace.Mode == EditorMode.Music: NewSong(); return true;
            case "file.new": Session.NewScene(); return true;
            case "file.new_song": NewSong(); return true;
            case "file.open":
                if (_workspace.Mode == EditorMode.Video)
                    OpenRecording();
                else if (_workspace.Mode == EditorMode.Music)
                    OpenSongFile();
                else
                    OpenScene();
                return true;
            case "file.save": Save(Active, saveAs: false, then: null); return true;
            case "file.save_as": Save(Active, saveAs: true, then: null); return true;
            case "file.close": CloseTab(Session.ActiveTab); return true;
            case "file.quit": _workspace.RequestQuit(); return true;
            case "file.export": _workspace.ExportDialog.Open(); return true;
            case "edit.undo": Active?.History.Undo(); return true;
            case "edit.redo": Active?.History.Redo(); return true;
            case "edit.history": ShowHistory(); return true;
            case "edit.delete": Active?.Delete(Active.Selection.Nodes.ToArray()); return true;
            case "edit.duplicate": Active?.Duplicate(Active.Selection.Nodes.ToArray()); return true;
            case "edit.rename": Rename(); return true;
            case "node.add": AddNode(); return true;
            case "scene.instance": InstanceScene(); return true;
            case "node.move_up": MoveSelected(-1); return true;
            case "node.move_down": MoveSelected(1); return true;
            case "gizmo.select": SetMode(GizmoMode.Select); return true;
            case "gizmo.translate": SetMode(GizmoMode.Translate); return true;
            case "gizmo.rotate": SetMode(GizmoMode.Rotate); return true;
            case "gizmo.scale": SetMode(GizmoMode.Scale); return true;
            case "gizmo.local":
                _workspace.Gizmo.Local = !_workspace.Gizmo.Local;
                _workspace.Toolbar.Refresh();
                return true;
            case "gizmo.snap":
                _workspace.Gizmo.Snap = _workspace.Gizmo.Snap with { Enabled = !_workspace.Gizmo.Snap.Enabled };
                _workspace.Toolbar.Refresh();
                return true;
            case "gizmo.pivot":
                // Median → Active → Individual Origins → Median (Blender's pivot point).
                _workspace.Gizmo.Pivot = _workspace.Gizmo.Pivot switch
                {
                    GizmoPivot.Median => GizmoPivot.Active,
                    GizmoPivot.Active => GizmoPivot.Individual,
                    _ => GizmoPivot.Median,
                };
                _workspace.Toolbar.Refresh();
                return true;
            case "view.frame": _workspace.Viewport.FrameSelection(); return true;
            case "view.grid":
                _workspace.Viewport.GridVisible = !_workspace.Viewport.GridVisible;
                _workspace.Toolbar.Refresh();
                return true;
            case "view.post":
                _workspace.Viewport.PostPreview = !_workspace.Viewport.PostPreview;
                _workspace.Toolbar.Refresh();
                return true;
            case "view.navigation":
                _workspace.Viewport.NavigationVisible = !_workspace.Viewport.NavigationVisible;
                _workspace.Toolbar.Refresh();
                return true;
            case "view.front": Active?.Camera.SetView(EditorView.Front); return true;
            case "view.right": Active?.Camera.SetView(EditorView.Right); return true;
            case "view.top": Active?.Camera.SetView(EditorView.Top); return true;
            case "view.reset":
                if (Active?.Camera is { Is2D: true } camera2D)
                    camera2D.Reset2D();
                else
                    Active?.Camera?.Reset();
                return true;
            case "view.2d":
                _workspace.Viewport.Toggle2D();
                _workspace.Toolbar.Refresh();
                return true;
            case "help.docs": OsShell.OpenUrl(EditorBrand.DocsUrl); return true;
            case "help.shortcuts": ShowShortcuts(); return true;
            case "help.about": ShowAbout(); return true;
            case "help.check_updates": _workspace.Updates.CheckNow(); return true;
            case "help.update":
                if (_workspace.Updates.Available is { IsUpdate: true })
                    _workspace.UpdateDialog.Open();
                return true;
            case "play.main": _workspace.Play.PlayMain(); return true;
            case "play.scene": _workspace.Play.PlayCurrent(); return true;
            case "play.pause": _workspace.Play.TogglePause(); return true;
            case "play.stop": _workspace.Play.Stop(); return true;
            case "play.record": _workspace.Play.ToggleRecording(); return true;
            case "play.another": _workspace.Play.PlayAnotherInstance(); return true;
            case "play.instances": _workspace.Play.PlayInstances(); return true;
            case "play.reload_scene": _workspace.Play.ReloadScene(); return true;
            case "simulate.toggle": _workspace.Simulation.Toggle(); return true;
            case "simulate.pause": _workspace.Simulation.TogglePause(); return true;
            case "simulate.keep": _workspace.Simulation.KeepTransforms(); return true;
            case "project.build_reload": _workspace.Project.BuildAndReload(); return true;
            case "project.reload_code": _workspace.Project.ReloadGameAssembly(); return true;
            case "project.open": ChooseProjectFile(); return true;
            case "project.new": _workspace.NewProject.Open(); return true;
            case "project.manager": ShowProjectManager(); return true;
            case "project.settings": _workspace.ProjectSettings.Open(); return true;
            case "project.user_data": _workspace.Project.OpenUserDataFolder(); return true;
            case "project.close": CloseProject(); return true;
            case "editor.settings": _workspace.EditorSettingsDialog.Open(); return true;
            case "video.settings": _workspace.EditorSettingsDialog.Open(scrollToVideo: true); return true;
            case "editor.mode.game": _workspace.SetMode(EditorMode.Game); return true;
            case "editor.mode.video": _workspace.SetMode(EditorMode.Video); return true;
            case "editor.mode.music": _workspace.SetMode(EditorMode.Music); return true;
            case "music.settings": _workspace.EditorSettingsDialog.Open(scrollToMusic: true); return true;
            case "editor.video_editor":
                _workspace.ProjectManager.Close();
                _workspace.SetMode(EditorMode.Video);
                return true;
            case "view.output" when _workspace.Mode == EditorMode.Music: _workspace.ToggleMusicOutput(); return true;
            case "view.output": _workspace.ToggleVideoOutput(); return true;
            case "video.toggle_transcript": _workspace.ToggleVideoTranscript(); return true;
            case "music.browser": _workspace.ToggleMusicBrowser(); return true;
            case "music.adaptive": _workspace.ToggleMusicAdaptive(); return true;
            // The video toolbar's buttons without a video tab: they show disabled, and a click does nothing.
            case "video.play" or "video.stop" or "video.transcribe" or "video.remove_fillers" or "video.detect_silence"
                or "video.detect_idle" or "video.export" or "video.export_transcript" when Session.ActiveTab is not VideoTab:
                return true;
            // The same for the music toolbar and the Song menu without a song tab.
            case { } command when command.StartsWith("song.", StringComparison.Ordinal) &&
                                  !command.StartsWith(RecentSongCommand, StringComparison.Ordinal) && Session.ActiveTab is not SongTab:
                return true;
            default:
                if (id.StartsWith(RecentSongCommand, StringComparison.Ordinal) &&
                    int.TryParse(id.AsSpan(RecentSongCommand.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var recentSong))
                {
                    OpenRecentSong(recentSong);
                    return true;
                }

                if (id.StartsWith(RecentVideoCommand, StringComparison.Ordinal) &&
                    int.TryParse(id.AsSpan(RecentVideoCommand.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var recent))
                {
                    OpenRecentVideo(recent);
                    return true;
                }

                return id.StartsWith("fs.", StringComparison.Ordinal) && _workspace.FileSystem.RunCommand(id);
        }
    }

    // Commands that act on the active video tab (false: not a video command, run it as usual).
    private bool RunVideo(VideoTab video, string id)
    {
        switch (id)
        {
            case "file.save": SaveVideo(video, saveAs: false, then: null); return true;
            case "file.save_as": SaveVideo(video, saveAs: true, then: null); return true;
            // A boundary drag in flight ends first, as one recorded step, so undo reverts exactly that drag.
            case "edit.undo":
                video.Controller.CancelGesture();
                video.Document.Editor.Undo();
                return true;
            case "edit.redo":
                video.Controller.CancelGesture();
                video.Document.Editor.Redo();
                return true;
            case "edit.history": return true;
            case "edit.delete": video.Controller.DeleteHovered(); return true;
            case "video.play": video.TogglePlay(); return true;
            case "video.stop": video.Stop(); return true;
            case "video.detect_idle":
                video.Controller.CancelGesture();
                video.Notice = video.Document.Capture is null
                    ? "This recording has no Mainframe Engine sidecar."
                    : video.Document.DetectIdleGameplay(_workspace.Settings.VideoGameplay);
                return true;
            case "video.detect_silence": _workspace.VideoView.DetectSilence(video); return true;
            case "video.transcribe": _workspace.VideoView.Transcribe(video); return true;
            case "video.remove_fillers": _workspace.VideoView.RemoveFillers(video); return true;
            case "video.export": _workspace.VideoView.Export(video); return true;
            case "video.export_transcript": ExportTranscript(video); return true;
            default: return false;
        }
    }

    // Commands that act on the active song tab instead of a scene (false: not a song command, run it as usual).
    private bool RunSong(SongTab song, string id)
    {
        var history = song.Document.History;
        switch (id)
        {
            case "file.save": SaveSong(song, saveAs: false, then: null); return true;
            case "file.save_as": SaveSong(song, saveAs: true, then: null); return true;
            case "edit.undo": history.Undo(); return true;
            case "edit.redo": history.Redo(); return true;
            case "edit.history": return true;
            case "edit.delete": song.Controller.Delete(); return true;
            case "edit.duplicate": song.Controller.Duplicate(); return true;
            case "song.play": song.Player.TogglePlay(); return true;
            case "song.adaptive_play": song.PlayAdaptive(fromPlayhead: false); return true;
            case "song.adaptive_play_here": song.PlayAdaptive(fromPlayhead: true); return true;
            case "song.fire_stinger": song.FireStinger(); return true;
            case "song.make_adaptive":
                if (!song.Document.IsReadOnly && song.Document.Song.Adaptive is null)
                {
                    song.Document.EnableAdaptive();
                    _workspace.ShowMusicRightTab(adaptive: true);
                }

                return true;
            case "song.stop": song.Player.Stop(); return true;
            case "song.rewind": song.Controller.Key(Silk.NET.Input.Key.Home, EditorModifiers.None); return true;
            case "song.loop": song.Controller.ToggleLoop(); return true;
            case "song.record": song.Controller.ToggleRecording(); return true;
            case "song.render": _workspace.SongView.Render(song.Document.FilePath); return true;
            case "song.render_menu":
                {
                    var (x, y) = _workspace.Toolbar.Below("song-render");
                    _workspace.Popup.Show(
                    [
                        new MenuItem(BakeLabel(song), "song.bake", "Ctrl+B", song.Document.Song.Adaptive is not null, Icon: "package"),
                        new MenuItem("Render Timeline…", "song.render", "Ctrl+E", Icon: "download"),
                    ], x, y, command =>
                    {
                        if (command is not null)
                            Execute(command);
                    });
                    return true;
                }
            case "song.bake":
                if (song.Document.Song.Adaptive is not null)
                    _workspace.SongView.Bake(song.Document.FilePath);
                return true;
            case "song.piano_roll": _workspace.ToggleMusicPane(SongBottomPanel.PianoRoll); return true;
            case "song.mixer": _workspace.ToggleMusicPane(SongBottomPanel.Mixer); return true;
            case "song.curves": _workspace.ToggleMusicPane(SongBottomPanel.Curves); return true;
            case "song.hide_pane": _workspace.SetMusicPane(SongBottomPanel.None); return true;
            case "song.mute_track":
                if (song.SelectedTrack is { } muted && !song.Document.IsReadOnly)
                    song.Document.SetTrackMute(muted, !muted.Mute);
                return true;
            case "song.solo_track":
                if (song.SelectedTrack is { } soloed && !song.Document.IsReadOnly)
                    song.Document.SetTrackSolo(soloed, !soloed.Solo);
                return true;
            case "song.signature": _workspace.SongPanel.SignatureMenu(_workspace.Toolbar.Below("song-sig")); return true;
            case "song.snap": _workspace.SongPanel.SnapMenu(_workspace.Toolbar.Below("song-snap")); return true;
            case "song.quantize": _workspace.SongPanel.QuantizeMenu(_workspace.Toolbar.Below("song-quantize")); return true;
            case "song.add_instrument_track": _workspace.SongPanel.AddTrack(SongTrackKind.Instrument); return true;
            case "song.add_audio_track": _workspace.SongPanel.AddTrack(SongTrackKind.Audio); return true;
            case "song.copy_notes": song.Controller.CopyNotes(); return true;
            case "song.paste_notes": song.Controller.PasteNotes(); return true;
            case "song.quantize_notes": song.Controller.Quantize(); return true;
            default: return false;
        }
    }

    /// <summary>File › New Song: asks where to save it (default <c>Assets/Music</c>), creates it and opens its tab.</summary>
    public void NewSong()
    {
        var root = Session.ProjectRoot;
        var folder = root is null ? StartDirectory(null) : Path.Combine(root, "Assets", "Music");
        if (root is not null)
            Directory.CreateDirectory(folder);
        var model = new FilePickerModel(FilePickerMode.Save, folder, ["*.msong"], "NewSong.msong");
        _workspace.FilePicker.Show(model, "New Song", "Create", path => CreateSong(path));
    }

    /// <summary>Creates a song at <paramref name="path"/> (one built-in instrument track) and opens it; false on failure.</summary>
    public bool CreateSong(string path)
    {
        try
        {
            var full = SongTab.CreateFile(path);
            Log.Info($"[Editor] Created {Session.DisplayPath(full)}");
            Session.OpenSong(full);
            _workspace.FileSystem.Rescan();
            return true;
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            ReportError($"Could not create {Path.GetFileName(path)}", e);
            return false;
        }
    }

    /// <summary>File › Open Song… in music mode: a picker on <c>.msong</c> files, starting in the project's <c>Assets/Music</c>.</summary>
    public void OpenSongFile()
    {
        var root = Session.ProjectRoot;
        var music = root is null ? null : Path.Combine(root, "Assets", "Music");
        var folder = music is not null && Directory.Exists(music) ? music : StartDirectory(null);
        var model = new FilePickerModel(FilePickerMode.Open, folder, ["*.msong"]);
        _workspace.FilePicker.Show(model, "Open Song", "Open", OpenSong);
    }

    // File › Recent Songs and the music mode's empty state: the project's songs, most recently changed first.
    private void OpenRecentSong(int index)
    {
        var songs = SongPanel.FindSongs(Session.ProjectRoot, SongPanel.MaxRecentSongs);
        if ((uint)index < (uint)songs.Count)
            OpenSong(songs[index]);
    }

    /// <summary>Opens a song tab for <paramref name="path"/>, reporting failures.</summary>
    public void OpenSong(string path)
    {
        try
        {
            Session.OpenSong(path);
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            ReportError($"Could not open {Path.GetFileName(path)}", e);
        }
    }

    /// <summary>Saves a song tab (Save As asks for a file), then runs <paramref name="then"/> with whether it was saved.</summary>
    public void SaveSong(SongTab song, bool saveAs, Action<bool>? then)
    {
        if (song.Document.IsReadOnly && !saveAs)
        {
            ReportError($"Could not save {song.DisplayName}", new InvalidOperationException(song.Document.ReadOnlyNotice ?? "The song is read-only."));
            then?.Invoke(false);
            return;
        }

        if (!saveAs)
        {
            var saved = TrySaveSong(song, null);
            then?.Invoke(saved);
            return;
        }

        var model = new FilePickerModel(FilePickerMode.Save, Path.GetDirectoryName(song.Document.FilePath)!, ["*.msong"], song.DisplayName);
        _workspace.FilePicker.Show(model, "Save Song As", "Save", path =>
        {
            var saved = TrySaveSong(song, path);
            then?.Invoke(saved);
        }, onCancel: () => then?.Invoke(false));
    }

    private bool TrySaveSong(SongTab song, string? path)
    {
        try
        {
            Session.SaveSong(song, path);
            Log.Info($"[Editor] Saved {Session.DisplayPath(song.FilePath)}");
            _workspace.FileSystem.UpdateUnsaved();
            return true;
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            ReportError($"Could not save {song.DisplayName}", e);
            return false;
        }
    }

    // ── Video ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a recording, <c>.mfcut</c> or <c>.cutback</c> in a video tab (switching to video mode) behind the splash and
    /// puts it at the top of the recent videos. Without ffmpeg it says so instead.
    /// </summary>
    public void OpenVideo(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            var ffmpeg = LocateFfmpeg(_workspace.Settings.VideoFfmpegPath);
            _workspace.RunWithSplash($"Opening {Path.GetFileName(path)}…", () =>
            {
                try
                {
                    Session.OpenVideo(path, ffmpeg, _workspace.Settings.VideoUndoHistoryLimit);
                    TouchRecentVideo(path);
                }
                catch (Exception e) when (IsRecoverable(e))
                {
                    ReportError($"Could not open {Path.GetFileName(path)}", e);
                }
            });
        }
        catch (FfmpegMissingException e)
        {
            // No tab exists yet to hold a notice, so this one is modal, with the way to the setting on it.
            Log.Error($"[Editor] The video editor needs ffmpeg: {e.Message}");
            _workspace.Message.Show(new MessageRequest
            {
                Title = "Error",
                Message = $"The video editor needs ffmpeg:\n{e.Message}",
                Buttons = ["Video Settings…", "OK"],
                CancelButton = 1,
                Callback = (button, _) =>
                {
                    if (button == 0)
                        Execute("video.settings");
                },
            });
        }
    }

    /// <summary>Finds ffmpeg for video mode (<see cref="VideoFfmpeg.Locate"/>); tests swap it so the machine's ffmpeg never matters.</summary>
    internal Func<string?, FfmpegLocation> LocateFfmpeg { get; set; } = VideoFfmpeg.Locate;

    /// <summary>
    /// Opens any file by kind: recordings and video projects in a video tab, songs in a song tab, <c>.rml</c> in a UI
    /// preview, anything else as a scene. File › Open's pickers use it.
    /// </summary>
    public void OpenAny(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        switch (FileKinds.Of(path, isDirectory: false))
        {
            case FileKind.Video or FileKind.VideoProject:
                OpenVideo(path);
                break;
            case FileKind.Song:
                OpenSong(path);
                break;
            case FileKind.Rml:
                try
                {
                    Session.OpenUiPreview(path);
                }
                catch (Exception e) when (IsRecoverable(e))
                {
                    ReportError($"Could not preview {Path.GetFileName(path)}", e);
                }

                break;
            default:
                _workspace.RunWithSplash($"Loading {Path.GetFileName(path)}…", () =>
                {
                    try
                    {
                        Session.Open(path);
                    }
                    catch (Exception e) when (IsRecoverable(e))
                    {
                        ReportError($"Could not open {Path.GetFileName(path)}", e);
                    }
                });
                break;
        }
    }

    /// <summary>Video mode's File › Open: a picker for a recording or a video project.</summary>
    public void OpenRecording()
    {
        var start = Session.ActiveVideo?.Document.SourcePath ?? _workspace.Settings.RecentVideos.FirstOrDefault();
        var directory = start is not null && Path.GetDirectoryName(start) is { } folder && Directory.Exists(folder)
            ? folder
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var model = new FilePickerModel(FilePickerMode.Open, directory, VideoFilters);
        _workspace.FilePicker.Show(model, "Open Recording", "Open", OpenAny);
    }

    /// <summary>
    /// Saves a video tab's project, then runs <paramref name="then"/> with whether it was saved. An unsaved recording (or an
    /// imported <c>.cutback</c>) and Save As ask where, suggesting <c>&lt;video dir&gt;/&lt;video name&gt;.mfcut</c>.
    /// </summary>
    public void SaveVideo(VideoTab tab, bool saveAs, Action<bool>? then)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!saveAs && tab.Document.FilePath is not null)
        {
            var saved = TrySaveVideo(tab, null); // not inside then?.Invoke(...): a null callback would skip the save
            then?.Invoke(saved);
            return;
        }

        var suggested = tab.Document.FilePath ?? tab.Document.DefaultProjectPath;
        var model = new FilePickerModel(FilePickerMode.Save, Path.GetDirectoryName(suggested)!, ["*.mfcut"], Path.GetFileName(suggested));
        _workspace.FilePicker.Show(model, "Save Video Project", "Save",
            path =>
            {
                var saved = TrySaveVideo(tab, path); // not inside then?.Invoke(...): a null callback would skip the save
                then?.Invoke(saved);
            },
            onCancel: () => then?.Invoke(false));
    }

    /// <summary>
    /// File › Export Transcript: asks where (default <c>&lt;recording name&gt;.txt</c> beside the recording) and writes the
    /// edited transcript, cut words left out and times matching the exported video; <c>.srt</c> writes subtitles.
    /// </summary>
    public void ExportTranscript(VideoTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.Document.Transcript.Count == 0)
        {
            tab.Notice = "There is no transcript to export. Transcribe the recording first.";
            return;
        }

        var source = tab.Document.SourcePath;
        var model = new FilePickerModel(FilePickerMode.Save, Path.GetDirectoryName(source)!, ["*.txt", "*.srt"],
            Path.GetFileNameWithoutExtension(source) + ".txt");
        _workspace.FilePicker.Show(model, "Export Transcript", "Export", path => WriteTranscript(tab, path));
    }

    /// <summary>
    /// Writes <paramref name="tab"/>'s edited transcript to <paramref name="path"/> as UTF-8 without a BOM: <c>.srt</c>
    /// as subtitles, anything else as text (<c>.txt</c> is added unless the name already ends in it). Never the recording
    /// itself. A failure is shown in a message box; false then.
    /// </summary>
    internal bool WriteTranscript(VideoTab tab, string path)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrEmpty(path);
        var srt = path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase);
        if (!srt && !path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            path += ".txt";
        try
        {
            if (VideoPaths.SameFile(path, tab.Document.SourcePath))
                throw new IOException("That is the recording itself; the transcript goes in a separate file.");
            File.WriteAllText(path, tab.Document.TranscriptText(srt), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            tab.Notice = $"Exported {Path.GetFileName(path)}.";
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ReportError("Could not write the transcript", e);
            return false;
        }
    }

    private bool TrySaveVideo(VideoTab tab, string? path)
    {
        try
        {
            Session.SaveVideo(tab, path);
            Log.Info($"[Editor] Saved {tab.FilePath}");
            _workspace.FileSystem.UpdateUnsaved();
            TouchRecentVideo(tab.FilePath!, replacing: tab.Document.SourcePath);
            return true;
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            ReportError($"Could not save {tab.DisplayName}", e);
            return false;
        }
    }

    /// <summary>
    /// Moves <paramref name="path"/> to the front of <paramref name="list"/> (a copy already there is removed, ignoring case on
    /// Windows) and keeps at most <see cref="RecentVideoLimit"/> entries.
    /// </summary>
    internal static void TouchRecent(List<string> list, string path)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentException.ThrowIfNullOrEmpty(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        list.RemoveAll(p => string.Equals(p, path, comparison));
        list.Insert(0, path);
        if (list.Count > RecentVideoLimit)
            list.RemoveRange(RecentVideoLimit, list.Count - RecentVideoLimit);
    }

    // The saved project supersedes its recording in the list (replacing): reopening the bare video would start over.
    private void TouchRecentVideo(string path, string? replacing = null)
    {
        var full = Path.GetFullPath(path);
        var settings = _workspace.Settings.Clone();
        if (replacing is not null && !EditorSession.PathsEqual(replacing, full))
            settings.RecentVideos.RemoveAll(p => EditorSession.PathsEqual(p, replacing));
        TouchRecent(settings.RecentVideos, full);
        _workspace.StoreSettings(settings);
    }

    // File › Recent Videos: a recording that has gone is dropped from the list, with a message.
    private void OpenRecentVideo(int index)
    {
        var recent = _workspace.Settings.RecentVideos;
        if ((uint)index >= (uint)recent.Count)
            return;
        var path = recent[index];
        if (File.Exists(path))
        {
            OpenVideo(path);
            return;
        }

        var settings = _workspace.Settings.Clone();
        settings.RecentVideos.RemoveAt(index);
        _workspace.StoreSettings(settings);
        _workspace.Message.Show(new MessageRequest
        {
            Title = "Recent Videos",
            Message = $"{Path.GetFileName(path)} was moved or deleted.",
            Buttons = ["OK"],
        });
    }

    private void SetMode(GizmoMode mode)
    {
        _workspace.Gizmo.Mode = mode;
        _workspace.Toolbar.Refresh();
    }

    // ── Files ────────────────────────────────────────────────────────────────────────────────────────────────────

    private string StartDirectory(EditedScene? scene)
    {
        if (scene?.FilePath is { } path && Path.GetDirectoryName(path) is { } directory)
            return directory;
        if (Session.ProjectRoot is { } root)
            return Directory.Exists(Path.Combine(root, "Assets", "Scenes")) ? Path.Combine(root, "Assets", "Scenes") : root;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>Game mode's File › Open: a picker for a scene or a recording (which opens in video mode).</summary>
    public void OpenScene()
    {
        var model = new FilePickerModel(FilePickerMode.Open, StartDirectory(Active), ["*.mscene", .. VideoFilters]);
        _workspace.FilePicker.Show(model, "Open", "Open", OpenAny);
    }

    /// <summary>
    /// Saves <paramref name="scene"/> (asking for a file when it has none or <paramref name="saveAs"/>), then runs
    /// <paramref name="then"/> with whether it was saved.
    /// </summary>
    public void Save(EditedScene? scene, bool saveAs, Action<bool>? then)
    {
        if (scene is null)
        {
            then?.Invoke(false);
            return;
        }

        if (!saveAs && scene.FilePath is not null)
        {
            var saved = TrySave(scene, null); // not inside then?.Invoke(...): a null callback would skip the save
            then?.Invoke(saved);
            return;
        }

        var name = scene.FilePath is null ? "NewScene.mscene" : Path.GetFileName(scene.FilePath);
        var model = new FilePickerModel(FilePickerMode.Save, StartDirectory(scene), ["*.mscene"], name);
        _workspace.FilePicker.Show(model, saveAs ? "Save Scene As" : "Save Scene", "Save",
            path =>
            {
                var saved = TrySave(scene, path);
                then?.Invoke(saved);
            },
            onCancel: () => then?.Invoke(false));
    }

    /// <summary>Saves every dirty scene that has a file (untitled ones are skipped with a warning); false if any failed.</summary>
    public bool SaveAll() => SaveAll(static _ => true);

    // Saves the dirty tabs that include accepts (a project switch: the game tabs only).
    private bool SaveAll(Func<IEditorTab, bool> include)
    {
        var ok = true;
        foreach (var tab in Session.Tabs)
        {
            if (!include(tab))
                continue;
            if (tab is SongTab { IsDirty: true, Document.IsReadOnly: false } song)
            {
                ok &= TrySaveSong(song, null);
            }
            else if (tab is VideoTab { IsDirty: true } video)
            {
                if (video.FilePath is null)
                {
                    Log.Warning($"[Editor] {video.DisplayName} has never been saved; use Save As to keep it.");
                    ok = false;
                    continue;
                }

                ok &= TrySaveVideo(video, null);
            }
        }

        foreach (var scene in Session.Scenes)
        {
            if (!scene.IsDirty || !include(scene))
                continue;
            if (scene.FilePath is null)
            {
                Log.Warning($"[Editor] {scene.DisplayName} has never been saved; use Save As to keep it.");
                ok = false;
                continue;
            }

            ok &= TrySave(scene, null);
        }

        return ok;
    }

    /// <summary>
    /// Saves every dirty scene and video project: those with a file at once, then each untitled one through Save As (one
    /// dialog after the other). <paramref name="then"/> gets true only when everything was saved (false on a failure or a
    /// cancelled dialog).
    /// </summary>
    public void SaveAll(Action<bool> then) => SaveAll(then, static _ => true);

    private void SaveAll(Action<bool> then, Func<IEditorTab, bool> include)
    {
        ArgumentNullException.ThrowIfNull(then);
        if (!SaveAll(include) && Session.Tabs.Any(t => t.IsDirty && include(t) && t is SongTab or EditedScene { FilePath: not null } or VideoTab { FilePath: not null }))
        {
            then(false); // a file-backed save failed (already reported)
            return;
        }

        var untitled = new Queue<IEditorTab>(Session.Tabs.Where(t => t.IsDirty && include(t) && t.FilePath is null && t is EditedScene or VideoTab));
        void Next(bool previousSaved)
        {
            if (!previousSaved)
            {
                then(false);
                return;
            }

            if (untitled.Count == 0)
            {
                then(true);
                return;
            }

            var tab = untitled.Dequeue();
            Session.Activate(tab);
            if (tab is VideoTab video)
                SaveVideo(video, saveAs: true, then: Next);
            else
                Save((EditedScene)tab, saveAs: true, then: Next);
        }

        Next(true);
    }

    private bool TrySave(EditedScene scene, string? path)
    {
        try
        {
            Session.Save(scene, path);
            Log.Info($"[Editor] Saved {Session.DisplayPath(scene.FilePath)}");
            return true;
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            ReportError($"Could not save {scene.DisplayName}", e);
            return false;
        }
    }

    /// <summary>
    /// Closes <paramref name="tab"/>: a scene, song or video project with unsaved changes asks to save first
    /// (<see cref="CloseScene"/>, <see cref="CloseWithPrompt"/>).
    /// </summary>
    public void CloseTab(IEditorTab? tab)
    {
        if (tab is EditedScene scene)
            CloseScene(scene);
        else if (tab is SongTab { IsDirty: true } or VideoTab { IsDirty: true })
            CloseWithPrompt(tab);
        else if (tab is not null)
            Session.Close(tab);
    }

    /// <summary>Closes <paramref name="scene"/>, asking to save it first when it has unsaved changes.</summary>
    public void CloseScene(EditedScene? scene)
    {
        if (scene is null)
            return;
        if (!scene.IsDirty)
        {
            Session.Close(scene);
            return;
        }

        _workspace.Message.Show(new MessageRequest
        {
            Title = "Unsaved changes",
            Message = $"Save the changes to {scene.DisplayName} before closing it?",
            Buttons = ["Save", "Don't Save", "Cancel"],
            DefaultButton = 0,
            CancelButton = 2,
            Callback = (button, _) =>
            {
                if (button == 1)
                    Session.Close(scene);
                else if (button == 0)
                    Save(scene, saveAs: false, then: saved =>
                    {
                        if (saved)
                            Session.Close(scene);
                    });
            },
        });
    }

    // ── Projects ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the project in <paramref name="directory"/>: asks to save unsaved scenes, closes them (the game tabs; video
    /// tabs stay) and the previous project's code, then opens it (building its game code when needed) and its main scene, behind the splash.
    /// </summary>
    public void OpenProject(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        AfterUnsavedScenes("Open Project", () => AfterAssetsFolderCheck(directory, () =>
            _workspace.RunWithSplash($"Opening {Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar))}…", () => OpenProjectNow(directory, null))));
    }

    /// <summary>
    /// Runs <paramref name="open"/> at once for a project with an <c>Assets/</c> folder. A project that still has the old
    /// <c>Content/</c> folder (ADR 0197) is offered the rename first; declining leaves it closed, since the editor reads
    /// only <c>Assets/</c>.
    /// </summary>
    public void AfterAssetsFolderCheck(string directory, Action open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(open);
        if (!AssetsFolderMigration.IsNeeded(directory))
        {
            open();
            return;
        }

        if (_workspace.Splash.Visible)
            _workspace.Splash.Finish();
        var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        _workspace.Message.Show(new MessageRequest
        {
            Title = "Rename Content to Assets",
            Message = $"{name} keeps its files in {ContentPaths.LegacyFolderName}/, the folder's name before this engine version. " +
                      $"The editor reads {ContentPaths.FolderName}/: rename the folder and update the {ContentPaths.LegacyFolderName}/ " +
                      "paths in the project's scenes, resources, UI, project file, MSBuild files and code?\n\n" +
                      "Commit or back up the project first.",
            Buttons = ["Rename and Open", "Cancel"],
            DefaultButton = 0,
            CancelButton = 1,
            Icon = "folder",
            Callback = (button, _) =>
            {
                if (button == 0 && MigrateAssetsFolder(directory))
                {
                    open();
                    return;
                }

                if (Session.Scenes.Count == 0 && Session.ProjectRoot is null)
                    _workspace.ProjectManager.Open();
            },
        });
    }

    private bool MigrateAssetsFolder(string directory)
    {
        try
        {
            var files = AssetsFolderMigration.Run(directory);
            Log.Info($"[Editor] Renamed {ContentPaths.LegacyFolderName}/ to {ContentPaths.FolderName}/ in {directory}; " +
                     $"updated the paths in {files.Count} file(s){(files.Count > 0 ? ": " + string.Join(", ", files) : ".")}");
            return true;
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            ReportError($"Could not rename {ContentPaths.LegacyFolderName}/ to {ContentPaths.FolderName}/ in {directory}", e);
            return false;
        }
    }

    /// <summary>
    /// A package project older than this editor is moved to the editor's <c>MainframeEngine</c> version before it builds
    /// (<see cref="ProjectEngineVersion.Upgrade"/>; the build restores the new package). Current, checkout and unknown
    /// projects are left as they are, as is one newer than the editor, which is never downgraded (a warning).
    /// </summary>
    private void UpgradeEnginePackage(string directory)
    {
        try
        {
            var engine = ProjectEngineVersion.Read(directory);
            switch (engine.StatusAgainst(EngineInfo.Version))
            {
                case ProjectEngineStatus.Older:
                    var files = ProjectEngineVersion.Upgrade(directory, EngineInfo.Version);
                    Log.Info($"[Editor] Updated the project's MainframeEngine package from {engine.Version} to {EngineInfo.Version} " +
                             $"({string.Join(", ", files)}).");
                    break;
                case ProjectEngineStatus.Newer:
                    Log.Warning($"[Editor] The project uses MainframeEngine {engine.Version}, newer than this editor " +
                                $"({EngineInfo.Version}); update the editor to work on it.");
                    break;
            }
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            ReportError($"Could not update the MainframeEngine package of {directory}", e);
        }
    }

    /// <summary>Opens a project at once (no questions): start-up and <see cref="OpenProject"/>; then <paramref name="scene"/> or the main scene.</summary>
    public void OpenProjectNow(string directory, string? scene)
    {
        _workspace.ProjectManager.Close();
        _workspace.Play.Stop();
        _workspace.Simulation.Stop("Simulation stopped: the project closes."); // the copy holds game objects: free it before the code unloads
        Session.CloseProjectTabs();
        _workspace.Inspector.CloseResource();
        // Music mode stays music mode: the start scene opens in game mode's tab strip, behind the music editor.
        var mode = _workspace.Mode;
        UpgradeEnginePackage(directory);
        try
        {
            _workspace.Project.Open(directory, onReady: () =>
            {
                OpenStartScene(scene);
                if (mode == EditorMode.Music)
                    _workspace.SetMode(EditorMode.Music);
            });
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            ReportError($"Could not open the project at {directory}", e);
            if (Session.Scenes.Count == 0 && Session.ProjectRoot is null)
                _workspace.ProjectManager.Open();
        }
    }

    // The requested scene, else the project's main scene, else an empty scene.
    private void OpenStartScene(string? scene)
    {
        var path = scene ?? _workspace.Project.MainScenePath();
        if (path is not null)
        {
            try
            {
                Session.Open(path);
                return;
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                Log.Error($"[Editor] Could not open '{path}': {e.Message}");
            }
        }

        if (Session.Scenes.Count == 0)
            Session.NewScene();
    }

    private void ChooseProjectFile()
    {
        var start = Session.ProjectRoot is { } root && Path.GetDirectoryName(root) is { } parent
            ? parent
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ChooseProjectFile(start);
    }

    /// <summary>
    /// Open Project (File menu, Project Manager): a file picker that lists and accepts only <c>.mfproj</c> files — never a
    /// folder — and opens the project of the chosen <c>project.mfproj</c>.
    /// </summary>
    public void ChooseProjectFile(string startDirectory)
    {
        var model = new FilePickerModel(FilePickerMode.Open, startDirectory, ["*.mfproj"]);
        _workspace.FilePicker.Show(model, "Open Project", "Open", file =>
        {
            if (EditorCommandLine.ProjectFolderOf(file) is not { } folder)
            {
                _workspace.Message.Show(new MessageRequest
                {
                    Title = "Not a Project",
                    Message = $"{file} is not a project file. Choose a project's {ProjectSettings.FileName}.",
                    Buttons = ["OK"],
                });
                return;
            }

            OpenProject(folder);
        });
    }

    private void ShowProjectManager() => AfterUnsavedScenes("Project Manager", () =>
    {
        _workspace.Play.Stop();
        _workspace.Simulation.Stop("Simulation stopped: the project closes."); // the copy holds game objects: free it before the code unloads
        Session.CloseProjectTabs();
        _workspace.Inspector.CloseResource();
        _workspace.Project.Close();
        _workspace.ProjectManager.Open();
    });

    /// <summary>Closes a song or video tab with unsaved changes after asking (Save, Don't Save, Cancel).</summary>
    private void CloseWithPrompt(IEditorTab tab) => _workspace.Message.Show(new MessageRequest
    {
        Title = "Unsaved changes",
        Message = $"Save the changes to {tab.DisplayName} before closing it?",
        Buttons = ["Save", "Don't Save", "Cancel"],
        DefaultButton = 0,
        CancelButton = 2,
        Callback = (button, _) =>
        {
            if (button == 1)
            {
                Session.Close(tab);
                return;
            }

            if (button != 0)
                return;
            void CloseIfSaved(bool saved)
            {
                if (saved)
                    Session.Close(tab);
            }

            if (tab is SongTab song)
                SaveSong(song, saveAs: false, then: CloseIfSaved);
            else if (tab is VideoTab video)
                SaveVideo(video, saveAs: false, then: CloseIfSaved);
        },
    });

    private void CloseProject() => AfterUnsavedScenes("Close Project", () =>
    {
        _workspace.Play.Stop();
        _workspace.Simulation.Stop("Simulation stopped: the project closes."); // the copy holds game objects: free it before the code unloads
        Session.CloseProjectTabs();
        _workspace.Inspector.CloseResource();
        _workspace.Project.Close();
        _workspace.ProjectManager.Open();
    });

    // Runs then once the project's unsaved tabs (scenes and songs) were saved or discarded (Save All / Don't Save / Cancel).
    // Only those: a project switch closes them, and video tabs, which need no project, stay open with their changes.
    private void AfterUnsavedScenes(string title, Action then)
    {
        var dirty = Session.DirtyTabs.Where(static t => t.Mode.UsesProject()).ToList();
        if (dirty.Count == 0)
        {
            then();
            return;
        }

        _workspace.Message.Show(new MessageRequest
        {
            Title = title,
            Message = $"Save the changes to {string.Join(", ", dirty.Select(s => s.DisplayName))} first?",
            Buttons = ["Save All", "Don't Save", "Cancel"],
            DefaultButton = 0,
            CancelButton = 2,
            Callback = (button, _) =>
            {
                if (button == 1)
                    then();
                else if (button == 0)
                    SaveAll(saved =>
                    {
                        if (saved)
                            then();
                    }, static t => t.Mode.UsesProject());
            },
        });
    }

    // ── Nodes ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Add Node dialog: every registered node type as an inheritance tree (abstract types are structure only).</summary>
    public void AddNode()
    {
        if (Active is not { } scene)
            return;
        _workspace.TreePicker.Show(new TreePickerRequest
        {
            Kind = "node",
            Title = "Create New Node",
            OkLabel = "Create",
            Entries = PickerSources.NodeTypes(),
            OnAccept = entry =>
            {
                var info = (NodeTypeInfo)entry.Payload!;
                var node = (Node)info.CreateInstance();
                node.Name = info.Name;
                scene.AddNode(node);
            },
        });
    }

    /// <summary>Registered node types the user can create (not abstract, not the tree root or placeholders).</summary>
    public static IReadOnlyList<NodeTypeInfo> NodeTypes() =>
        [.. PickerSources.NodeTypes().Where(e => e.Selectable).Select(e => (NodeTypeInfo)e.Payload!).OrderBy(t => t.Name, StringComparer.Ordinal)];

    /// <summary>
    /// Instances a scene under the selection: the project's scenes as a folder tree (Browse… for any file), or the file
    /// picker when there is no project.
    /// </summary>
    public void InstanceScene()
    {
        if (Active is not { } scene)
            return;
        void Instance(string path)
        {
            try
            {
                scene.InstanceScene(path);
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                ReportError($"Could not instance {Path.GetFileName(path)}", e);
            }
        }

        void Browse()
        {
            var model = new FilePickerModel(FilePickerMode.Open, StartDirectory(scene), ["*.mscene"]);
            _workspace.FilePicker.Show(model, "Instance Scene", "Instance", Instance);
        }

        if (Session.ProjectRoot is not { } root)
        {
            Browse();
            return;
        }

        _workspace.TreePicker.Show(new TreePickerRequest
        {
            Kind = "scene",
            Title = "Instance Child Scene",
            OkLabel = "Instance",
            // The scene being edited cannot contain itself.
            Entries = [.. PickerSources.Scenes(root).Where(e => e.Payload is not string file || !string.Equals(
                Path.GetFullPath(file), scene.FilePath is null ? null : Path.GetFullPath(scene.FilePath), StringComparison.Ordinal))],
            OnAccept = entry => Instance((string)entry.Payload!),
            OnBrowse = Browse,
        });
    }

    /// <summary>Asks for a new name for the selected node.</summary>
    public void Rename()
    {
        if (Active is not { } scene || scene.Selection.Primary is not { } node || !scene.IsEditable(node))
            return;
        _workspace.Message.Show(new MessageRequest
        {
            Title = "Rename Node",
            Message = $"New name for {node.Name}:",
            Input = node.Name,
            Buttons = ["Rename", "Cancel"],
            DefaultButton = 0,
            CancelButton = 1,
            Callback = (button, text) =>
            {
                if (button == 0 && Node.IsInstanceValid(node))
                    scene.Rename(node, text);
            },
        });
    }

    private void MoveSelected(int delta)
    {
        if (Active is { Selection.Primary: { } node } scene)
            scene.Move(node, delta);
    }

    // ── Menus and help ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The undo history as a menu: clicking an entry undoes or redoes up to it.</summary>
    public void ShowHistory(float x = 120, float y = 28)
    {
        if (Active is not { } scene)
            return;
        var history = scene.History;
        var items = new List<MenuItem> { MenuItem.Header("Undo history") };
        items.Add(new MenuItem("(scene opened)", "history:0", Css: history.Position == 0 ? "current" : null, Icon: "file"));
        for (var i = 0; i < history.Actions.Count; i++)
        {
            var css = i + 1 == history.Position ? "current" : i + 1 > history.Position ? "undone" : null;
            items.Add(new MenuItem(history.Actions[i].Name, $"history:{i + 1}", Css: css, Icon: HistoryIcon(history.Actions[i])));
        }

        _workspace.Popup.Show(items, x, y, command =>
        {
            if (command.StartsWith("history:", StringComparison.Ordinal) &&
                int.TryParse(command.AsSpan(8), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var position))
                scene.History.GoTo(Math.Clamp(position, 0, scene.History.Actions.Count));
        });
    }

    /// <summary>The items of the menu bar's menu <paramref name="menu"/> in the current <see cref="EditorWorkspace.Mode"/>.</summary>
    public IReadOnlyList<MenuItem> MenuItems(string menu)
    {
        if (menu == "video" || (_workspace.Mode == EditorMode.Video && menu is "file" or "edit" or "view"))
            return VideoMenuItems(menu);
        if (menu == "song" || (_workspace.Mode == EditorMode.Music && menu is "file" or "edit" or "view"))
            return MusicMenuItems(menu);
        var scene = Active;
        var song = Session.ActiveSong;
        var history = scene?.History ?? song?.Document.History;
        var hasSelection = scene?.Selection.Count > 0;
        return menu switch
        {
            "file" =>
            [
                new MenuItem("New Scene", "file.new", "Ctrl+N", Icon: "file-plus"),
                new MenuItem("Open Scene…", "file.open", "Ctrl+O", Icon: "folder-open"),
                new MenuItem("New Song…", "file.new_song", null, Session.ProjectRoot is not null, Icon: "music"),
                MenuItem.Separator,
                new MenuItem("New Project…", "project.new", Icon: "square-plus"),
                new MenuItem("Open Project…", "project.open", Icon: "folder-open"),
                new MenuItem("Project Manager…", "project.manager", Icon: "folders"),
                MenuItem.Separator,
                new MenuItem("Save", "file.save", "Ctrl+S", scene is not null || song is not null, Icon: "device-floppy"),
                new MenuItem("Save As…", "file.save_as", "Ctrl+Shift+S", scene is not null || song is not null, Icon: "file-export"),
                new MenuItem("Export…", "file.export", "Ctrl+Shift+E", _workspace.Project.DesktopProject is not null, Icon: "package-export")
                {
                    Tooltip = _workspace.Project.DesktopProject is null
                        ? "Open a game project with a desktop project to export it."
                        : "Export player builds of the project from its export presets (project.mfproj).",
                },
                MenuItem.Separator,
                new MenuItem("Close Tab", "file.close", "Ctrl+W", Session.ActiveTab is not null, Icon: "x"),
                new MenuItem("Quit", "file.quit", "Ctrl+Q", Icon: "logout"),
            ],
            "edit" =>
            [
                new MenuItem(history?.UndoAction is { } undo ? $"Undo {undo.Name}" : "Undo", "edit.undo", "Ctrl+Z", history?.CanUndo == true, Icon: "arrow-back-up"),
                new MenuItem(history?.RedoAction is { } redo ? $"Redo {redo.Name}" : "Redo", "edit.redo", "Ctrl+Shift+Z", history?.CanRedo == true, Icon: "arrow-forward-up"),
                new MenuItem("Undo History…", "edit.history", null, scene is not null && history?.Actions.Count > 0, Icon: "history"),
                MenuItem.Separator,
                new MenuItem("Add Node…", "node.add", "Ctrl+A", scene is not null, Icon: "circle-plus"),
                new MenuItem("Instance Scene…", "scene.instance", "Ctrl+Shift+A", scene is not null, Icon: "link"),
                new MenuItem("Rename", "edit.rename", "F2", hasSelection, Icon: "pencil"),
                new MenuItem("Duplicate", "edit.duplicate", "Ctrl+D", hasSelection || song?.SelectedClip is not null, Icon: "copy"),
                new MenuItem("Delete", "edit.delete", "Del", hasSelection || song?.SelectedClip is not null, Icon: "trash"),
            ],
            "view" =>
            [
                new MenuItem("Frame Selection", "view.frame", "F", hasSelection, Icon: "focus-centered"),
                new MenuItem("Front View", "view.front", "1", scene is not null, Icon: "square-letter-f"),
                new MenuItem("Right View", "view.right", "3", scene is not null, Icon: "square-letter-r"),
                new MenuItem("Top View", "view.top", "7", scene is not null, Icon: "square-letter-t"),
                new MenuItem("Reset Camera", "view.reset", null, scene is not null, Icon: "refresh"),
                new MenuItem(scene?.Camera.Is2D == true ? "3D View" : "2D View", "view.2d", null, scene is not null,
                    Icon: scene?.Camera.Is2D == true ? "box" : "square"),
                MenuItem.Separator,
                new MenuItem(_workspace.Viewport.GridVisible ? "Hide Grid" : "Show Grid", "view.grid", "G", Icon: "grid-3x3"),
                new MenuItem(_workspace.Viewport.PostPreview ? "Hide Post-Processing" : "Preview Post-Processing", "view.post", null, Icon: "sparkles"),
                new MenuItem(_workspace.Viewport.NavigationVisible ? "Hide Navigation" : "Visible Navigation", "view.navigation", null, Icon: "route"),
                MenuItem.Separator,
                new MenuItem("Switch to Music Mode", "editor.mode.music", Icon: "music"),
                new MenuItem("Switch to Video Mode", "editor.mode.video", Icon: "video"),
            ],
            "project" =>
            [
                new MenuItem("Project Settings…", "project.settings", null, Session.Project is not null, Icon: "settings"),
                new MenuItem("Open User Data Folder", "project.user_data", null, Session.Project is not null, Icon: "folder-user"),
                MenuItem.Separator,
                new MenuItem("Build & Reload Code", "project.build_reload", "Ctrl+Shift+B",
                    _workspace.Project.GameLibraryProject is not null && !_workspace.Project.IsBuilding && !_workspace.Export.IsRunning, Icon: "hammer")
                {
                    Tooltip = _workspace.Export.IsRunning ? ExportService.BusyText : null,
                },
                new MenuItem("Reload Code", "project.reload_code", null, _workspace.Project.IsGameLoaded, Icon: "reload"),
                MenuItem.Separator,
                new MenuItem("Editor Settings…", "editor.settings", Icon: "adjustments"),
                MenuItem.Separator,
                new MenuItem("Close Project", "project.close", null, Session.ProjectRoot is not null, Icon: "folder-x"),
            ],
            "run" =>
            [
                new MenuItem("Play", "play.main", "F5", _workspace.Project.DesktopProject is not null, Icon: "player-play-filled"),
                new MenuItem("Play Scene", "play.scene", "F6", _workspace.Project.DesktopProject is not null && scene is not null, Icon: "movie"),
                new MenuItem(_workspace.Play.RecordingLabel, "play.record", "F10", _workspace.Play.CanToggleRecording, Icon: "point-filled"),
                new MenuItem("Run Another Instance", "play.another", "Shift+F5", _workspace.Project.DesktopProject is not null, Icon: "copy"),
                new MenuItem("Play Instances", "play.instances", "Ctrl+F5", _workspace.Project.DesktopProject is not null && _workspace.Session.Project?.PlayInstances.Count > 0, Icon: "copy"),
                MenuItem.Separator,
                new MenuItem(_workspace.Play.IsPaused ? "Resume" : "Pause", "play.pause", "F7", _workspace.Play.IsPlaying, Icon: "player-pause"),
                new MenuItem("Reload Scene in Game", "play.reload_scene", null, _workspace.Play.IsPlaying, Icon: "refresh"),
                new MenuItem("Stop", "play.stop", "F8", _workspace.Play.IsPlaying, Icon: "player-stop"),
                MenuItem.Separator,
                new MenuItem(_workspace.Simulation.IsActive ? "Stop Simulation" : "Simulate", "simulate.toggle", "Alt+F6", scene is not null,
                    Icon: _workspace.Simulation.IsActive ? "player-stop" : "atom"),
                new MenuItem(_workspace.Simulation.IsPaused ? "Resume Simulation" : "Pause Simulation", "simulate.pause", null, _workspace.Simulation.IsActive,
                    Icon: "player-pause"),
                new MenuItem("Keep Transforms", "simulate.keep", null, _workspace.Simulation.IsActive, Icon: "device-floppy"),
            ],
            "help" =>
            [
                new MenuItem("Documentation", "help.docs", "F1", Icon: "book"),
                new MenuItem("Keyboard Shortcuts", "help.shortcuts", Icon: "keyboard"),
                new MenuItem("Check for Updates…", "help.check_updates", null, _workspace.Updates.IsEnabled, Icon: "refresh"),
                new MenuItem("About Mainframe Editor", "help.about", Icon: "info-circle"),
            ],
            _ => [],
        };
    }

    /// <summary>
    /// Video mode's File, Edit and View menus, and the Video menu. The video commands (<c>video.*</c>) are defined here and
    /// handled by the video tab; items that act on a recording are enabled only while a video tab is active.
    /// </summary>
    private List<MenuItem> VideoMenuItems(string menu)
    {
        var tab = Session.ActiveTab?.Mode == EditorMode.Video;
        return menu switch
        {
            "file" =>
            [
                new MenuItem("Open…", "file.open", "Ctrl+O", Icon: "folder-open"),
                .. RecentVideoItems(),
                MenuItem.Separator,
                new MenuItem("Save", "file.save", "Ctrl+S", tab, Icon: "device-floppy"),
                new MenuItem("Save As…", "file.save_as", "Ctrl+Shift+S", tab, Icon: "device-floppy"),
                MenuItem.Separator,
                new MenuItem("Export Video…", "video.export", "Ctrl+E", tab, Icon: "download"),
                new MenuItem("Export Transcript…", "video.export_transcript", null, tab && Session.ActiveVideo?.Document.Transcript.Count > 0, Icon: "file-text"),
                MenuItem.Separator,
                new MenuItem("Close Tab", "file.close", "Ctrl+W", tab, Icon: "x"),
                new MenuItem("Quit", "file.quit", "Ctrl+Q", Icon: "logout"),
            ],
            "edit" =>
            [
                new MenuItem("Undo", "edit.undo", "Ctrl+Z", tab, Icon: "arrow-back-up"),
                new MenuItem("Redo", "edit.redo", "Ctrl+Shift+Z", tab, Icon: "arrow-forward-up"),
                MenuItem.Separator,
                new MenuItem("Delete Region", "edit.delete", "Del", tab, Icon: "trash"),
                MenuItem.Separator,
                new MenuItem("Video Settings…", "video.settings", Icon: "settings"),
            ],
            "view" =>
            [
                new MenuItem("Show/Hide Transcript", "video.toggle_transcript", null, tab, Icon: "file-text"),
                new MenuItem("Show/Hide Output", "view.output", Icon: "terminal-2"), // a log, not a recording: needs no tab
                MenuItem.Separator,
                new MenuItem("Switch to Game Mode", "editor.mode.game", Icon: "device-gamepad-2"),
                new MenuItem("Switch to Music Mode", "editor.mode.music", Icon: "music"),
            ],
            _ =>
            [
                new MenuItem("Play/Pause", "video.play", "Space", tab, Icon: "player-play"),
                MenuItem.Separator,
                new MenuItem("Transcribe", "video.transcribe", null, tab, Icon: "language"),
                new MenuItem("Remove Filler Words", "video.remove_fillers", null, tab, Icon: "clear-all"),
                new MenuItem("Detect Silence", "video.detect_silence", null, tab, Icon: "volume-off"),
                new MenuItem("Detect Idle Gameplay", "video.detect_idle", null, tab, Icon: "device-gamepad-2"),
            ],
        };
    }

    /// <summary>
    /// Music mode's File, Edit and View menus, and the Song menu. The song commands (<c>song.*</c>) are handled by the song
    /// tab; items that act on a song are enabled only while a song tab is active.
    /// </summary>
    private List<MenuItem> MusicMenuItems(string menu)
    {
        var song = Session.ActiveSong;
        var tab = song is not null;
        var editable = song is { Document.IsReadOnly: false };
        var history = song?.Document.History;
        var hasProject = Session.ProjectRoot is not null;
        var notes = song is { Focus: SongArea.PianoRoll };
        return menu switch
        {
            "file" =>
            [
                new MenuItem("New Song…", "file.new_song", "Ctrl+N", hasProject, Icon: "music"),
                new MenuItem("Open Song…", "file.open", "Ctrl+O", hasProject, Icon: "folder-open"),
                .. RecentSongItems(),
                MenuItem.Separator,
                new MenuItem("Save", "file.save", "Ctrl+S", tab, Icon: "device-floppy"),
                new MenuItem("Save As…", "file.save_as", "Ctrl+Shift+S", tab, Icon: "file-export"),
                MenuItem.Separator,
                new MenuItem("Render…", "song.render", "Ctrl+E", tab, Icon: "download"),
                new MenuItem(BakeLabel(song), "song.bake", "Ctrl+B", song?.Document.Song.Adaptive is not null, Icon: "package"),
                MenuItem.Separator,
                new MenuItem("Project Manager…", "project.manager", Icon: "folders"),
                new MenuItem("Close Tab", "file.close", "Ctrl+W", tab, Icon: "x"),
                new MenuItem("Quit", "file.quit", "Ctrl+Q", Icon: "logout"),
            ],
            "edit" =>
            [
                new MenuItem(history?.UndoAction is { } undo ? $"Undo {undo.Name}" : "Undo", "edit.undo", "Ctrl+Z", history?.CanUndo == true, Icon: "arrow-back-up"),
                new MenuItem(history?.RedoAction is { } redo ? $"Redo {redo.Name}" : "Redo", "edit.redo", "Ctrl+Shift+Z", history?.CanRedo == true, Icon: "arrow-forward-up"),
                MenuItem.Separator,
                new MenuItem("Copy Notes", "song.copy_notes", "Ctrl+C", notes && song!.SelectedNotes.Count > 0, Icon: "copy"),
                new MenuItem("Paste Notes", "song.paste_notes", "Ctrl+V", notes && editable, Icon: "clipboard"),
                new MenuItem("Duplicate", "edit.duplicate", "Ctrl+D", editable && song!.SelectedClip is not null, Icon: "copy"),
                new MenuItem("Delete", "edit.delete", "Del", editable && song!.SelectedClip is not null, Icon: "trash"),
                new MenuItem("Quantize Notes", "song.quantize_notes", "Q", notes && editable, Icon: "magnet"),
                MenuItem.Separator,
                new MenuItem("Music Settings…", "music.settings", Icon: "settings"),
            ],
            "view" =>
            [
                new MenuItem("Piano Roll", "song.piano_roll", "P", tab, Css: song?.Panel == SongBottomPanel.PianoRoll ? "current" : null, Icon: "piano"),
                new MenuItem("Mixer", "song.mixer", "X", tab, Css: song?.Panel == SongBottomPanel.Mixer ? "current" : null, Icon: "adjustments-alt"),
                new MenuItem("Curves", "song.curves", null, song?.Document.Song.Adaptive is not null, Css: song?.Panel == SongBottomPanel.Curves ? "current" : null, Icon: "wave-sine"),
                new MenuItem("Hide Editor", "song.hide_pane", null, tab && song!.Panel != SongBottomPanel.None, Icon: "layout-bottombar"),
                new MenuItem("Browser", "music.browser", "F", Css: _workspace.Layout.Settings is { MusicShowBrowser: true, MusicAdaptiveTab: false } ? "current" : null, Icon: "layout-sidebar-right"),
                new MenuItem("Adaptive", "music.adaptive", "A", Css: _workspace.Layout.Settings is { MusicShowBrowser: true, MusicAdaptiveTab: true } ? "current" : null, Icon: "stack-2"),
                MenuItem.Separator,
                new MenuItem("Show/Hide Output", "view.output", Icon: "terminal-2"),
                MenuItem.Separator,
                new MenuItem("Switch to Game Mode", "editor.mode.game", Icon: "device-gamepad-2"),
                new MenuItem("Switch to Video Mode", "editor.mode.video", Icon: "video"),
            ],
            _ =>
            [
                new MenuItem(song?.Player.IsPlaying == true ? "Stop" : "Play", "song.play", "Space", tab, Icon: song?.Player.IsPlaying == true ? "player-stop" : "player-play"),
                new MenuItem("To Start", "song.rewind", "Home", tab, Icon: "home"),
                new MenuItem(song?.Document.Song.Loop.Enabled == true ? "Loop Off" : "Loop", "song.loop", "L", tab, Icon: "repeat"),
                new MenuItem("Make Adaptive", "song.make_adaptive", null, editable && song?.Document.Song.Adaptive is null, Icon: "stack-2"),
                new MenuItem(song?.Player.IsAdaptive == true ? "Stop Adaptive" : "Play Adaptive", "song.adaptive_play", "Shift+Space",
                    song?.Document.Song.Adaptive is not null, Icon: "player-track-next"),
                new MenuItem("Play Adaptive from Here", "song.adaptive_play_here", "Alt+Shift+Space", song?.Document.Song.Adaptive is not null, Icon: "player-track-next"),
                new MenuItem("Fire Stinger", "song.fire_stinger", "T", song?.Player.IsAdaptive == true, Icon: "bolt"),
                new MenuItem(song?.Midi.IsRecording == true ? "Stop Recording" : "Record", "song.record", "R", tab, Icon: "point-filled"),
                MenuItem.Separator,
                new MenuItem("Add Instrument Track", "song.add_instrument_track", null, editable, Icon: "music"),
                new MenuItem("Add Audio Track", "song.add_audio_track", null, editable, Icon: "file-music"),
                MenuItem.Separator,
                new MenuItem(song?.SelectedTrack?.Mute == true ? "Unmute Track" : "Mute Track", "song.mute_track", "M",
                    editable && song!.SelectedTrack is not null, Icon: "volume-off"),
                new MenuItem(song?.SelectedTrack?.Solo == true ? "Unsolo Track" : "Solo Track", "song.solo_track", "S",
                    editable && song!.SelectedTrack is not null, Icon: "ear"),
            ],
        };
    }

    // The project's songs under a "Songs" header; nothing when the project has none.
    // "Bake Adaptive Music", or the running bake's progress (choosing it cancels).
    private string BakeLabel(SongTab? song) =>
        song is not null && _workspace.SongView.BakeOf(song.Document.FilePath) is { } job
            ? string.Create(CultureInfo.InvariantCulture, $"Cancel Bake ({job.Progress * 100:0} %)")
            : "Bake Adaptive Music";

    private List<MenuItem> RecentSongItems()
    {
        var songs = SongPanel.FindSongs(Session.ProjectRoot, SongPanel.MaxRecentSongs);
        var items = new List<MenuItem>();
        if (songs.Count == 0)
            return items;
        items.Add(MenuItem.Header("Songs"));
        for (var i = 0; i < songs.Count; i++)
            items.Add(new MenuItem(Path.GetFileNameWithoutExtension(songs[i]), RecentSongCommand + i.ToString(CultureInfo.InvariantCulture), Icon: "music"));
        return items;
    }

    // The "Recent Videos" header and the first few recent recordings; nothing when there are none.
    private List<MenuItem> RecentVideoItems()
    {
        var recent = _workspace.Settings.RecentVideos;
        var items = new List<MenuItem>();
        if (recent.Count == 0)
            return items;
        items.Add(MenuItem.Header("Recent Videos"));
        for (var i = 0; i < recent.Count && i < RecentVideoMenuItems; i++)
            items.Add(new MenuItem(Path.GetFileName(recent[i]), RecentVideoCommand + i.ToString(CultureInfo.InvariantCulture), Icon: "video"));
        return items;
    }

    /// <summary>The icon of an undo history entry: what the action did (property icon, add, delete, move …).</summary>
    public static string HistoryIcon(IEditorAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action switch
        {
            SetPropertyAction set => PropertyIcons.SemanticIcon(set.Property.Name) ?? "pencil",
            AddNodeAction add when add.Name.StartsWith("Instance", StringComparison.Ordinal) => "link",
            AddNodeAction add when add.Name.StartsWith("Duplicate", StringComparison.Ordinal) => "copy",
            AddNodeAction => "circle-plus",
            RemoveNodeAction => "trash",
            ReparentAction => "arrows-right-left",
            RenameAction => "pencil",
            MoveInTreeAction => "arrows-up-down",
            _ when action.Name.StartsWith("Delete", StringComparison.Ordinal) => "trash",
            _ when action.Name.StartsWith("Duplicate", StringComparison.Ordinal) => "copy",
            _ when action.Name.StartsWith("Move", StringComparison.Ordinal) => "arrows-move",
            _ when action.Name.StartsWith("Rotate", StringComparison.Ordinal) => "rotate",
            _ when action.Name.StartsWith("Scale", StringComparison.Ordinal) => "arrows-maximize",
            _ => "stack-2",
        };
    }

    private void ShowShortcuts() => _workspace.Message.Show(new MessageRequest
    {
        Title = "Keyboard Shortcuts",
        Message = "Ctrl/Cmd+N new · Ctrl+O open · Ctrl+S save · Ctrl+Shift+S save as · Ctrl+W close\n" +
                  "Ctrl+Z undo · Ctrl+Shift+Z / Ctrl+Y redo · Ctrl+D duplicate · Del delete · F2 rename\n" +
                  "Ctrl+A add node · Ctrl+Shift+A instance scene · Ctrl+Up/Down move in tree\n" +
                  "Q select · W move · E rotate · R scale · T local/global · Y snap · F frame · G grid\n" +
                  "Viewport: click select · RMB+WASD/QE fly (Shift faster) · Alt+LMB or MMB orbit · Shift+MMB pan · wheel zoom\n" +
                  "1 / 3 / 7 front, right, top view · F1 documentation · F12 developer overlay · F9 UI debugger\n" +
                  "Music mode: Space play/stop · R record · L loop · Home to start · M mixer · Del · Ctrl+C/V/D · arrows nudge (Shift: octave) · Q quantize · Ctrl+E render · Ctrl+B bake · Ctrl+wheel zoom\n" +
                  "F5 play · F6 play scene · Shift+F5 another instance · F7 pause · F8 stop · F10 play and record · Alt+F6 simulate · Ctrl+Shift+B build & reload code",
        Buttons = ["Close"],
    });

    private void ShowAbout() => _workspace.Message.Show(new MessageRequest
    {
        Title = "About",
        Message = $"{EditorBrand.NameWithVersion}\n" +
                  "A modular C# game engine and Godot-style editor. Open source under the MIT licence.\n\n" +
                  "Engine: Vulkan 1.2 renderer (HDR, materials, cascaded shadows, glTF/FBX/OBJ import) · " +
                  "Godot-style node tree with .mscene scenes · RmlUi game UI · Jitter2 3D and Box2D 2D physics · " +
                  "SoundFlow audio · multiplayer replication · gettext localization · Spine animation.\n\n" +
                  "Editor: scene tree, inspector and undo · 3D/2D viewport with gizmos · FileSystem panel · " +
                  "Play in a separate process · C# code reload. Its UI is built with the engine's own RmlUi.\n\n" +
                  $"{RuntimeInformation.FrameworkDescription} · " +
                  $"{RuntimeInformation.OSDescription} " +
                  $"({RuntimeInformation.OSArchitecture})\n" +
                  EditorBrand.RepositoryUrl.AbsoluteUri,
        Icon = "info-circle",
        Buttons = ["OK", "GitHub"],
        CancelButton = 0,
        Callback = (button, _) =>
        {
            if (button == 1)
                OsShell.OpenUrl(EditorBrand.RepositoryUrl);
        },
    });
}
