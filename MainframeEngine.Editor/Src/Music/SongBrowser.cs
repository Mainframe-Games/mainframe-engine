using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor.Music;

/// <summary>A plugin being dragged from the <see cref="SongBrowser"/> (<c>fx:eq</c>, <c>vst:&lt;class id&gt;</c>, <c>vsti:&lt;class id&gt;</c>).</summary>
public sealed record SongPluginDrag(string ItemId, string Label, string Icon);

/// <summary>
/// Music mode's Browser (Logic's Browsers), the <see cref="SongPanel"/>'s right column: the project's songs (click opens
/// one) and every plugin — the built-in effects by category, then the scanned VST3 effects and instruments. A plugin is
/// applied by a double-click (the selected track, or the master) or dragged onto a track header, a mixer strip, an insert
/// slot (replacing it) or an arrangement lane. Sections and groups collapse (saved with the layout); the search field
/// filters everything. The list is a flat data-model list rebuilt only when what it shows changed.
/// </summary>
public sealed class SongBrowser(EditorWorkspace workspace)
{
    public const int SectionKind = 0;
    public const int GroupKind = 1;
    public const int ItemKind = 2;
    public const int NoteKind = 3;

    public sealed class Row
    {
        public required int Index { get; init; }
        public required int Kind { get; init; }
        public required string Label { get; init; }
        public required string Icon { get; init; }
        public required string Tooltip { get; init; }
        public required bool Current { get; init; }
        public required bool Collapsed { get; init; }
        public required bool Plugin { get; init; }
    }

    public static readonly RmlStructType<Row> RowType = new RmlStructType<Row>()
        .Member("index", static r => r.Index)
        .Member("kind", static r => r.Kind)
        .Member("label", static r => r.Label)
        .Member("icon", static r => r.Icon)
        .Member("tooltip", static r => r.Tooltip)
        .Member("current", static r => r.Current)
        .Member("collapsed", static r => r.Collapsed)
        .Member("plugin", static r => r.Plugin);

    private readonly List<Row> _rows = [];
    private readonly List<string> _ids = [];
    private HashSet<string>? _collapsed;
    private string? _root;
    private int _filesVersion = -1;
    private string? _current;
    private PluginCatalog? _catalog;
    private string _builtSearch = "";
    private int _collapsedVersion;
    private int _builtCollapsed = -1;
    private bool _built;

    /// <summary>The rows the panel binds (<see cref="RowType"/>).</summary>
    public List<Row> Rows => _rows;

    /// <summary>Each row's id: <c>songs</c>/<c>plugins</c> (sections), <c>fx:&lt;category&gt;</c>/<c>vst</c>/<c>vsti</c> (groups), <c>song:&lt;path&gt;</c> and plugin ids (items).</summary>
    public IReadOnlyList<string> Ids => _ids;

    /// <summary>The search text (case-insensitive substring of names).</summary>
    public string Search { get; set; } = "";

    /// <summary>The plugin being dragged, or null.</summary>
    public SongPluginDrag? Drag { get; private set; }

    private HashSet<string> Collapsed => _collapsed ??= new HashSet<string>(workspace.Layout.Settings.MusicBrowserCollapsed ?? [], StringComparer.Ordinal);

    /// <summary>Every frame while the Browser shows: rebuilds the rows when the project, its songs, the active song, the plugin catalog, the search or the collapsed set changed. True when it did.</summary>
    public bool Update(SongTab? tab)
    {
        var root = workspace.Session.ProjectRoot;
        var files = workspace.FileSystem.Files?.Version ?? -1;
        var current = tab?.FilePath;
        var catalog = PluginCatalog.Current;
        if (_built && string.Equals(root, _root, StringComparison.Ordinal) && files == _filesVersion && string.Equals(current, _current, StringComparison.Ordinal) &&
            ReferenceEquals(catalog, _catalog) && string.Equals(Search, _builtSearch, StringComparison.Ordinal) && _builtCollapsed == _collapsedVersion)
            return false;
        _built = true;
        (_root, _filesVersion, _current, _catalog, _builtSearch, _builtCollapsed) = (root, files, current, catalog, Search, _collapsedVersion);
        Build(root, current, catalog);
        return true;
    }

