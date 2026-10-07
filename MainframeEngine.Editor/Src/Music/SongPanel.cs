using System.Globalization;
using MainframeEngine.Editor.Music;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The song tab's chrome (<c>Content/Editor/song.rml</c>), over the view area while a <see cref="SongTab"/> is active:
/// the transport (rewind, play, stop, loop, tempo, time signature, position, snap, underruns, Render with progress), the
/// track headers (rename, colour, mute, solo, instrument, add and remove), the Piano roll | Mixer switcher and the mixer
/// strips (instrument slot, inserts placeholder, pan, volume fader, peak meter, mute/solo, master). The arrangement and
/// the piano roll are images of the tab's canvases (<see cref="SongView"/>). Steady-state frames touch only the position,
/// the meters and the underrun counter, and allocate nothing.
/// </summary>
public sealed class SongPanel : EditorDocument
{
    private static readonly string[] Percent = BuildPercent();

    private sealed class Row
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required string Color { get; init; }
        public required string Instrument { get; init; }
        public required string Inserts { get; init; }
        public required bool Audio { get; init; }
        public required bool Mute { get; init; }
        public required bool Solo { get; init; }
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
        .Member("inserts", static r => r.Inserts)
        .Member("audio", static r => r.Audio)
        .Member("mute", static r => r.Mute)
        .Member("solo", static r => r.Solo)
        .Member("selected", static r => r.Selected)
        .Member("status", static r => r.Status)
        .Member("fader", static r => r.Fader)
        .Member("db", static r => r.Db)
        .Member("pan", static r => r.Pan)
        .Member("pan_text", static r => r.PanText)
        .Member("tooltip", static r => r.Tooltip);

    private readonly List<Row> _rows = [];
    private readonly List<string> _meterIds = [];
    private int[] _meterShown = [];
    private float[] _meterLevel = [];
    private float _masterLevel;
    private int _masterShown = -1;
    private RmlDataModel? _model;
    private SongTab? _tab;
    private int _docVersion = -1;
    private int _viewVersion = -1;
    private long _shownPosition = -1;
    private int _shownUnderruns = -1;
    private int _shownRender = -2;
    private float _shownScroll = float.NaN;
    private bool _shownPlaying;
    private bool _tempoFocused;
    private bool _arrangeImage;
    private bool _rollImage;
    private RmlEventListener?[] _listeners = new RmlEventListener?[4];

    // Bound values.
    private bool _readOnly;
    private string _notice = "";
    private bool _playing;
    private bool _loop;
    private string _sig = "4 / 4";
    private string _snap = "1/16";
    private string _xruns = "0 xruns";
    private bool _xrunsBad;
    private bool _rendering;
    private string _renderLabel = "Render";
    private string _renderTooltip = "";
    private bool _panelRoll = true;
    private string _clipInfo = "";
    private string _masterFader = "0";
    private string _masterDb = "0.0 dB";
    private string _masterInserts = "none";

    public SongPanel(EditorWorkspace workspace)
        : base(workspace, "song.rml")
    {
        Visible = false;
    }

    /// <summary>The arrangement canvas area in window dp (empty while hidden).</summary>
    public LayoutRect ArrangeRect { get; private set; }

    /// <summary>The piano-roll canvas area in window dp (empty while the mixer shows).</summary>
    public LayoutRect RollRect { get; private set; }

    protected override void OnReady()
    {
        _model = CreateDataModel("song")
            .BindList("rows", _rows, RowType)
            .Bind("readonly", this, static p => p._readOnly)
            .Bind("notice", this, static p => p._notice)
            .Bind("playing", this, static p => p._playing)
            .Bind("loop", this, static p => p._loop)
            .Bind("sig", this, static p => p._sig)
            .Bind("snap", this, static p => p._snap)
            .Bind("xruns", this, static p => p._xruns)
            .Bind("xruns_bad", this, static p => p._xrunsBad)
            .Bind("rendering", this, static p => p._rendering)
            .Bind("render_label", this, static p => p._renderLabel)
            .Bind("render_tooltip", this, static p => p._renderTooltip)
            .Bind("panel_roll", this, static p => p._panelRoll)
            .Bind("clip_info", this, static p => p._clipInfo)
            .Bind("master_fader", this, static p => p._masterFader)
            .Bind("master_db", this, static p => p._masterDb)
            .Bind("master_inserts", this, static p => p._masterInserts)
            .Event("inserts", e => InsertMenu(e.GetArgument(0).GetInt32(), Below(e.Event.CurrentElement)))
            .Event("play", _ => Song(t => t.Player.Play()))
            .Event("stop", _ => Song(t => t.Player.Stop()))
            .Event("rewind", _ => Song(t => t.Controller.Key(Silk.NET.Input.Key.Home, EditorModifiers.None)))
            .Event("toggle_loop", _ => Song(t => t.Controller.ToggleLoop()))
            .Event("render", _ => Workspace.Commands.Execute("song.render"))
            .Event("snap_menu", e => SnapMenu(Below(e.Event.CurrentElement)))
            .Event("sig_menu", e => SignatureMenu(Below(e.Event.CurrentElement)))
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
            .Event("panel", e =>
            {
                var mixer = e.GetArgument(0).GetInt32() == 1;
                Song(t => t.SetPanel(mixer ? SongBottomPanel.Mixer : SongBottomPanel.PianoRoll));
            });
    }

