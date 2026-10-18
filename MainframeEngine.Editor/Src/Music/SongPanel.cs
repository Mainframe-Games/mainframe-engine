using System.Globalization;
using MainframeEngine.Editor.Music;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// Music mode's view (<c>Content/Editor/song.rml</c>), below the tab strip: for the active <see cref="SongTab"/> the
/// track headers (rename, colour, record-arm, mute, solo, instrument, add and remove) above one bottom pane at a time (the
/// layout's <see cref="EditorLayoutSettings.MusicPane"/>: the piano roll, the mixer or none; its splitter sets
/// <see cref="EditorLayoutSettings.MusicPaneHeight"/>), the pane's header (Piano roll | Mixer, Hide) and the mixer strips
/// (instrument slot, inserts placeholder, pan, volume fader, peak meter, mute/solo, master); without one,
/// New Song, Open Song and the project's songs (or, with no project, the Project Manager). The transport is in the
/// toolbar (<see cref="ToolbarPanel.UpdateMusic"/>). The arrangement and the piano roll are images of the tab's canvases
/// (<see cref="SongView"/>). Steady-state frames touch only the meters, and allocate nothing.
/// </summary>
public sealed class SongPanel : EditorDocument
{
    private static readonly string[] Percent = BuildPercent();

    private sealed class RecentSong
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required string Path { get; init; }
    }

    private static readonly RmlStructType<RecentSong> RecentType = new RmlStructType<RecentSong>()
        .Member("index", static r => r.Index)
        .Member("name", static r => r.Name)
        .Member("path", static r => r.Path);

    // One insert slot of a mixer strip (the strip's nested data-for).
    private sealed class SlotRow
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required bool Bypass { get; init; }
        public required bool Missing { get; init; }
        public required string Tooltip { get; init; }
    }

    private static readonly RmlStructType<SlotRow> SlotType = new RmlStructType<SlotRow>()
        .Member("index", static r => r.Index)
        .Member("name", static r => r.Name)
        .Member("bypass", static r => r.Bypass)
        .Member("missing", static r => r.Missing)
        .Member("tooltip", static r => r.Tooltip);

    private sealed class Row
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required string Color { get; init; }
        public required string Instrument { get; init; }
        public required List<SlotRow> Slots { get; init; }
        public required bool Audio { get; init; }
        public required bool Mute { get; init; }
        public required bool Solo { get; init; }
        public required bool Armed { get; init; }
        public required bool Selected { get; init; }
        public required string Status { get; init; }
        public required string Fader { get; init; }
        public required string Db { get; init; }
        public required string Pan { get; init; }
        public required string PanText { get; init; }
        public required string Tooltip { get; init; }
    }

    private static readonly RmlStructType<Row> RowType = new RmlStructType<Row>()
        .Member("index", static r => r.Index)
        .Member("name", static r => r.Name)
        .Member("color", static r => r.Color)
        .Member("instrument", static r => r.Instrument)
        .Member("audio", static r => r.Audio)
        .Member("mute", static r => r.Mute)
        .Member("solo", static r => r.Solo)
        .Member("armed", static r => r.Armed)
        .Member("selected", static r => r.Selected)
        .Member("status", static r => r.Status)
        .Member("fader", static r => r.Fader)
        .Member("db", static r => r.Db)
        .Member("pan", static r => r.Pan)
        .Member("pan_text", static r => r.PanText)
        .Member("tooltip", static r => r.Tooltip)
        .List("slots", static r => r.Slots, SlotType);

    // The Browser's drag starts once the mouse moved this far (dp) from the press.
    private const float DragThreshold = 4;

    private readonly List<Row> _rows = [];
    private readonly List<SlotRow> _masterSlots = [];
    private readonly List<string> _meterIds = [];
    private int[] _meterShown = [];
    private float[] _meterLevel = [];
    private float _masterLevel;
    private int _masterShown = -1;
    private RmlDataModel? _model;
    private SongTab? _tab;
    private int _docVersion = -1;
    private int _viewVersion = -1;
    private float _shownScroll = float.NaN;
    private bool _arrangeImage;
    private bool _rollImage;
    private RmlEventListener?[] _listeners = new RmlEventListener?[13];
    private float _appliedBrowserWidth = float.NaN;
    private bool _browserOpen;
    private int _pressedRow = -1;
    private (float X, float Y) _pressedAt;
    private int _dragUpFrames;
    private float _appliedPaneHeight = float.NaN;
    private readonly List<RecentSong> _recent = [];
    private string? _recentRoot;
    private bool _recentStale = true;

    // Bound values.
    private bool _hasTab;
    private bool _hasProject;
    private bool _readOnly;
    private string _notice = "";
    private int _midiVersion = -1;
    private bool _panelRoll = true;
    private bool _panelMixer;
    private bool _paneOpen = true;
    private string _clipInfo = "";
    private string _masterFader = "0";
    private string _masterDb = "0.0 dB";

    public SongPanel(EditorWorkspace workspace)
        : base(workspace, "song.rml")
    {
        Visible = false;
        Browser = new SongBrowser(workspace);
    }

    /// <summary>Music mode's Browser (the right column): songs and plugins.</summary>
    public SongBrowser Browser { get; }

    /// <summary>The arrangement canvas area in window dp (empty while hidden).</summary>
    public LayoutRect ArrangeRect { get; private set; }

    /// <summary>The piano-roll canvas area in window dp (empty while the mixer or no pane shows).</summary>
    public LayoutRect RollRect { get; private set; }

    protected override void OnReady()
    {
        _model = CreateDataModel("song")
            .BindList("rows", _rows, RowType)
            .BindList("recent", _recent, RecentType)
            .Bind("has_tab", this, static p => p._hasTab)
            .Bind("has_project", this, static p => p._hasProject)
            .Bind("readonly", this, static p => p._readOnly)
            .Bind("notice", this, static p => p._notice)
            .Bind("panel_roll", this, static p => p._panelRoll)
            .Bind("panel_mixer", this, static p => p._panelMixer)
            .Bind("pane_open", this, static p => p._paneOpen)
            .Bind("clip_info", this, static p => p._clipInfo)
            .Bind("master_fader", this, static p => p._masterFader)
            .Bind("master_db", this, static p => p._masterDb)
            .BindList("master_slots", _masterSlots, SlotType)
            .BindList("browser", Browser.Rows, SongBrowser.RowType)
            .Bind("browser_open", this, static p => p._browserOpen)
            .Event("fx_open", e => OpenSlot(e.GetArgument(0).GetInt32(), e.GetArgument(1).GetInt32()))
            .Event("fx_bypass", e => Slot(e.GetArgument(0).GetInt32(), e.GetArgument(1).GetInt32(),
                (tab, _, insert) => tab.Document.SetInsertBypass(insert, !insert.Bypass)))
            .Event("fx_add", e => AddEffectMenu(e.GetArgument(0).GetInt32(), Below(e.Event.CurrentElement)))
            .Event("fx_menu", e =>
            {
                if (e.Event.GetParameter("button", 0) == 1) // right-click
                    SlotMenu(e.GetArgument(0).GetInt32(), e.GetArgument(1).GetInt32(), Below(e.Event.CurrentElement));
            })
            .Event("arm", e => Track(e.GetArgument(0).GetInt32(), (t, track) => t.Midi.ToggleArm(track)))
            .Event("add_track", e => AddTrackMenu(Below(e.Event.CurrentElement)))
            .Event("select", e => SelectTrack(e.GetArgument(0).GetInt32()))
            .Event("rename", e => Rename(e.GetArgument(0).GetInt32()))
            .Event("mute", e => Track(e.GetArgument(0).GetInt32(), (t, track) => t.Document.SetTrackMute(track, !track.Mute)))
            .Event("solo", e => Track(e.GetArgument(0).GetInt32(), (t, track) => t.Document.SetTrackSolo(track, !track.Solo)))
            .Event("color", e =>
            {
                var index = e.GetArgument(0).GetInt32();
                ColorMenu(index, Below(e.Event.CurrentElement));
            })
            .Event("remove", e => Track(e.GetArgument(0).GetInt32(), (t, track) => t.Document.RemoveTrack(track)))
            .Event("instrument", e =>
            {
                var index = e.GetArgument(0).GetInt32();
                InstrumentMenu(index, Below(e.Event.CurrentElement));
            })
            .Event("edit_line", e => EditLine(e.GetArgument(0).GetInt32()))
            .Event("audition", e => Track(e.GetArgument(0).GetInt32(), (t, track) => t.Controller.AuditionBriefly(track)))
            .Event("panel", e => Workspace.SetMusicPane(e.GetArgument(0).GetInt32() == 1 ? SongBottomPanel.Mixer : SongBottomPanel.PianoRoll));
    }

    protected override void OnAttach(RmlDocument document)
    {
        foreach (var listener in _listeners)
            listener?.Remove();
        var body = document.AsElement();
        _listeners[0] = body.AddEventListener("change", OnChange);
        _listeners[1] = body.AddEventListener("mouseup", _ => _tab?.Document.History.EndMerge());
        var splitter = document.GetElementById("song-split-pane");
        _listeners[2] = splitter.AddEventListener("drag", e => DragPaneSplitter((float)(e.GetParameter("mouse_y", 0.0) / MathF.Max(0.01f, Workspace.Host.PixelScale))));
        _listeners[3] = splitter.AddEventListener("dragstart", e => e.CurrentElement.SetClass("dragging", true));
        _listeners[4] = splitter.AddEventListener("dragend", e =>
        {
            e.CurrentElement.SetClass("dragging", false);
            Workspace.SaveLayout();
        });
        var browserSplitter = document.GetElementById("song-split-browser");
        _listeners[5] = browserSplitter.AddEventListener("drag", e => DragBrowserSplitter((float)(e.GetParameter("mouse_x", 0.0) / MathF.Max(0.01f, Workspace.Host.PixelScale))));
        _listeners[6] = browserSplitter.AddEventListener("dragstart", e => e.CurrentElement.SetClass("dragging", true));
        _listeners[7] = browserSplitter.AddEventListener("dragend", e =>
        {
            e.CurrentElement.SetClass("dragging", false);
            Workspace.SaveLayout();
        });
        _listeners[8] = body.AddEventListener("click", OnBrowserClick);
        _listeners[9] = body.AddEventListener("dblclick", OnBrowserDoubleClick);
        _listeners[10] = body.AddEventListener("mousedown", OnBrowserPress);
        _listeners[11] = body.AddEventListener("mousemove", OnBrowserMove);
        _listeners[12] = body.AddEventListener("mouseup", OnBrowserRelease);
        _appliedBrowserWidth = float.NaN;
        _appliedPaneHeight = float.NaN;
        _arrangeImage = false;
        _rollImage = false;
        _docVersion = -1;
        _shownScroll = float.NaN;
    }

    /// <summary>Shows the canvases' images once their textures are published.</summary>
    public void SetImages(bool arrange, bool roll)
    {
        if (!IsLoaded)
            return;
        if (arrange != _arrangeImage)
        {
            _arrangeImage = arrange;
            SetImage("song-arrange-image", arrange ? "engine://" + SongView.ArrangeTexture : null);
        }

        if (roll != _rollImage)
        {
            _rollImage = roll;
            SetImage("song-roll-image", roll ? "engine://" + SongView.RollTexture : null);
        }
    }

    private void SetImage(string id, string? source)
    {
        var image = Document.GetElementById(id);
        if (image.IsNull)
            return;
        if (source is null)
            image.RemoveAttribute("src");
        else
            image.SetAttribute("src", source);
    }

    /// <summary>The project's songs the empty state lists are read again the next time it shows (a song was created or saved).</summary>
    public void InvalidateRecent()
    {
        _recentStale = true;
        Browser.Invalidate();
    }

    /// <summary>The songs the empty state lists (newest first), for tests and the File menu.</summary>
    public IReadOnlyList<string> RecentSongs => [.. _recent.Select(static r => r.Path)];

    /// <summary>Every frame in music mode (from the <see cref="SongView"/>): follows the active song tab, else shows the empty state.</summary>
    public void Tick(SongTab? tab)
    {
        if (!ReferenceEquals(tab, _tab))
        {
            _tab = tab;
            _docVersion = -1;
            _viewVersion = -1;
            if (tab is null)
                _recentStale = true; // the last song may have just been saved or created
        }

        if (!IsLoaded)
            return;
        TickBrowser(tab);
        if (_hasTab != tab is not null)
        {
            _hasTab = tab is not null;
            _model?.Dirty("has_tab");
        }

        if (tab is null)
        {
            ArrangeRect = default;
            RollRect = default;
            UpdateEmpty();
            return;
        }

        ApplyPaneHeight();
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        ArrangeRect = RectOf("song-arrange", scale);
        RollRect = tab.Panel == SongBottomPanel.PianoRoll ? RectOf("song-roll", scale) : default;

        if (tab.Document.Version != _docVersion || tab.ViewVersion != _viewVersion || tab.Midi.Version != _midiVersion)
            Rebuild(tab);
        UpdateMeters(tab);
        UpdateHeaderScroll(tab);
    }

    /// <summary>
    /// The bottom pane's splitter moved to <paramref name="y"/> (window dp, its centre): the pane takes the height below its
    /// header, clamped by <see cref="EditorLayout.SetMusicPaneHeight"/> (saved when the drag ends).
    /// </summary>
    internal void DragPaneSplitter(float y)
    {
        if (!IsLoaded || _tab is null)
            return;
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var bottom = RectOf("song-bottom", scale);
        if (bottom.Height <= 0)
            return;
        var header = RectOf("song-switcher", scale);
        Workspace.Layout.SetMusicPaneHeight(bottom.Bottom - y - EditorLayout.SplitterSize * 0.5f - header.Height);
        ApplyPaneHeight();
    }

    // The bottom pane's height, from the layout (splitter drags, a loaded layout) within the window's limits.
    private void ApplyPaneHeight()
    {
        var height = Workspace.Layout.MusicPaneShownHeight;
        if (height == _appliedPaneHeight || Document.GetElementById("song-bottom") is not { IsNull: false } pane)
            return;
        _appliedPaneHeight = height;
        pane.SetProperty("height", RmlText.Dp(height));
    }

    private LayoutRect RectOf(string id, float scale)
    {
        var element = Document.GetElementById(id);
        if (element.IsNull)
            return default;
        var b = element.Bounds;
        return new LayoutRect(b.X / scale, b.Y / scale, b.Width / scale, b.Height / scale);
    }

    private void Rebuild(SongTab tab)
    {
        _docVersion = tab.Document.Version;
        _viewVersion = tab.ViewVersion;
        _midiVersion = tab.Midi.Version;
        var armed = tab.Midi.ArmedTrackId;
        var song = tab.Document.Song;
        _rows.Clear();
        _meterIds.Clear();
        for (var i = 0; i < song.Tracks.Count; i++)
        {
            var track = song.Tracks[i];
            var status = tab.Player.GetTrackStatus(track.Id) ?? "";
            _rows.Add(new Row
            {
                Index = i,
                Name = track.Name,
                Color = track.Color ?? SongCanvas.Palette[i % SongCanvas.Palette.Length],
                Instrument = track.Kind == SongTrackKind.Audio ? "Audio" : track.Instrument is { IsZzfx: true } ? "Built-in ZzFX" : track.Instrument?.DisplayName ?? "No instrument",
                Slots = Slots(tab, track.Inserts),
                Audio = track.Kind == SongTrackKind.Audio,
                Mute = track.Mute,
                Solo = track.Solo,
                Armed = track.Id == armed,
                Selected = ReferenceEquals(track, tab.SelectedTrack),
                Status = status,
                Fader = Number(-Math.Clamp(track.VolumeDb, -60f, 6f)),
                Db = DbText(track.VolumeDb),
                Pan = Number(track.Pan),
                PanText = PanText(track.Pan),
                Tooltip = $"{track.Name} — {(track.Kind == SongTrackKind.Audio ? "audio track" : "instrument track")}; click to select, double-click to rename",
            });
            _meterIds.Add("meter-" + i.ToString(CultureInfo.InvariantCulture));
        }

        if (_meterShown.Length != _rows.Count)
        {
            _meterShown = new int[_rows.Count];
            _meterLevel = new float[_rows.Count];
        }

        Array.Fill(_meterShown, -1);
        _masterShown = -1;
        _readOnly = tab.Document.IsReadOnly;
        _notice = tab.Document.ReadOnlyNotice ?? "";
        _panelRoll = tab.Panel == SongBottomPanel.PianoRoll;
        _panelMixer = tab.Panel == SongBottomPanel.Mixer;
        _paneOpen = tab.Panel != SongBottomPanel.None;
        _clipInfo = ClipInfo(tab);
        _masterFader = Number(-Math.Clamp(song.Master.VolumeDb, -60f, 6f));
        _masterDb = DbText(song.Master.VolumeDb);
        _masterSlots.Clear();
        _masterSlots.AddRange(Slots(tab, song.Master.Inserts));
        _model?.DirtyAll();
    }

    private static string ClipInfo(SongTab tab)
    {
        if (tab.SelectedClip is not { } clip || tab.SelectedTrack is not { } track)
            return tab.SelectedTrack is { } t ? t.Name : "";
        var bar = tab.Document.Song.TicksPerBar;
        var first = clip.Start / bar + 1;
        var last = (clip.End - 1) / bar + 1;
        var bars = first == last ? $"bar {first}" : $"bars {first}–{last}";
        return clip is MidiClip midi
            ? $"{track.Name} · {bars} · {midi.Notes.Count} notes{(tab.SelectedNotes.Count > 0 ? $" ({tab.SelectedNotes.Count} selected)" : "")}"
            : $"{track.Name} · {bars} · {Path.GetFileName(((AudioClip)clip).File)}";
    }

    // The empty state: whether a project is open and its songs, read again when the project changed or a song was made.
    private void UpdateEmpty()
    {
        var root = Workspace.Session.ProjectRoot;
        var hasProject = root is not null;
        if (_hasProject != hasProject)
        {
            _hasProject = hasProject;
            _model?.Dirty("has_project");
        }

        if (!_recentStale && string.Equals(root, _recentRoot, StringComparison.Ordinal))
            return;
        _recentStale = false;
        _recentRoot = root;
        _recent.Clear();
        var songs = FindSongs(root, MaxRecentSongs);
        for (var i = 0; i < songs.Count; i++)
            _recent.Add(new RecentSong { Index = i, Name = Path.GetFileNameWithoutExtension(songs[i]), Path = songs[i] });
        _model?.Dirty("recent");
    }

    /// <summary>How many of the project's songs the empty state and File › Recent Songs list.</summary>
    public const int MaxRecentSongs = 8;

    /// <summary>
    /// The project's songs (<c>.msong</c> under <paramref name="projectRoot"/>'s <c>Content/</c>), most recently changed
    /// first, at most <paramref name="max"/>; none without a project or when the folder cannot be read.
    /// </summary>
    public static List<string> FindSongs(string? projectRoot, int max)
    {
        var content = projectRoot is null ? null : Path.Combine(projectRoot, "Content");
        if (content is null || !Directory.Exists(content))
            return [];
        try
        {
            return [.. Directory.EnumerateFiles(content, "*.msong", SearchOption.AllDirectories)
                .Select(static f => new FileInfo(f))
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .ThenBy(static f => f.FullName, StringComparer.Ordinal)
                .Take(max)
                .Select(static f => f.FullName)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Editor] Could not list the project's songs: {e.Message}");
            return [];
        }
    }

    // Peak meters: −60…+6 dB, falling back at about 24 dB/s; only changed heights touch the DOM.
    private void UpdateMeters(SongTab tab)
    {
        if (tab.Panel != SongBottomPanel.Mixer)
            return;
        var tracks = tab.Document.Song.Tracks;
        for (var i = 0; i < _meterIds.Count && i < tracks.Count && i < _meterLevel.Length; i++)
        {
            _meterLevel[i] = Fall(_meterLevel[i], tab.Player.TakeTrackPeak(tracks[i].Id));
            var pct = PercentOf(_meterLevel[i]);
            if (pct == _meterShown[i])
                continue;
            _meterShown[i] = pct;
            var element = Document.GetElementById(_meterIds[i]);
            if (!element.IsNull)
                element.SetProperty("height", Percent[pct]);
        }

        _masterLevel = Fall(_masterLevel, tab.Player.TakeMasterPeak());
        var master = PercentOf(_masterLevel);
        if (master != _masterShown)
        {
            _masterShown = master;
            var element = Document.GetElementById("meter-master");
            if (!element.IsNull)
                element.SetProperty("height", Percent[master]);
        }
    }

    private static float Fall(float shown, float peak) => MathF.Max(peak, shown * 0.92f);

    private static int PercentOf(float linear)
    {
        if (linear <= 1e-6f)
            return 0;
        var db = 20 * MathF.Log10(linear);
        return Math.Clamp((int)MathF.Round((db + 60) / 66 * 100), 0, 100);
    }

    private void UpdateHeaderScroll(SongTab tab)
    {
        if (tab.ArrangeScrollY == _shownScroll)
            return;
        _shownScroll = tab.ArrangeScrollY;
        var list = Document.GetElementById("song-header-list");
        if (!list.IsNull)
            list.SetProperty("top", (-tab.ArrangeScrollY).ToString("0.#", CultureInfo.InvariantCulture) + "dp");
    }

    // ── Edits ────────────────────────────────────────────────────────────────────────────────────────────────────

    private void OnChange(RmlEvent e)
    {
        if (e.Target.Id == "song-browser-search")
        {
            Browser.Search = e.Value;
            return;
        }

        if (_tab is not { } tab || tab.Document.IsReadOnly)
            return;
        var target = e.Target;
        var value = e.Value;
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return;
        var song = tab.Document.Song;
        if (target.HasAttribute("data-master"))
        {
            var db = -number;
            if (MathF.Abs(db - song.Master.VolumeDb) > 0.01f)
                Guard("Could not set the volume", () => tab.Document.SetMasterVolume(db <= -60 ? AudioMath.SilenceDb : db, "song-master"));
        }
        else if (target.GetAttribute("data-vol") is { } vol && TrackAt(tab, vol) is { } volTrack)
        {
            var db = -number;
            if (MathF.Abs(db - volTrack.VolumeDb) > 0.01f && !(db <= -60 && volTrack.VolumeDb <= -60))
                Guard("Could not set the volume", () => tab.Document.SetTrackVolume(volTrack, db <= -60 ? AudioMath.SilenceDb : db, "song-vol-" + volTrack.Id));
        }
        else if (target.GetAttribute("data-pan") is { } pan && TrackAt(tab, pan) is { } panTrack)
        {
            if (MathF.Abs(number - panTrack.Pan) > 0.001f)
                Guard("Could not set the pan", () => tab.Document.SetTrackPan(panTrack, Math.Clamp(number, -1, 1), "song-pan-" + panTrack.Id));
        }
    }

    private static SongTrack? TrackAt(SongTab tab, string index) =>
        int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && (uint)i < (uint)tab.Document.Song.Tracks.Count
            ? tab.Document.Song.Tracks[i]
            : null;

    private void Song(Action<SongTab> action)
    {
        if (_tab is { } tab)
            Guard("Song edit failed", () => action(tab));
    }

    /// <summary>Song › Add Instrument/Audio Track: a new track of <paramref name="kind"/>, selected.</summary>
    public void AddTrack(SongTrackKind kind) => Song(tab =>
    {
        if (tab.Document.IsReadOnly)
            return;
        var track = tab.Document.AddTrack(kind);
        tab.SelectClip(track, null);
    });

    private void Track(int index, Action<SongTab, SongTrack> action)
    {
        if (_tab is { } tab && (uint)index < (uint)tab.Document.Song.Tracks.Count)
        {
            var track = tab.Document.Song.Tracks[index];
            Guard("Song edit failed", () => action(tab, track));
        }
    }

    private void SelectTrack(int index) => Track(index, (tab, track) =>
    {
        if (ReferenceEquals(tab.SelectedTrack, track))
            return;
        tab.SelectClip(track, track.Clips.Count > 0 ? track.Clips[0] : null);
    });

    private void Rename(int index) => Track(index, (tab, track) => Workspace.Message.Show(new MessageRequest
    {
        Title = "Rename Track",
        Message = $"New name for {track.Name}:",
        Input = track.Name,
        Buttons = ["Rename", "Cancel"],
        DefaultButton = 0,
        CancelButton = 1,
        Callback = (button, text) =>
        {
            if (button == 0 && !string.IsNullOrWhiteSpace(text) && tab.Document.Song.Tracks.Contains(track))
                Guard("Could not rename the track", () => tab.Document.RenameTrack(track, text.Trim()));
        },
    }));

    private static readonly (string Name, string Hex)[] Colors =
    [
        ("Cyan", "#22d3ee"), ("Violet", "#a78bfa"), ("Amber", "#f59e0b"), ("Teal", "#5eead4"),
        ("Pink", "#f472b6"), ("Lime", "#a3e635"), ("Blue", "#60a5fa"), ("Orange", "#fb923c"),
    ];

    private void ColorMenu(int index, (float X, float Y) at)
    {
        if (_tab is not { } tab || (uint)index >= (uint)tab.Document.Song.Tracks.Count)
            return;
        var track = tab.Document.Song.Tracks[index];
        var items = new List<MenuItem> { MenuItem.Header("Track colour") };
        foreach (var (name, hex) in Colors)
            items.Add(new MenuItem(name, "color:" + hex, Css: string.Equals(track.Color, hex, StringComparison.OrdinalIgnoreCase) ? "current" : null, Icon: "palette"));
        items.Add(new MenuItem("Automatic", "color:", Css: track.Color is null ? "current" : null, Icon: "refresh"));
        Workspace.Popup.Show(items, at.X, at.Y, command =>
        {
            var hex = command["color:".Length..];
            if (tab.Document.Song.Tracks.Contains(track))
                Guard("Could not set the colour", () => tab.Document.SetTrackColor(track, hex.Length == 0 ? null : hex));
        });
    }

    private void InstrumentMenu(int index, (float X, float Y) at)
    {
        if (_tab is not { } tab || (uint)index >= (uint)tab.Document.Song.Tracks.Count)
            return;
        var track = tab.Document.Song.Tracks[index];
        var catalog = PluginCatalog.Current;
        var current = track.Instrument?.Plugin?.ClassId;
        var items = new List<MenuItem>
        {
            MenuItem.Header("Instrument"),
            new(SynthParams.ProductName, "wavetable", Css: track.Instrument?.IsWavetable == true ? "current" : null, Icon: "wave-square"),
            new("Built-in ZzFX", "zzfx", Css: track.Instrument?.IsZzfx == true ? "current" : null, Icon: "wave-sine"),
            new(track.Instrument?.IsWavetable == true ? "Open Synth…" : "Edit Sound…", "edit",
                Enabled: track.Instrument?.IsZzfx == true || track.Instrument?.IsWavetable == true, Icon: "pencil"),
            MenuItem.Separator,
            MenuItem.Header("VST3 instruments"),
        };
        foreach (var plugin in catalog.Instruments)
        {
            items.Add(new MenuItem(plugin.DisplayName, "vst:" + plugin.ClassId,
                Css: string.Equals(current, plugin.ClassId, StringComparison.OrdinalIgnoreCase) ? "current" : null, Icon: "plug"));
        }

        if (!catalog.Instruments.Any())
            items.Add(new MenuItem(PluginHostClient.IsAvailable ? "None found — Editor Settings › Rescan Plugins" : "The plugin host is not available", null, Enabled: false, Icon: "plug-x"));
        if (track.Instrument?.Plugin is not null)
        {
            items.Add(MenuItem.Separator);
            items.Add(new MenuItem("Open Plugin Editor", "open", Icon: "app-window"));
        }

        EnsureScanned();
        Workspace.Popup.Show(items, at.X, at.Y, command =>
        {
            if (!tab.Document.Song.Tracks.Contains(track))
                return;
            if (command == "wavetable" && track.Instrument?.IsWavetable != true)
            {
                Guard("Could not set the instrument", () =>
                {
                    tab.Document.SetTrackInstrument(track, InstrumentDescriptor.Wavetable());
                    tab.Player.Update(tab.Document);
                    Workspace.PluginWindow.OpenSynth(tab, track);
                });
            }
            else if (command == "zzfx" && track.Instrument?.IsZzfx != true)
                Guard("Could not set the instrument", () => tab.Document.SetTrackInstrument(track, InstrumentDescriptor.Zzfx(SongDocument.DefaultZzfxLine)));
            else if (command == "edit")
                EditLine(index);
            else if (command == "open" && track.Instrument is { } instrument)
                OpenPluginEditor(tab, instrument);
            else if (command?.StartsWith("vst:", StringComparison.Ordinal) == true && catalog.Find(command[4..]) is { } plugin &&
                     !string.Equals(current, plugin.ClassId, StringComparison.OrdinalIgnoreCase))
                Guard("Could not set the instrument", () => tab.Document.SetTrackInstrument(track, new InstrumentDescriptor { Plugin = plugin.ToDescriptor() }));
        });
    }

    // A strip's insert slots: name (a missing plugin or unknown effect marked), bypass and a tooltip.
    private static List<SlotRow> Slots(SongTab tab, List<PluginInsert> inserts)
    {
        var slots = new List<SlotRow>(inserts.Count);
        for (var i = 0; i < inserts.Count; i++)
        {
            var insert = inserts[i];
            var missing = insert.IsBuiltin
                ? BuiltinEffects.Find(insert.Builtin!) is null
                : insert.Plugin is { } p && (p.ClassId is null || PluginCatalog.Current.Find(p.ClassId) is null);
            var name = InsertName(insert);
            slots.Add(new SlotRow
            {
                Index = i,
                Name = name,
                Bypass = insert.Bypass,
                Missing = missing,
                Tooltip = (missing ? $"{name} — not available on this computer" : insert.IsBuiltin ? $"{name} — click to open its window" : $"{name} — click to open the plugin's editor") +
                          "; right-click: bypass, move, replace or remove",
            });
        }

        return slots;
    }

    // The insert at slot `slot` of track `index` (−1: the master).
    private void Slot(int index, int slot, Action<SongTab, SongTrack?, PluginInsert> action)
    {
        if (_tab is not { } tab || index >= tab.Document.Song.Tracks.Count)
            return;
        var track = index >= 0 ? tab.Document.Song.Tracks[index] : null;
        var inserts = track?.Inserts ?? tab.Document.Song.Master.Inserts;
        if ((uint)slot < (uint)inserts.Count)
            Guard("Could not change the insert", () => action(tab, track, inserts[slot]));
    }

    /// <summary>A slot's click: a built-in effect's window, or a VST3 plugin's editor.</summary>
    private void OpenSlot(int index, int slot) => Slot(index, slot, (tab, track, insert) =>
    {
        if (insert.IsBuiltin)
            Workspace.PluginWindow.Open(tab, track, insert);
        else
            OpenPluginEditor(tab, insert);
    });

    // The empty slot: the built-in effects by category, then the scanned VST3 effects.
    private void AddEffectMenu(int index, (float X, float Y) at)
    {
        if (_tab is not { } tab || index >= tab.Document.Song.Tracks.Count || tab.Document.IsReadOnly)
            return;
        var track = index >= 0 ? tab.Document.Song.Tracks[index] : null;
        var catalog = PluginCatalog.Current;
        var items = new List<MenuItem>();
        foreach (var category in Enum.GetValues<BuiltinCategory>())
        {
            items.Add(MenuItem.Header(BuiltinEffects.CategoryName(category)));
            foreach (var info in BuiltinEffects.InCategory(category))
                items.Add(new MenuItem(info.Name, "fx:" + info.Id, Icon: info.Icon));
        }

        items.Add(MenuItem.Header("VST3 effects"));
        foreach (var plugin in catalog.Effects)
            items.Add(new MenuItem(plugin.DisplayName, "vst:" + plugin.ClassId, Icon: "plug"));
        if (!catalog.Effects.Any())
            items.Add(new MenuItem(PluginHostClient.IsAvailable ? "None found — Editor Settings › Rescan Plugins" : "The plugin host is not available", null, Enabled: false, Icon: "plug-x"));

        EnsureScanned();
        Workspace.Popup.Show(items, at.X, at.Y, command =>
        {
            if (command is not null && (track is null || tab.Document.Song.Tracks.Contains(track)))
                Browser.Apply(tab, command, track, null);
        });
    }

    // A slot's right-click menu: open, bypass, move, replace (the add menu's effects) or remove.
    private void SlotMenu(int index, int slot, (float X, float Y) at)
    {
        if (_tab is not { } tab || index >= tab.Document.Song.Tracks.Count)
            return;
        var track = index >= 0 ? tab.Document.Song.Tracks[index] : null;
        var inserts = track?.Inserts ?? tab.Document.Song.Master.Inserts;
        if ((uint)slot >= (uint)inserts.Count)
            return;
        var insert = inserts[slot];
        var editable = !tab.Document.IsReadOnly;
        var items = new List<MenuItem>
        {
            MenuItem.Header($"{slot + 1}. {InsertName(insert)}"),
            new(insert.IsBuiltin ? "Open" : "Open Plugin Editor", "open", Icon: "app-window"),
            new(insert.Bypass ? "Enable" : "Bypass", "bypass", Enabled: editable, Icon: "power"),
            new("Move Up", "up", Enabled: editable && slot > 0, Icon: "arrow-up"),
            new("Move Down", "down", Enabled: editable && slot < inserts.Count - 1, Icon: "arrow-down"),
            new("Remove", "remove", Enabled: editable, Icon: "trash"),
            MenuItem.Header("Replace with"),
        };
        foreach (var info in BuiltinEffects.All)
            items.Add(new MenuItem(info.Name, "fx:" + info.Id, Enabled: editable, Icon: info.Icon));
        foreach (var plugin in PluginCatalog.Current.Effects)
            items.Add(new MenuItem(plugin.DisplayName, "vst:" + plugin.ClassId, Enabled: editable, Icon: "plug"));

        Workspace.Popup.Show(items, at.X, at.Y, command =>
        {
            if (command is null || !inserts.Contains(insert) || (track is not null && !tab.Document.Song.Tracks.Contains(track)))
                return;
            var i = inserts.IndexOf(insert);
            switch (command)
            {
                case "open":
                    OpenSlot(index, i);
                    break;
                case "bypass":
                    Guard("Could not bypass the insert", () => tab.Document.SetInsertBypass(insert, !insert.Bypass));
                    break;
                case "up":
                    Guard("Could not move the insert", () => tab.Document.MoveInsert(track, insert, i - 1));
                    break;
                case "down":
                    Guard("Could not move the insert", () => tab.Document.MoveInsert(track, insert, i + 1));
                    break;
                case "remove":
                    Guard("Could not remove the insert", () => tab.Document.RemoveInsert(track, insert));
                    break;
                default:
                    Browser.Apply(tab, command, track, insert);
                    break;
            }
        });
    }

    // ── The Browser ──────────────────────────────────────────────────────────────────────────────────────────────

    // Every frame in music mode: shows or hides it, its width, its rows (rebuilt on change), and a plugin drag's ghost.
    private void TickBrowser(SongTab? tab)
    {
        var open = Workspace.Layout.Settings.MusicShowBrowser;
        if (open != _browserOpen)
        {
            _browserOpen = open;
            _model?.Dirty("browser_open");
        }

        if (open)
        {
            var width = Workspace.Layout.MusicBrowserShownWidth;
            if (width != _appliedBrowserWidth && Document.GetElementById("song-browser") is { IsNull: false } browser)
            {
                _appliedBrowserWidth = width;
                browser.SetProperty("width", RmlText.Dp(width));
            }

            if (Browser.Update(tab))
                _model?.Dirty("browser");
        }

        // A drag ends with the button (a release over the song's canvases is the SongView's); the ghost follows the mouse.
        if (Browser.Drag is not { } drag)
            return;
        var ghost = Document.GetElementById("song-drag-ghost");
        if (!Workspace.Host.PrimaryMouseDown && ++_dragUpFrames > 1)
        {
            Browser.CancelDrag();
            ghost.SetClass("hidden", true);
            return;
        }

        if (ghost.IsClassSet("hidden"))
        {
            ghost.SetClass("hidden", false);
            Document.GetElementById("song-drag-icon").SetClassNames(drag.Icon);
            Document.GetElementById("song-drag-label").SetInnerRml(RmlText.Escape(drag.Label));
        }

        var mouse = Workspace.Host.MousePosition * Workspace.Host.PointerScale;
        var origin = Rect ?? default;
        ghost.SetProperty("left", RmlText.Dp(mouse.X - origin.X + 14));
        ghost.SetProperty("top", RmlText.Dp(mouse.Y - origin.Y + 10));
    }

    /// <summary>The Browser's splitter moved to <paramref name="x"/> (window dp, its centre): the Browser takes the width to its right.</summary>
    internal void DragBrowserSplitter(float x)
    {
        if (!IsLoaded || Rect is not { } rect)
            return;
        Workspace.Layout.SetMusicBrowserWidth(rect.Right - x - EditorLayout.SplitterSize * 0.5f);
    }

    private static int BrowserRow(RmlEvent e) =>
        FindAttribute(e.Target, "data-b") is { } attr && int.TryParse(attr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : -1;

    private void OnBrowserClick(RmlEvent e)
    {
        if (BrowserRow(e) is >= 0 and var index)
            Browser.Click(index);
    }

    private void OnBrowserDoubleClick(RmlEvent e)
    {
        if (BrowserRow(e) is >= 0 and var index)
            Browser.DoubleClick(index);
    }

    private void OnBrowserPress(RmlEvent e)
    {
        _pressedRow = e.GetParameter("button", 0) == 0 ? BrowserRow(e) : -1;
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        _pressedAt = ((float)(e.GetParameter("mouse_x", 0.0) / scale), (float)(e.GetParameter("mouse_y", 0.0) / scale));
    }

    private void OnBrowserMove(RmlEvent e)
    {
        if (_pressedRow < 0 || Browser.Drag is not null)
            return;
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var dx = (float)(e.GetParameter("mouse_x", 0.0) / scale) - _pressedAt.X;
        var dy = (float)(e.GetParameter("mouse_y", 0.0) / scale) - _pressedAt.Y;
        if (dx * dx + dy * dy < DragThreshold * DragThreshold)
            return;
        if (Browser.BeginDrag(_pressedRow))
            _dragUpFrames = 0;
        _pressedRow = -1;
    }

    // A release over a track header, mixer strip or insert slot drops the dragged plugin there.
    private void OnBrowserRelease(RmlEvent e)
    {
        _pressedRow = -1;
        if (Browser.Drag is null)
            return;
        if (FindAttribute(e.Target, "data-drop") is { } target)
            Browser.Drop(target);
        else
            Browser.CancelDrag();
        Document.GetElementById("song-drag-ghost").SetClass("hidden", true);
    }

    private void OpenPluginEditor(SongTab tab, object owner)
    {
        if (tab.Player.Plugins?.Instances.FirstOrDefault(i => ReferenceEquals(i.Owner, owner)) is not { } instance)
        {
            Log.Warning("[Music] The plugin is not loaded (no plugin host, or the song is still loading it).");
            return;
        }

        if (instance.Error is { } error)
        {
            Log.Warning($"[Music] {instance.DisplayName}: {error}");
            return;
        }

        Guard($"Could not open {instance.DisplayName}'s editor", () => tab.Player.Plugins!.OpenEditor(instance));
    }

    // The first plugin menu without a scan cache scans in the background (the next menu lists the results).
    private void EnsureScanned()
    {
        if (!File.Exists(PluginScanner.DefaultCachePath))
            PluginScanner.RescanInBackground(Workspace.Settings.PluginFolders, force: false);
    }

    private static string InsertName(PluginInsert insert) => insert.Plugin is { } p && !insert.IsBuiltin
        ? string.IsNullOrEmpty(p.Vendor) ? p.Name ?? p.ClassId ?? "plugin" : $"{p.Name} ({p.Vendor})"
        : insert.DisplayName;

    /// <summary>The built-in instrument's ZzFX line in a text field; Apply checks it, stores it and auditions C4.</summary>
    public void EditLine(int index) => Track(index, (tab, track) =>
    {
        if (track.Kind != SongTrackKind.Instrument)
            return;
        if (track.Instrument is { IsWavetable: true })
        {
            Workspace.PluginWindow.OpenSynth(tab, track); // the synth's parameters (its own window comes later)
            return;
        }

        Workspace.Message.Show(new MessageRequest
        {
            Title = "Edit Sound",
            Message = $"The ZzFX line of {track.Name}'s built-in instrument (paste one copied from the sound designer):",
            Input = track.Instrument?.Params ?? SongDocument.DefaultZzfxLine,
            Buttons = ["Apply", "Cancel"],
            DefaultButton = 0,
            CancelButton = 1,
            Icon = "wave-sine",
            Callback = (button, text) =>
            {
                if (button != 0 || !tab.Document.Song.Tracks.Contains(track))
                    return;
                if (!ZzfxParameters.TryParse(text, out _, out var error))
                {
                    Workspace.Message.Show(new MessageRequest { Title = "Edit Sound", Message = $"Not a ZzFX line: {error}", Buttons = ["OK"] });
                    return;
                }

                Guard("Could not set the sound", () =>
                {
                    tab.Document.SetTrackInstrument(track, InstrumentDescriptor.Zzfx(text.Trim()));
                    tab.Player.Update(tab.Document);
                    tab.Controller.AuditionBriefly(track);
                });
            },
        });
    });

    private void AddTrackMenu((float X, float Y) at)
    {
        if (_tab is not { } tab || tab.Document.IsReadOnly)
            return;
        Workspace.Popup.Show(
        [
            new MenuItem("Instrument Track", "instrument", Icon: "music"),
            new MenuItem("Audio Track", "audio", Icon: "file-music"),
        ], at.X, at.Y, command => Guard("Could not add the track", () =>
        {
            var kind = command == "audio" ? SongTrackKind.Audio : SongTrackKind.Instrument;
            var track = tab.Document.AddTrack(kind);
            tab.SelectClip(track, null);
        }));
    }

    internal static string QuantizeLabel(MidiQuantize quantize) => quantize switch
    {
        MidiQuantize.Sixteenth => "1/16",
        MidiQuantize.Eighth => "1/8",
        _ => "Off",
    };

    /// <summary>The input quantize menu at <paramref name="at"/> (the toolbar's Input Quantize button).</summary>
    internal void QuantizeMenu((float X, float Y) at)
    {
        if (_tab is not { } tab)
            return;
        var items = new List<MenuItem> { MenuItem.Header("Input quantize") };
        foreach (var q in (ReadOnlySpan<MidiQuantize>)[MidiQuantize.Off, MidiQuantize.Sixteenth, MidiQuantize.Eighth])
            items.Add(new MenuItem(QuantizeLabel(q), "quantize:" + (int)q, Css: q == tab.Midi.Quantize ? "current" : null, Icon: q == MidiQuantize.Off ? "x" : "keyboard"));
        Workspace.Popup.Show(items, at.X, at.Y, command =>
        {
            if (int.TryParse(command.AsSpan(9), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                tab.Midi.Quantize = (MidiQuantize)value;
                _midiVersion = -1;
            }
        });
    }

    /// <summary>The snap menu at <paramref name="at"/> (the toolbar's Snap button).</summary>
    internal void SnapMenu((float X, float Y) at)
    {
        if (_tab is not { } tab)
            return;
        var items = new List<MenuItem> { MenuItem.Header("Snap") };
        foreach (var snap in SongGrid.All)
            items.Add(new MenuItem(SongGrid.Label(snap), "snap:" + (int)snap, Css: snap == tab.Snap ? "current" : null, Icon: snap == SongSnap.Off ? "x" : "magnet"));
        Workspace.Popup.Show(items, at.X, at.Y, command =>
        {
            if (int.TryParse(command.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                tab.Snap = (SongSnap)value;
                tab.Touch();
            }
        });
    }

    private static readonly (int Beats, int Unit)[] Signatures = [(2, 4), (3, 4), (4, 4), (5, 4), (6, 8), (7, 8), (12, 8)];

    /// <summary>The time signature menu at <paramref name="at"/> (the toolbar's Time Signature button).</summary>
    internal void SignatureMenu((float X, float Y) at)
    {
        if (_tab is not { } tab || tab.Document.IsReadOnly)
            return;
        var current = tab.Document.Song.TimeSignature;
        var items = new List<MenuItem> { MenuItem.Header("Time signature") };
        foreach (var (beats, unit) in Signatures)
            items.Add(new MenuItem($"{beats} / {unit}", $"sig:{beats}:{unit}",
                Css: current.Length == 2 && current[0] == beats && current[1] == unit ? "current" : null, Icon: "clock"));
        Workspace.Popup.Show(items, at.X, at.Y, command =>
        {
            var parts = command.Split(':');
            if (parts.Length == 3 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var b) && int.TryParse(parts[2], CultureInfo.InvariantCulture, out var u))
                Guard("Could not set the time signature", () => tab.Document.SetTimeSignature(b, u));
        });
    }

    private (float X, float Y) Below(RmlElement element)
    {
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var b = element.Bounds;
        return (b.X / scale, (b.Y + b.Height) / scale + 2);
    }

    private void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            Workspace.Commands.ReportError(what, e);
        }
    }

    private static string Number(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static readonly bool s_canEncode = PluginHostClient.IsAvailable;

    internal static string RenderTooltip(SongTab tab)
    {
        var output = SongRenderer.OutputPathFor(tab.Document.Song, tab.Document.Name, s_canEncode);
        var format = output.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
            ? $"Ogg Vorbis, quality {tab.Document.Song.Render.Quality}"
            : tab.Document.Song.Render.SampleFormat == SongSampleFormat.Pcm16 ? "16-bit WAV" : "32-bit float WAV";
        return $"Render — write {output} ({format}) and its loop points";
    }

    private static string DbText(float db) => db <= -60 ? "−∞ dB" : db.ToString("0.0", CultureInfo.InvariantCulture) + " dB";

    private static string PanText(float pan) => MathF.Abs(pan) < 0.005f ? "C"
        : (pan < 0 ? "L" : "R") + MathF.Round(MathF.Abs(pan) * 100).ToString(CultureInfo.InvariantCulture);

    private static string[] BuildPercent()
    {
        var result = new string[101];
        for (var i = 0; i <= 100; i++)
            result[i] = i.ToString(CultureInfo.InvariantCulture) + "%";
        return result;
    }
}