    /// <summary>The rows are read again on the next <see cref="Update"/> (a song was created or saved).</summary>
    public void Invalidate() => _built = false;

    private void Build(string? root, string? current, PluginCatalog catalog)
    {
        _rows.Clear();
        _ids.Clear();
        var search = Search.Trim();
        var filtering = search.Length > 0;
        bool Matches(string text) => !filtering || text.Contains(search, StringComparison.OrdinalIgnoreCase);

        // Songs.
        var songs = root is null ? [] : SongPanel.FindSongs(root, int.MaxValue).Where(p => Matches(Path.GetFileNameWithoutExtension(p))).ToList();
        if (Section("songs", "Songs", "music", root is null ? "Songs — open a project first" : "Songs — every .msong in the project; click to open"))
        {
            var content = root is null ? "" : Path.Combine(root, "Content");
            foreach (var song in songs.OrderBy(static p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase))
            {
                var relative = content.Length > 0 ? Path.GetRelativePath(content, song) : song;
                Add("song:" + song, ItemKind, Path.GetFileNameWithoutExtension(song), "music", $"{relative} — click to open",
                    current is not null && EditorSession.PathsEqual(current, song));
            }

            if (songs.Count == 0)
                Add("", NoteKind, root is null ? "No project open" : filtering ? "No songs match" : "No songs yet — File › New Song", "", "", false);
        }

        // Plugins: the built-in categories, then VST3.
        if (!Section("plugins", "Plugins", "plug", "Plugins — double-click to add to the selected track (or the master); drag onto a track, mixer strip or insert slot"))
            return;
        foreach (var category in Enum.GetValues<BuiltinCategory>())
        {
            var effects = BuiltinEffects.InCategory(category).Where(i => Matches(i.Name)).ToList();
            if (filtering && effects.Count == 0)
                continue;
            if (!Group("fx:" + category, BuiltinEffects.CategoryName(category), "Built-in effects"))
                continue;
            foreach (var info in effects)
                Add("fx:" + info.Id, ItemKind, info.Name, info.Icon, info.Tooltip, false);
        }

        // Built-in instruments.
        (string Id, string Name, string Icon, string Tip)[] builtins =
        [
            ("inst:wavetable", SynthParams.ProductName, "wave-square", "Two wavetable oscillators, unison, two filters, envelopes, LFOs and a mod matrix"),
            ("inst:zzfx", "Built-in ZzFX", "wave-sine", "The ZzFX sound-effect synth as an instrument (a sound line per track)"),
        ];
        var instrumentsBuiltin = builtins.Where(b => Matches(b.Name)).ToList();
        if ((!filtering || instrumentsBuiltin.Count > 0) && Group("inst", "Instruments", "Built-in instruments: drop one on an instrument track"))
        {
            foreach (var (id, name, icon, tip) in instrumentsBuiltin)
                Add(id, ItemKind, name, icon, tip + " — double-click sets the selected track's instrument", false);
        }

        var vst = catalog.Effects.Where(p => Matches(p.DisplayName)).ToList();
        if ((!filtering || vst.Count > 0) && Group("vst", "VST3 Effects", "Scanned VST3 effect plugins (Editor Settings › VST3 folders)"))
        {
            EnsureScanned();
            foreach (var plugin in vst)
                Add("vst:" + plugin.ClassId, ItemKind, plugin.DisplayName, "plug", $"{plugin.DisplayName} — VST3 effect", false);
            if (vst.Count == 0)
                Add("", NoteKind, PluginHostClient.IsAvailable ? "None found — Editor Settings › Rescan Plugins" : "The plugin host is not available", "", "", false);
        }

        var instruments = catalog.Instruments.Where(p => Matches(p.DisplayName)).ToList();
        if ((!filtering || instruments.Count > 0) && Group("vsti", "VST3 Instruments", "Scanned VST3 instruments: drop one on an instrument track"))
        {
            EnsureScanned();
            foreach (var plugin in instruments)
                Add("vsti:" + plugin.ClassId, ItemKind, plugin.DisplayName, "piano", $"{plugin.DisplayName} — VST3 instrument; drop on an instrument track", false);
            if (instruments.Count == 0)
                Add("", NoteKind, "None found", "", "", false);
        }

        // Search shows everything that matches: sections and groups stay open.
        bool Section(string id, string label, string icon, string tooltip)
        {
            var collapsed = !filtering && Collapsed.Contains(id);
            Add(id, SectionKind, label, icon, tooltip, false, collapsed);
            return !collapsed;
        }

        bool Group(string id, string label, string tooltip)
        {
            var collapsed = !filtering && Collapsed.Contains(id);
            Add(id, GroupKind, label, "", tooltip, false, collapsed);
            return !collapsed;
        }
    }