    protected override void OnAttach(RmlDocument document)
    {
        foreach (var listener in _listeners)
            listener?.Remove();
        var body = document.AsElement();
        _listeners[0] = body.AddEventListener("change", OnChange);
        _listeners[1] = body.AddEventListener("mouseup", _ => _tab?.Document.History.EndMerge());
        _listeners[2] = body.AddEventListener("focus", e =>
        {
            if (e.Target.Id == "song-tempo")
                _tempoFocused = true;
        }, inCapturePhase: true);
        _listeners[3] = body.AddEventListener("blur", e =>
        {
            if (e.Target.Id != "song-tempo")
                return;
            _tempoFocused = false;
            _tab?.Document.History.EndMerge();
            _docVersion = -1; // show the tempo as stored
        }, inCapturePhase: true);
        _arrangeImage = false;
        _rollImage = false;
        _docVersion = -1;
        _shownPosition = -1;
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

    /// <summary>Every frame (from the <see cref="SongView"/>): follows the active song tab.</summary>
    public void Tick(SongTab? tab)
    {
        var show = tab is not null;
        if (Visible != show)
            Visible = show;
        if (!ReferenceEquals(tab, _tab))
        {
            _tab = tab;
            _docVersion = -1;
            _viewVersion = -1;
            _shownPosition = -1;
            _shownRender = -2;
        }

        if (tab is null || !IsLoaded)
        {
            ArrangeRect = default;
            RollRect = default;
            return;
        }

        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        ArrangeRect = RectOf("song-arrange", scale);
        RollRect = tab.Panel == SongBottomPanel.PianoRoll ? RectOf("song-roll", scale) : default;

        if (tab.Document.Version != _docVersion || tab.ViewVersion != _viewVersion)
            Rebuild(tab);
        UpdatePosition(tab);
        UpdateTransport(tab);
        UpdateMeters(tab);
        UpdateHeaderScroll(tab);
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
                Inserts = InsertsText(track.Inserts),
                Audio = track.Kind == SongTrackKind.Audio,
                Mute = track.Mute,
                Solo = track.Solo,
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
        _loop = song.Loop.Enabled;
        _sig = $"{song.TimeSignature.ElementAtOrDefault(0)} / {song.TimeSignature.ElementAtOrDefault(1)}";
        _snap = SongGrid.Label(tab.Snap);
        _panelRoll = tab.Panel == SongBottomPanel.PianoRoll;
        _clipInfo = ClipInfo(tab);
        _masterFader = Number(-Math.Clamp(song.Master.VolumeDb, -60f, 6f));
        _masterDb = DbText(song.Master.VolumeDb);
        _masterInserts = InsertsText(song.Master.Inserts);
        if (!_tempoFocused && Document.GetElementById("song-tempo") is { IsNull: false } tempo)
            tempo.SetValue(song.Tempo.ToString("0.##", CultureInfo.InvariantCulture));
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

    private void UpdatePosition(SongTab tab)
    {
        var song = tab.Document.Song;
        var ticks = (long)tab.Player.PositionTicks;
        var shown = ticks - ticks % Math.Max(1, song.Ppq / 64);
        if (shown == _shownPosition)
            return;
        _shownPosition = shown;
        var element = Document.GetElementById("song-pos");
        if (element.IsNull)
            return;
        Span<char> text = stackalloc char[24];
        var n = SongGrid.FormatPosition(text, ticks, song);
        element.SetInnerRml(text[..n]);
    }

    private void UpdateTransport(SongTab tab)
    {
        var dirty = false;
        if (tab.Player.IsPlaying != _shownPlaying)
        {
            _shownPlaying = tab.Player.IsPlaying;
            _playing = _shownPlaying;
            _model?.Dirty("playing");
        }

        var underruns = tab.Player.Underruns + tab.Player.PluginXruns;
        if (underruns != _shownUnderruns)
        {
            _shownUnderruns = underruns;
            _xruns = underruns == 1 ? "1 xrun" : underruns.ToString(CultureInfo.InvariantCulture) + " xruns";
            _xrunsBad = underruns > 0;
            dirty = true;
        }

        var job = Workspace.SongView.RenderOf(tab.Document.FilePath);
        var render = job is null ? -1 : (int)(job.Progress * 100);
        if (render != _shownRender)
        {
            _shownRender = render;
            _rendering = job is not null;
            _renderLabel = job is null ? "Render" : $"Cancel {render} %";
            _renderTooltip = job is null
                ? RenderTooltip(tab)
                : $"Rendering {tab.Document.Name}… {render} % — click to cancel (the previous output stays)";
            dirty = true;
        }

        if (dirty && _model is { } model)
        {
            model.Dirty("xruns");
            model.Dirty("xruns_bad");
            model.Dirty("rendering");
            model.Dirty("render_label");
            model.Dirty("render_tooltip");
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
        if (_tab is not { } tab || tab.Document.IsReadOnly)
            return;
        var target = e.Target;
        var value = e.Value;
        if (target.Id == "song-tempo")
        {
            if (_tempoFocused && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm) && bpm is >= 20 and <= 999 &&
                Math.Abs(bpm - tab.Document.Song.Tempo) > 1e-6)
                Guard("Could not set the tempo", () => tab.Document.SetTempo(bpm, "song-tempo"));
            return;
        }

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
            new("Built-in ZzFX", "zzfx", Css: track.Instrument?.IsZzfx == true ? "current" : null, Icon: "wave-sine"),
            new("Edit Sound…", "edit", Enabled: track.Instrument?.IsZzfx == true, Icon: "pencil"),
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
            if (command == "zzfx" && track.Instrument?.IsZzfx != true)
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

    // A track's (index ≥ 0) or the master's (−1) insert chain: per insert open editor, bypass, move, remove; add an effect.
    private void InsertMenu(int index, (float X, float Y) at)
    {
        if (_tab is not { } tab || index >= tab.Document.Song.Tracks.Count)
            return;
        var track = index >= 0 ? tab.Document.Song.Tracks[index] : null;
        var inserts = track?.Inserts ?? tab.Document.Song.Master.Inserts;
        var catalog = PluginCatalog.Current;
        var items = new List<MenuItem>();
        for (var i = 0; i < inserts.Count; i++)
        {
            var insert = inserts[i];
            var n = i.ToString(CultureInfo.InvariantCulture);
            items.Add(MenuItem.Header($"{i + 1}. {InsertName(insert)}{(insert.Bypass ? " (bypassed)" : "")}"));
            items.Add(new MenuItem("Open Plugin Editor", "open:" + n, Enabled: insert.Plugin is not null, Icon: "app-window"));
            items.Add(new MenuItem(insert.Bypass ? "Enable" : "Bypass", "bypass:" + n, Icon: insert.Bypass ? "plug-connected" : "plug-x"));
            items.Add(new MenuItem("Move Up", "up:" + n, Enabled: i > 0, Icon: "arrow-up"));
            items.Add(new MenuItem("Move Down", "down:" + n, Enabled: i < inserts.Count - 1, Icon: "arrow-down"));
            items.Add(new MenuItem("Remove", "remove:" + n, Icon: "trash"));
        }

        items.Add(MenuItem.Header("Add VST3 effect"));
        foreach (var plugin in catalog.Effects)
            items.Add(new MenuItem(plugin.DisplayName, "add:" + plugin.ClassId, Icon: "plus"));
        if (!catalog.Effects.Any())
            items.Add(new MenuItem(PluginHostClient.IsAvailable ? "None found — Editor Settings › Rescan Plugins" : "The plugin host is not available", null, Enabled: false, Icon: "plug-x"));

        EnsureScanned();
        Workspace.Popup.Show(items, at.X, at.Y, command =>
        {
            if (command is null || (track is not null && !tab.Document.Song.Tracks.Contains(track)))
                return;
            var colon = command.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
                return;
            var verb = command[..colon];
            var argument = command[(colon + 1)..];
            if (verb == "add")
            {
                if (catalog.Find(argument) is { } plugin)
                    Guard("Could not add the insert", () => tab.Document.AddInsert(track, plugin.ToDescriptor()));
                return;
            }

            if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) || (uint)i >= (uint)inserts.Count)
                return;
            var insert = inserts[i];
            switch (verb)
            {
                case "open":
                    OpenPluginEditor(tab, insert);
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
            }
        });
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

    private static string InsertName(PluginInsert insert) => insert.Plugin is { } p
        ? string.IsNullOrEmpty(p.Vendor) ? p.Name ?? p.ClassId ?? "plugin" : $"{p.Name} ({p.Vendor})"
        : "unknown insert";

    private static string InsertsText(List<PluginInsert> inserts) => inserts.Count == 0
        ? "none"
        : string.Join(", ", inserts.Select(i => (i.Plugin?.Name ?? "?") + (i.Bypass ? " (off)" : "")));

    /// <summary>The built-in instrument's ZzFX line in a text field; Apply checks it, stores it and auditions C4.</summary>
    public void EditLine(int index) => Track(index, (tab, track) =>
    {
        if (track.Kind != SongTrackKind.Instrument)
            return;
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

    private void SnapMenu((float X, float Y) at)
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

    private void SignatureMenu((float X, float Y) at)
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

    private static string RenderTooltip(SongTab tab)
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
