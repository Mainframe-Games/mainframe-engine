using System.Diagnostics;
using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// The song tab's per-frame driver, a <c>[Tool]</c> node of the workspace (the <see cref="ViewportController"/> of song
/// tabs): sizes the active tab's two canvas viewports to the <see cref="SongPanel"/>'s canvas areas, publishes them to
/// the UI as <c>engine://song-arrange</c> and <c>engine://song-roll</c>, routes mouse input over them to the tab's
/// <see cref="SongCanvasController"/> (and FileSystem drops of audio files), keeps the playhead in view while playing,
/// and runs song renders (<see cref="SongRenderJob"/>), reporting them in the Output panel.
/// </summary>
[Tool]
public sealed class SongView : Node
{
    public const string ArrangeTexture = "song-arrange";
    public const string RollTexture = "song-roll";
    private const double DoubleClickSeconds = 0.35;

    private readonly EditorWorkspace _workspace;
    private readonly List<SongRenderJob> _renders = [];
    private SongTab? _tab;
    private SubViewport? _arrangeRegistered;
    private SubViewport? _rollRegistered;
    private SongArea _dragArea;
    private Vector2 _mouse;
    private long _lastClick;
    private Vector2 _lastClickAt;

    public SongView(EditorWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <summary>The active song tab, or null.</summary>
    public SongTab? Tab => _tab;

    /// <summary>Renders in progress.</summary>
    public IReadOnlyList<SongRenderJob> Renders => _renders;

    /// <summary>The render of the song at <paramref name="songPath"/> in progress, or null.</summary>
    public SongRenderJob? RenderOf(string songPath)
    {
        foreach (var job in _renders)
            if (EditorSession.PathsEqual(job.SongPath, Path.GetFullPath(songPath)))
                return job;
        return null;
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        PollRenders();
        var tab = _workspace.Session.ActiveTab as SongTab;
        if (!ReferenceEquals(tab, _tab))
            Switch(tab);
        _workspace.SongPanel.Tick(tab);
        if (tab is null)
            return;
        var controller = tab.Controller;
        controller.Tick(gameTime.DeltaTime);
        var scale = _workspace.Host.PixelScale;
        var arrange = _workspace.SongPanel.ArrangeRect;
        var roll = _workspace.SongPanel.RollRect;
        controller.ArrangeSize = new Vector2(arrange.Width, arrange.Height);
        controller.RollSize = new Vector2(roll.Width, roll.Height);
        var showRoll = tab.Panel == SongBottomPanel.PianoRoll && roll.Width > 1 && roll.Height > 1;
        Place(tab.ArrangeViewport, tab.Arrangement, arrange, scale, arrange.Width > 1 && arrange.Height > 1);
        Place(tab.RollViewport, tab.PianoRoll, roll, scale, showRoll);
        Publish(tab, showRoll);
        Follow(tab);
    }

    private static void Place(SubViewport viewport, SongCanvas canvas, LayoutRect rect, float scale, bool show)
    {
        var mode = show ? SubViewportUpdateMode.Always : SubViewportUpdateMode.Disabled;
        if (viewport.UpdateMode != mode)
            viewport.UpdateMode = mode;
        if (!show)
            return;
        var w = Math.Max(1, (int)MathF.Round(rect.Width * scale));
        var h = Math.Max(1, (int)MathF.Round(rect.Height * scale));
        if (viewport.Width != w || viewport.Height != h)
        {
            viewport.Width = w;
            viewport.Height = h;
        }

        canvas.PixelScale = scale;
        canvas.ViewSize = new Vector2(rect.Width, rect.Height);
        canvas.QueueRedraw();
    }

    private void Publish(SongTab tab, bool showRoll)
    {
        if (Tree?.Servers.Get<UiServer>() is not { } ui)
            return;
        if (!ReferenceEquals(_arrangeRegistered, tab.ArrangeViewport))
        {
            ui.RegisterTexture(ArrangeTexture, tab.ArrangeViewport);
            _arrangeRegistered = tab.ArrangeViewport;
        }

        if (showRoll && !ReferenceEquals(_rollRegistered, tab.RollViewport))
        {
            ui.RegisterTexture(RollTexture, tab.RollViewport);
            _rollRegistered = tab.RollViewport;
        }

        _workspace.SongPanel.SetImages(_arrangeRegistered is not null, _rollRegistered is not null && showRoll);
    }

    // The active tab changed: the previous one stops (EditorSession.Activate) and the UI stops sampling its targets.
    private void Switch(SongTab? tab)
    {
        if (_dragArea != SongArea.None)
            _tab?.Controller.CancelGesture();
        _dragArea = SongArea.None;
        _tab = tab;
        if (_arrangeRegistered is not null || _rollRegistered is not null)
        {
            var ui = Tree?.Servers.Get<UiServer>();
            ui?.UnregisterTexture(ArrangeTexture);
            ui?.UnregisterTexture(RollTexture);
            _arrangeRegistered = null;
            _rollRegistered = null;
        }

        _workspace.SongPanel.SetImages(false, false);
        if (tab is null)
            return;
        tab.Controller.AudioLength = file => AudioLength(tab, file);
        tab.Controller.RequestAudioClip = (track, tick) => PickAudioClip(tab, track, tick);
    }

    private static long? AudioLength(SongTab tab, string file) =>
        SongWaveforms.GetNow(file) is { Seconds: > 0 } wave ? (long)Math.Round(tab.Document.Song.SecondsToTicks(wave.Seconds)) : null;

    private void PickAudioClip(SongTab tab, SongTrack track, long tick)
    {
        var root = _workspace.Session.ProjectRoot ?? Path.GetDirectoryName(tab.FilePath)!;
        var model = new FilePickerModel(FilePickerMode.Open, root, ["*.wav", "*.ogg", "*.mp3", "*.flac"]);
        _workspace.FilePicker.Show(model, "Add Audio Clip", "Add", path =>
        {
            if (!_workspace.Session.Tabs.Contains(tab) || !tab.Document.Song.Tracks.Contains(track))
                return;
            Guard("Could not add the audio clip", () => tab.Controller.AddAudioClip(track, AssetDatabase.Current.ToProjectPath(path), tick));
        });
    }

    // While playing, the views page to keep the playhead visible.
    private static void Follow(SongTab tab)
    {
        if (!tab.Player.IsPlaying)
            return;
        var c = tab.Controller;
        var tick = tab.Player.PositionTicks;
        var x = c.ArrangeX(tick);
        if (x < 0 || x > c.ArrangeSize.X - 8)
            tab.ArrangeScrollTick = Math.Max(0, tick - 8 / tab.ArrangePixelsPerTick);
        if (tab.RollClip is { } clip && tab.Panel == SongBottomPanel.PianoRoll)
        {
            var rx = c.RollX(tick - clip.Start);
            if (tick >= clip.Start && tick < clip.End && (rx < SongCanvasController.KeyboardWidth || rx > c.RollSize.X - 8))
                tab.RollScrollTick = tick - clip.Start - 8 / tab.RollPixelsPerTick;
        }
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────────────────────────

    protected override void OnInput(InputEvent inputEvent)
    {
        if (_tab is not { } tab || _workspace.IsDialogOpen || _workspace.Popup.Visible)
            return;
        var scale = _workspace.Host.PointerScale;
        switch (inputEvent)
        {
            case InputEventMouseButton { Pressed: false } drop when _workspace.FileDrag is { } file && _dragArea == SongArea.None:
                _mouse = drop.Position * scale;
                if (AreaAt(_mouse) == SongArea.Arrangement)
                {
                    _workspace.EndFileDrag();
                    DropFile(tab, file, Local(SongArea.Arrangement, _mouse));
                    Handled();
                }

                break;
            case InputEventMouseButton { Button: MouseButton.Left } button:
                _mouse = button.Position * scale;
                if (button.Pressed)
                {
                    var area = AreaAt(_mouse);
                    if (area == SongArea.None)
                        return;
                    var now = Stopwatch.GetTimestamp();
                    var doubleClick = Stopwatch.GetElapsedTime(_lastClick, now).TotalSeconds < DoubleClickSeconds &&
                                      Vector2.Distance(_mouse, _lastClickAt) < 5;
                    _lastClick = doubleClick ? 0 : now;
                    _lastClickAt = _mouse;
                    _dragArea = area;
                    Guard("Song edit failed", () => tab.Controller.Press(area, Local(area, _mouse), _workspace.Modifiers, doubleClick));
                    Handled();
                }
                else if (_dragArea != SongArea.None)
                {
                    var area = _dragArea;
                    _dragArea = SongArea.None;
                    Guard("Song edit failed", () => tab.Controller.Release(Local(area, _mouse)));
                    Handled();
                }

                break;
            case InputEventMouseMotion motion:
                _mouse = motion.Position * scale;
                if (_dragArea != SongArea.None)
                {
                    var area = _dragArea;
                    Guard("Song edit failed", () => tab.Controller.Drag(Local(area, _mouse), _workspace.Modifiers));
                    Handled();
                }

                break;
            case InputEventMouseWheel wheel:
                var at = AreaAt(_mouse);
                if (at != SongArea.None)
                {
                    tab.Controller.Wheel(at, Local(at, _mouse), wheel.Delta, _workspace.Modifiers);
                    Handled();
                }

                break;
        }
    }

    /// <summary>The song tab's keys (<see cref="SongCanvasController.Key"/>); false when not handled or no song tab is active.</summary>
    public bool HandleKey(Key key, EditorModifiers modifiers)
    {
        if (_workspace.Session.ActiveTab is not SongTab tab)
            return false;
        var handled = false;
        Guard("Song edit failed", () => handled = tab.Controller.Key(key, modifiers));
        return handled;
    }

    private void DropFile(SongTab tab, string file, Vector2 local)
    {
        if (FileKinds.Of(file, isDirectory: false) != FileKind.Audio)
        {
            Log.Info($"[Editor] Only sound files can be dropped on a song: {Path.GetFileName(file)}");
            return;
        }

        Guard($"Could not add {Path.GetFileName(file)}", () => tab.Controller.Drop(AssetDatabase.Current.ToProjectPath(file), local));
    }

    private SongArea AreaAt(Vector2 p)
    {
        if (_workspace.SongPanel.ArrangeRect.Contains(p.X, p.Y))
            return SongArea.Arrangement;
        if (_tab?.Panel == SongBottomPanel.PianoRoll && _workspace.SongPanel.RollRect.Contains(p.X, p.Y))
            return SongArea.PianoRoll;
        return SongArea.None;
    }

    private Vector2 Local(SongArea area, Vector2 p)
    {
        var rect = area == SongArea.Arrangement ? _workspace.SongPanel.ArrangeRect : _workspace.SongPanel.RollRect;
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

    // ── Render ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the song at <paramref name="songPath"/> (an open tab's current state, else the file) in the background;
    /// a second request while it runs cancels it.
    /// </summary>
    public void Render(string songPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(songPath);
        if (RenderOf(songPath) is { } running)
        {
            running.Cancel();
            return;
        }

        Song song;
        var open = _workspace.Session.Tabs.OfType<SongTab>().FirstOrDefault(t => EditorSession.PathsEqual(t.Document.FilePath, Path.GetFullPath(songPath)));
        if (open is not null)
        {
            song = open.Document.Song;
        }
        else
        {
            _workspace.Session.UseProjectOf(songPath);
            song = SongFormat.Load(songPath).Song;
        }

        var job = SongRenderJob.Start(song, songPath, AssetDatabase.Current);
        _renders.Add(job);
        Log.Info($"[Music] Rendering '{job.Name}'…");
    }

    private void PollRenders()
    {
        for (var i = _renders.Count - 1; i >= 0; i--)
        {
            var job = _renders[i];
            if (!job.IsDone)
                continue;
            _renders.RemoveAt(i);
            job.Dispose();
            if (job.Task.IsCanceled || (job.Task.Exception?.InnerException is OperationCanceledException))
            {
                Log.Info($"[Music] Render of '{job.Name}' cancelled; the previous output is unchanged.");
            }
            else if (job.Task.Exception?.InnerException is { } error)
            {
                Log.Error($"[Music] Render of '{job.Name}' failed: {error.Message}");
                _workspace.Commands.ReportError($"Could not render {job.Name}", error);
            }
            else
            {
                _workspace.FileSystem.Rescan();
            }
        }
    }
}