    private void Add(string id, int kind, string label, string icon, string tooltip, bool current, bool collapsed = false)
    {
        _rows.Add(new Row
        {
            Index = _rows.Count,
            Kind = kind,
            Label = label,
            Icon = icon.Length == 0 ? "" : "icon icon-sm icon-" + icon,
            Tooltip = tooltip,
            Current = current,
            Collapsed = collapsed,
            Plugin = kind == ItemKind && !id.StartsWith("song:", StringComparison.Ordinal),
        });
        _ids.Add(id);
    }

    // The first plugin group shown without a scan cache scans in the background (the next rebuild lists the results).
    private void EnsureScanned()
    {
        if (!File.Exists(PluginScanner.DefaultCachePath))
            PluginScanner.RescanInBackground(workspace.Settings.PluginFolders, force: false);
    }

    // --- interaction --------------------------------------------------------------------------------------------------

    /// <summary>A click on row <paramref name="index"/>: a section or group collapses or opens, a song opens.</summary>
    public void Click(int index)
    {
        if ((uint)index >= (uint)_rows.Count)
            return;
        var id = _ids[index];
        switch (_rows[index].Kind)
        {
            case SectionKind or GroupKind:
                ToggleCollapsed(id);
                break;
            case ItemKind when id.StartsWith("song:", StringComparison.Ordinal):
                workspace.Commands.OpenSong(id[5..]);
                break;
        }
    }

    /// <summary>A double-click on a plugin: added to the active song's selected track (or, with none, the master).</summary>
    public void DoubleClick(int index)
    {
        if ((uint)index >= (uint)_rows.Count || _rows[index].Kind != ItemKind || _ids[index].StartsWith("song:", StringComparison.Ordinal) ||
            workspace.Session.ActiveSong is not { } tab)
            return;
        Apply(tab, _ids[index], tab.SelectedTrack, null);
    }

    public void ToggleCollapsed(string id)
    {
        if (!Collapsed.Remove(id))
            Collapsed.Add(id);
        _collapsedVersion++;
        workspace.Layout.SetMusicBrowserCollapsed(Collapsed);
        workspace.SaveLayout();
    }

    /// <summary>Starts dragging the plugin of row <paramref name="index"/> (after the mouse moved a few dp); false for other rows.</summary>
    public bool BeginDrag(int index)
    {
        if ((uint)index >= (uint)_rows.Count || _rows[index].Kind != ItemKind || _ids[index].StartsWith("song:", StringComparison.Ordinal))
            return false;
        Drag = new SongPluginDrag(_ids[index], _rows[index].Label, _rows[index].Icon);
        return true;
    }

    public void CancelDrag() => Drag = null;

    /// <summary>
    /// Drops the dragged plugin (or <paramref name="itemId"/>) on <paramref name="target"/>: <c>track:i</c> or <c>strip:i</c>
    /// (appended to that track's inserts, or an instrument set), <c>slot:i:j</c> (replaces insert j), <c>master</c> or
    /// <c>mslot:j</c>. False when it does not apply.
    /// </summary>
    public bool Drop(string target, string? itemId = null)
    {
        itemId ??= Drag?.ItemId;
        Drag = null;
        if (itemId is null || workspace.Session.ActiveSong is not { } tab)
            return false;
        var song = tab.Document.Song;
        var parts = target.Split(':');
        SongTrack? TrackOf(string s) => int.TryParse(s, out var i) && (uint)i < (uint)song.Tracks.Count ? song.Tracks[i] : null;
        switch (parts[0])
        {
            case "track" or "strip" when parts.Length == 2 && TrackOf(parts[1]) is { } track:
                return Apply(tab, itemId, track, null);
            case "slot" when parts.Length == 3 && TrackOf(parts[1]) is { } track && int.TryParse(parts[2], out var j) && (uint)j < (uint)track.Inserts.Count:
                return Apply(tab, itemId, track, track.Inserts[j]);
            case "master":
                return Apply(tab, itemId, null, null);
            case "mslot" when parts.Length == 2 && int.TryParse(parts[1], out var m) && (uint)m < (uint)song.Master.Inserts.Count:
                return Apply(tab, itemId, null, song.Master.Inserts[m]);
            default:
                return false;
        }
    }

    /// <summary>
    /// Applies plugin <paramref name="itemId"/> to <paramref name="track"/> (null: the master): an effect is appended to its
    /// inserts, or replaces <paramref name="replace"/>; an instrument becomes an instrument track's instrument. A built-in
    /// effect's window opens. False (logged) when it does not fit there.
    /// </summary>
    public bool Apply(SongTab tab, string itemId, SongTrack? track, PluginInsert? replace)
    {
        var document = tab.Document;
        if (document.IsReadOnly)
            return false;
        try
        {
            var colon = itemId.IndexOf(':', StringComparison.Ordinal);
            var (kind, id) = colon < 0 ? (itemId, "") : (itemId[..colon], itemId[(colon + 1)..]);
            switch (kind)
            {
                case "fx" when BuiltinEffects.Find(id) is { } info:
                    {
                        PluginInsert insert;
                        if (replace is null)
                        {
                            insert = document.AddBuiltinInsert(track, info.Id);
                        }
                        else
                        {
                            insert = PluginInsert.ForBuiltin(info);
                            document.ReplaceInsert(track, replace, insert);
                        }

                        tab.Player.Update(document);
                        workspace.PluginWindow.Open(tab, track, insert);
                        return true;
                    }

                case "vst" when PluginCatalog.Current.Find(id) is { IsInstrument: false } plugin:
                    if (replace is null)
                        document.AddInsert(track, plugin.ToDescriptor());
                    else
                        document.ReplaceInsert(track, replace, new PluginInsert { Plugin = plugin.ToDescriptor() });
                    return true;
                case "inst" when id is InstrumentDescriptor.WavetableBuiltin or InstrumentDescriptor.ZzfxBuiltin:
                    if (track is not { Kind: SongTrackKind.Instrument })
                    {
                        Log.Info("[Music] Instruments go on instrument tracks.");
                        return false;
                    }

                    document.SetTrackInstrument(track, id == InstrumentDescriptor.WavetableBuiltin
                        ? InstrumentDescriptor.Wavetable()
                        : InstrumentDescriptor.Zzfx(SongDocument.DefaultZzfxLine));
                    tab.Player.Update(document);
                    if (id == InstrumentDescriptor.WavetableBuiltin)
                        workspace.SynthWindow.Open(tab, track);
                    return true;
                case "vsti" when PluginCatalog.Current.Find(id) is { IsInstrument: true } instrument:
                    if (track is not { Kind: SongTrackKind.Instrument })
                    {
                        Log.Info($"[Music] {instrument.DisplayName} is an instrument: drop it on an instrument track.");
                        return false;
                    }

                    document.SetTrackInstrument(track, new InstrumentDescriptor { Plugin = instrument.ToDescriptor() });
                    return true;
                default:
                    Log.Info($"[Music] '{itemId}' is not a plugin this editor has.");
                    return false;
            }
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            workspace.Commands.ReportError("Could not add the plugin", e);
            return false;
        }
    }
}
