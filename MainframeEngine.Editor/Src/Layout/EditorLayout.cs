using System.Text.Json;
using System.Text.Json.Serialization;
using MainframeEngine.Editor.Music;

namespace MainframeEngine.Editor;

/// <summary>A rectangle in dp (= window points; the editor UI layer uses <see cref="UiScaleMode.Dpi"/>).</summary>
public readonly record struct LayoutRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;

    public bool Contains(float x, float y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>The four draggable splitters.</summary>
public enum Splitter
{
    /// <summary>Between the left dock (scene tree, file system) and the centre.</summary>
    Left,

    /// <summary>Between the centre and the right dock (inspector).</summary>
    Right,

    /// <summary>Between the viewport and the output panel.</summary>
    Bottom,

    /// <summary>Between the scene tree and the file system in the left dock.</summary>
    LeftDock,
}

/// <summary>
/// The persisted panel sizes (Layout v1: fixed regions with draggable splitters, editor.md). Stored as JSON in
/// <c>~/.mainframe/editor_layout.json</c> and written atomically (temp file + rename).
/// </summary>
/// <summary>The bottom dock's tabs (G10): the Output panel and the Profiler; TileMap and TileSet (G2b) while a tile map layer is edited.</summary>
public enum BottomDockTab
{
    Output = 0,
    Profiler = 1,
    TileMap = 2,
    TileSet = 3,
}

public sealed record EditorLayoutSettings
{
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;
    public float LeftWidth { get; init; } = 270;
    public float RightWidth { get; init; } = 330;
    public float BottomHeight { get; init; } = 190;

    /// <summary>Share of the left dock's height taken by the scene tree (the file system gets the rest).</summary>
    public float LeftDockSplit { get; init; } = 0.62f;

    /// <summary>Window size in points at the last close (0 = default).</summary>
    public int WindowWidth { get; init; }
    public int WindowHeight { get; init; }

    /// <summary>Every <see cref="OutputLevel"/> shown.</summary>
    public const int DefaultOutputFilter = 0b1111;

    /// <summary>
    /// The output panel's level filter (bit per <see cref="OutputLevel"/>). Nullable because the JSON source generator
    /// ignores the initializers of <c>init</c> properties: a file without the key reads null, which <c>Load</c> restores to
    /// <see cref="DefaultOutputFilter"/> (a saved 0 stays).
    /// </summary>
    public int? OutputFilter { get; init; } = DefaultOutputFilter;

    /// <summary>The inspector's property-name column width in dp; 0 sizes it to the longest name.</summary>
    public float InspectorLabelWidth { get; init; }

    /// <summary>The bottom dock's active tab (<see cref="BottomDockTab"/>; a file without the key reads Output).</summary>
    public int BottomTab { get; init; }

    /// <summary>The output panel folds repeated lines into one with a ×N badge.</summary>
    public bool OutputCollapse { get; init; }

    /// <summary>
    /// The output panel scrolls to each new line (off: the scroll position stays). Nullable like <see cref="OutputFilter"/>:
    /// a file without the key reads null, which <c>Load</c> restores to true.
    /// </summary>
    public bool? OutputFollow { get; init; } = true;

    /// <summary>
    /// The sound designer (ZzfxStream inspector) plays only when asked, not after every committed change. Stored inverted,
    /// so a file written without the key auto-plays: the JSON source generator ignores the initializers of <c>init</c>
    /// properties, and a missing <c>bool</c> reads as false. The old <c>SoundDesignerAutoPlay</c> key is ignored, since
    /// that bug saved it as false in every layout file from before the sound designer.
    /// </summary>
    public bool SoundDesignerManualPlay { get; init; }

    /// <summary>The sound designer replays the sound after every committed change: <see cref="SoundDesignerManualPlay"/> inverted.</summary>
    [JsonIgnore]
    public bool SoundDesignerAutoPlay => !SoundDesignerManualPlay;

    /// <summary>Favourite entries per picker (<c>node</c>, <c>resource</c>, <c>scene</c>): type names or project paths.</summary>
    public Dictionary<string, string[]>? PickerFavorites { get; init; }

    /// <summary>Recently created entries per picker, newest first.</summary>
    public Dictionary<string, string[]>? PickerRecent { get; init; }

    /// <summary>
    /// The FileSystem panel shows the whole project folder; off (the default) is its Simplified view (Assets and Scripts).
    /// Stored inverted like <see cref="SoundDesignerManualPlay"/>, so a file written without the key is Simplified.
    /// </summary>
    public bool FileSystemVerbose { get; init; }

    /// <summary>Video mode shows the output panel below the view (View › Show/Hide Output); game mode always does.</summary>
    public bool VideoShowOutput { get; init; }

    /// <summary>Music mode shows the output panel below the music editor (View › Show/Hide Output); off by default.</summary>
    public bool MusicShowOutput { get; init; }

    /// <summary>
    /// Video mode hides the transcript beside the preview (View › Show/Hide Transcript). Stored inverted, so a file written
    /// without the key shows it: the JSON source generator ignores the initializers of <c>init</c> properties, and a
    /// missing <c>bool</c> reads as false.
    /// </summary>
    public bool VideoHideTranscript { get; init; }

    /// <summary>Video mode shows the transcript beside the preview: <see cref="VideoHideTranscript"/> inverted.</summary>
    [JsonIgnore]
    public bool VideoShowTranscript => !VideoHideTranscript;

    /// <summary>Video mode's transcript width in dp (a file without the key reads 0, which <c>Load</c> restores to 320).</summary>
    public float VideoTranscriptWidth { get; init; } = 320;

    /// <summary>Video mode's timeline height in dp (a file without the key reads 0, which <c>Load</c> restores to 180).</summary>
    public float VideoTimelineHeight { get; init; } = 180;

    /// <summary>
    /// Music mode's bottom pane (P / X, Logic style: one editor at a time, or none), shown for whichever song tab is active.
    /// Stored as its name; a file without the key reads <see cref="SongBottomPanel.PianoRoll"/> (the enum's first value).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<SongBottomPanel>))]
    public SongBottomPanel MusicPane { get; init; } = SongBottomPanel.PianoRoll;

    /// <summary>Music mode's bottom pane height in dp, below its header (a file without the key reads 0, which <c>Load</c> restores to 300).</summary>
    public float MusicPaneHeight { get; init; } = 300;

    /// <summary>Music mode's Browser on the right (F; Logic's Browsers): the project's songs and the plugins. Hidden by default.</summary>
    public bool MusicShowBrowser { get; init; }

    /// <summary>The right panel shows its Adaptive tab (A) instead of the Browser (F): an adaptive song's sections, parameters and rules.</summary>
    public bool MusicAdaptiveTab { get; init; }

    /// <summary>The Browser's width in dp (a file without the key reads 0, which <c>Load</c> restores to 260).</summary>
    public float MusicBrowserWidth { get; init; } = 260;

    /// <summary>The Browser's collapsed sections and groups (ids such as <c>songs</c>, <c>fx:Dynamics</c>, <c>vst</c>).</summary>
    public string[]? MusicBrowserCollapsed { get; init; }
}

/// <summary>
/// Computes the editor's panel rectangles from the window size and the <see cref="EditorLayoutSettings"/>, applies
/// splitter drags with clamping, and loads/saves the settings. Pure layout math, unit-tested.
/// </summary>
public sealed class EditorLayout
{
    public const float MenuBarHeight = 28;
    public const float ToolbarHeight = 36;
    public const float SplitterSize = 5;
    public const float MinDock = 150;
    public const float MinBottom = 70;
    public const float MinViewportWidth = 240;
    public const float MinViewportHeight = 160;

    public EditorLayout(EditorLayoutSettings? settings = null)
    {
        Settings = settings ?? new EditorLayoutSettings();
    }

    public EditorLayoutSettings Settings { get; private set; }

    /// <summary>
    /// The mode the rectangles are for. Video and music mode have no docks: the viewport panel (its tab strip above the
    /// video or song panel) spans the window, with the output panel below it only when <see cref="ShowsOutput"/>.
    /// </summary>
    public EditorMode Mode { get; private set; }

    /// <summary>Switches the layout to <paramref name="mode"/> and recomputes the rectangles.</summary>
    public void SetMode(EditorMode mode)
    {
        if (mode == Mode)
            return;
        Mode = mode;
        Recompute();
    }

    /// <summary>The window size (dp) the rectangles were computed for.</summary>
    public float WindowWidth { get; private set; }
    public float WindowHeight { get; private set; }

    public LayoutRect MenuBar { get; private set; }
    public LayoutRect Toolbar { get; private set; }
    public LayoutRect SceneTree { get; private set; }
    public LayoutRect FileSystem { get; private set; }
    public LayoutRect Viewport { get; private set; }
    public LayoutRect Inspector { get; private set; }
    public LayoutRect Output { get; private set; }

    /// <summary>Bumped whenever the rectangles change (panels re-apply their positions when it differs).</summary>
    public int Version { get; private set; }

    /// <summary>The default settings file: <c>~/.mainframe/editor_layout.json</c> (user profile on every OS).</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mainframe", "editor_layout.json");

    /// <summary>Recomputes the rectangles for a window of <paramref name="width"/> × <paramref name="height"/> dp.</summary>
    public void Update(float width, float height)
    {
        width = MathF.Max(width, 1);
        height = MathF.Max(height, 1);
        if (width == WindowWidth && height == WindowHeight && Version > 0)
            return;
        WindowWidth = width;
        WindowHeight = height;
        Recompute();
    }

    /// <summary>Whether the mode has no docks (video, music): the view spans the window.</summary>
    public bool IsFullWindow => Mode is EditorMode.Video or EditorMode.Music;

    /// <summary>Whether the output panel shows: always in game mode, else per mode (View › Show/Hide Output).</summary>
    public bool ShowsOutput => Mode switch
    {
        EditorMode.Video => Settings.VideoShowOutput,
        EditorMode.Music => Settings.MusicShowOutput,
        _ => true,
    };

    /// <summary>
    /// The splitter's hit/draw rectangle; empty when the mode has no such splitter (video and music mode: only Bottom,
    /// with the output).
    /// </summary>
    public LayoutRect SplitterRect(Splitter splitter) => (IsFullWindow, splitter) switch
    {
        (true, Splitter.Bottom) when ShowsOutput => new LayoutRect(Viewport.X, Viewport.Bottom, Viewport.Width, SplitterSize),
        (true, _) => default,
        (_, Splitter.Left) => new LayoutRect(SceneTree.Right, SceneTree.Y, SplitterSize, WindowHeight - SceneTree.Y),
        (_, Splitter.Right) => new LayoutRect(Inspector.X - SplitterSize, Inspector.Y, SplitterSize, Inspector.Height),
        (_, Splitter.Bottom) => new LayoutRect(Viewport.X, Viewport.Bottom, Viewport.Width, SplitterSize),
        (_, Splitter.LeftDock) => new LayoutRect(SceneTree.X, SceneTree.Bottom, SceneTree.Width, SplitterSize),
        _ => default,
    };

    /// <summary>
    /// Moves <paramref name="splitter"/> so its centre is at <paramref name="x"/>/<paramref name="y"/> (dp; the axis it
    /// moves along), clamped so every panel keeps its minimum size. Returns true when the layout changed.
    /// </summary>
    public bool DragSplitter(Splitter splitter, float x, float y)
    {
        var before = Settings;
        var content = WindowHeight - MenuBar.Height - Toolbar.Height;
        Settings = splitter switch
        {
            Splitter.Left => Settings with
            {
                LeftWidth = Math.Clamp(x - SplitterSize * 0.5f, MinDock, MathF.Max(MinDock, WindowWidth - Settings.RightWidth - MinViewportWidth - 2 * SplitterSize)),
            },
            Splitter.Right => Settings with
            {
                RightWidth = Math.Clamp(WindowWidth - x - SplitterSize * 0.5f, MinDock, MathF.Max(MinDock, WindowWidth - Settings.LeftWidth - MinViewportWidth - 2 * SplitterSize)),
            },
            Splitter.Bottom => Settings with
            {
                BottomHeight = Math.Clamp(WindowHeight - y - SplitterSize * 0.5f, MinBottom, MathF.Max(MinBottom, content - MinViewportHeight - SplitterSize - TabsHeight)),
            },
            Splitter.LeftDock => Settings with
            {
                LeftDockSplit = Math.Clamp((y - SceneTree.Y) / MathF.Max(1, content - SplitterSize), 0.15f, 0.9f),
            },
            _ => Settings,
        };
        if (before == Settings)
            return false;
        Recompute();
        return true;
    }

    /// <summary>Height of the scene tab strip at the top of the viewport panel.</summary>
    public const float TabsHeight = 30;

    /// <summary>The 3D view inside the viewport panel (below the scene tabs), in dp.</summary>
    public LayoutRect ViewportImage => new(Viewport.X, Viewport.Y + TabsHeight, Viewport.Width, MathF.Max(1, Viewport.Height - TabsHeight));

    /// <summary>Height of a UI preview's bar (linked files, backdrop controls) below the tab strip.</summary>
    public const float PreviewBarHeight = 30;

    /// <summary>The area a UI preview's document is laid out in (below the tabs and the preview bar), in dp.</summary>
    public LayoutRect PreviewImage
    {
        get
        {
            var view = ViewportImage;
            return new LayoutRect(view.X, view.Y + PreviewBarHeight, view.Width, MathF.Max(1, view.Height - PreviewBarHeight));
        }
    }

    /// <summary>Replaces the settings (the window size is kept) — e.g. after loading them.</summary>
    public void Apply(EditorLayoutSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Recompute();
    }

    /// <summary>Remembers the window size so the next start opens at it.</summary>
    public void RememberWindowSize(int width, int height) =>
        Settings = Settings with { WindowWidth = Math.Max(0, width), WindowHeight = Math.Max(0, height) };

    public void SetOutputFilter(int filter) => Settings = Settings with { OutputFilter = filter };

    /// <summary>The bottom dock's active tab.</summary>
    public void SetBottomTab(BottomDockTab tab) => Settings = Settings with { BottomTab = (int)tab };

    /// <summary>The inspector's name column width in dp (0: sized to the names).</summary>
    public void SetInspectorLabelWidth(float width) => Settings = Settings with { InspectorLabelWidth = MathF.Max(0, width) };

    public void SetOutputOptions(bool collapse, bool follow) => Settings = Settings with { OutputCollapse = collapse, OutputFollow = follow };

    /// <summary>The FileSystem panel's Simplified/Verbose toggle.</summary>
    public void SetFileSystemVerbose(bool verbose) => Settings = Settings with { FileSystemVerbose = verbose };

    public void SetSoundDesignerAutoPlay(bool autoPlay) => Settings = Settings with { SoundDesignerManualPlay = !autoPlay };

    /// <summary>Narrowest the video transcript gets (its splitter's lower stop).</summary>
    public const float MinVideoTranscriptWidth = 160;

    /// <summary>Lowest the video timeline gets: the ruler and a usable band of segments.</summary>
    public const float MinVideoTimelineHeight = 80;

    /// <summary>
    /// Video mode's transcript width in dp, clamped so the preview keeps <see cref="MinViewportWidth"/>. Video mode's view
    /// spans the window, so the bound comes from <see cref="Viewport"/>.
    /// </summary>
    public void SetVideoTranscriptWidth(float width) => Settings = Settings with
    {
        VideoTranscriptWidth = Math.Clamp(Finite(width, MinVideoTranscriptWidth), MinVideoTranscriptWidth,
            MathF.Max(MinVideoTranscriptWidth, Viewport.Width - MinViewportWidth - SplitterSize)),
    };

    /// <summary>Video mode's timeline height in dp, clamped so the preview keeps <see cref="MinViewportHeight"/>.</summary>
    public void SetVideoTimelineHeight(float height) => Settings = Settings with
    {
        VideoTimelineHeight = Math.Clamp(Finite(height, MinVideoTimelineHeight), MinVideoTimelineHeight,
            MathF.Max(MinVideoTimelineHeight, ViewportImage.Height - MinViewportHeight - SplitterSize)),
    };

    /// <summary>Lowest music mode's bottom pane gets (its splitter's lower stop).</summary>
    public const float MinMusicPaneHeight = 120;

    /// <summary>What music mode's track list keeps above the bottom pane: the ruler and about two tracks.</summary>
    public const float MinMusicTracksHeight = 96;

    /// <summary>The bottom pane's header (Piano roll | Mixer, the clip info, Hide) between its splitter and the pane.</summary>
    public const float MusicPaneHeaderHeight = 28;

    /// <summary>
    /// Highest music mode's bottom pane gets in the current window: the song view (below the tab strip) less the tracks'
    /// minimum, the pane header and the splitter.
    /// </summary>
    public float MaxMusicPaneHeight =>
        MathF.Max(MinMusicPaneHeight, ViewportImage.Height - MinMusicTracksHeight - MusicPaneHeaderHeight - SplitterSize);

    /// <summary>The bottom pane's height as shown: <see cref="EditorLayoutSettings.MusicPaneHeight"/> within this window's limits.</summary>
    public float MusicPaneShownHeight => Math.Clamp(Settings.MusicPaneHeight, MinMusicPaneHeight, MaxMusicPaneHeight);

    /// <summary>Music mode's bottom pane height in dp (its splitter), clamped to [<see cref="MinMusicPaneHeight"/>, <see cref="MaxMusicPaneHeight"/>].</summary>
    public void SetMusicPaneHeight(float height) => Settings = Settings with
    {
        MusicPaneHeight = Math.Clamp(Finite(height, MinMusicPaneHeight), MinMusicPaneHeight, MaxMusicPaneHeight),
    };

    /// <summary>The narrowest the music Browser gets, in dp.</summary>
    public const float MinMusicBrowserWidth = 200;

    /// <summary>The width the song view keeps beside the Browser, in dp.</summary>
    public const float MinMusicViewWidth = 400;

    /// <summary>The music Browser's widest, in this window.</summary>
    public float MaxMusicBrowserWidth => MathF.Max(MinMusicBrowserWidth, ViewportImage.Width - MinMusicViewWidth);

    /// <summary>The Browser's width as shown: <see cref="EditorLayoutSettings.MusicBrowserWidth"/> within this window's limits.</summary>
    public float MusicBrowserShownWidth => Math.Clamp(Settings.MusicBrowserWidth, MinMusicBrowserWidth, MaxMusicBrowserWidth);

    /// <summary>The Browser's width in dp (its splitter), clamped to [<see cref="MinMusicBrowserWidth"/>, <see cref="MaxMusicBrowserWidth"/>].</summary>
    public void SetMusicBrowserWidth(float width) => Settings = Settings with
    {
        MusicBrowserWidth = Math.Clamp(Finite(width, MinMusicBrowserWidth), MinMusicBrowserWidth, MaxMusicBrowserWidth),
    };

    /// <summary>Shows or hides the music Browser.</summary>
    public void SetMusicShowBrowser(bool show) => Settings = Settings with { MusicShowBrowser = show };

    /// <summary>Which tab the right panel shows: Adaptive (true) or Browser.</summary>
    public void SetMusicAdaptiveTab(bool adaptive) => Settings = Settings with { MusicAdaptiveTab = adaptive };

    /// <summary>The Browser's collapsed section and group ids.</summary>
    public void SetMusicBrowserCollapsed(IEnumerable<string> ids) =>
        Settings = Settings with { MusicBrowserCollapsed = [.. ids.Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)] };

    /// <summary>Music mode's bottom pane (P / X / Hide): the song panel's own layout follows (no rectangle here changes).</summary>
    public void SetMusicPane(SongBottomPanel pane) =>
        Settings = Settings with { MusicPane = Enum.IsDefined(pane) ? pane : SongBottomPanel.PianoRoll };

    /// <summary>View › Show/Hide Transcript: the panel's own layout follows (no rectangle here changes).</summary>
    public void SetVideoShowTranscript(bool show) => Settings = Settings with { VideoHideTranscript = !show };

    /// <summary>View › Show/Hide Output in video mode: the output panel's rectangle appears or goes.</summary>
    public void SetVideoShowOutput(bool show)
    {
        if (Settings.VideoShowOutput == show)
            return;
        Settings = Settings with { VideoShowOutput = show };
        Recompute();
    }

    /// <summary>View › Show/Hide Output in music mode: the output panel's rectangle appears or goes.</summary>
    public void SetMusicShowOutput(bool show)
    {
        if (Settings.MusicShowOutput == show)
            return;
        Settings = Settings with { MusicShowOutput = show };
        Recompute();
    }

    /// <summary>Most entries a picker's recent list keeps.</summary>
    public const int MaxRecent = 8;

    /// <summary>The favourites of picker <paramref name="kind"/>.</summary>
    public IReadOnlyList<string> PickerFavorites(string kind) =>
        Settings.PickerFavorites?.GetValueOrDefault(kind) ?? [];

    /// <summary>The recent entries of picker <paramref name="kind"/>, newest first.</summary>
    public IReadOnlyList<string> PickerRecent(string kind) =>
        Settings.PickerRecent?.GetValueOrDefault(kind) ?? [];

    /// <summary>Adds or removes <paramref name="id"/> from picker <paramref name="kind"/>'s favourites; true when it is now one.</summary>
    public bool ToggleFavorite(string kind, string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(id);
        var list = PickerFavorites(kind).ToList();
        var added = !list.Remove(id);
        if (added)
            list.Add(id);
        Settings = Settings with { PickerFavorites = With(Settings.PickerFavorites, kind, [.. list]) };
        return added;
    }

    /// <summary>Moves <paramref name="id"/> to the front of picker <paramref name="kind"/>'s recent list (at most <see cref="MaxRecent"/>).</summary>
    public void AddRecent(string kind, string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(id);
        var list = PickerRecent(kind).Where(r => !string.Equals(r, id, StringComparison.Ordinal)).Prepend(id).Take(MaxRecent).ToArray();
        Settings = Settings with { PickerRecent = With(Settings.PickerRecent, kind, list) };
    }

    private static Dictionary<string, string[]> With(Dictionary<string, string[]>? map, string kind, string[] list)
    {
        var copy = map is null ? new Dictionary<string, string[]>(StringComparer.Ordinal) : new Dictionary<string, string[]>(map, StringComparer.Ordinal);
        copy[kind] = list;
        return copy;
    }

    private void Recompute()
    {
        var w = WindowWidth;
        var h = WindowHeight;
        MenuBar = new LayoutRect(0, 0, w, MenuBarHeight);
        Toolbar = new LayoutRect(0, MenuBarHeight, w, ToolbarHeight);
        var top = MenuBarHeight + ToolbarHeight;
        var content = MathF.Max(1, h - top);
        if (IsFullWindow)
        {
            RecomputeFullWindow(w, top, content);
            return;
        }

        // Docks shrink before the viewport disappears on small windows.
        var left = Math.Clamp(Settings.LeftWidth, MinDock, MathF.Max(MinDock, (w - MinViewportWidth) * 0.5f - SplitterSize));
        var right = Math.Clamp(Settings.RightWidth, MinDock, MathF.Max(MinDock, w - left - MinViewportWidth - 2 * SplitterSize));
        var centerX = left + SplitterSize;
        var centerWidth = MathF.Max(1, w - left - right - 2 * SplitterSize);
        var bottom = Math.Clamp(Settings.BottomHeight, MinBottom, MathF.Max(MinBottom, content - MinViewportHeight - SplitterSize));
        var viewportHeight = MathF.Max(1, content - bottom - SplitterSize);

        var treeHeight = MathF.Max(40, MathF.Round((content - SplitterSize) * Settings.LeftDockSplit));
        SceneTree = new LayoutRect(0, top, left, treeHeight);
        FileSystem = new LayoutRect(0, top + treeHeight + SplitterSize, left, MathF.Max(1, content - treeHeight - SplitterSize));
        Viewport = new LayoutRect(centerX, top, centerWidth, viewportHeight);
        Output = new LayoutRect(centerX, top + viewportHeight + SplitterSize, centerWidth, bottom);
        Inspector = new LayoutRect(w - right, top, right, content);
        Version++;
    }

    // Video and music mode: no docks; the viewport panel spans the window, the output panel (when shown) below it.
    private void RecomputeFullWindow(float w, float top, float content)
    {
        var showOutput = ShowsOutput;
        var bottom = showOutput ? Math.Clamp(Settings.BottomHeight, MinBottom, MathF.Max(MinBottom, content - MinViewportHeight - SplitterSize)) : 0;
        SceneTree = default;
        FileSystem = default;
        Inspector = default;
        Viewport = new LayoutRect(0, top, w, MathF.Max(1, content - (showOutput ? bottom + SplitterSize : 0)));
        Output = showOutput ? new LayoutRect(0, top + Viewport.Height + SplitterSize, w, bottom) : default;
        Version++;
    }

    // ── Persistence ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads settings from <paramref name="path"/>; defaults when the file is missing, unreadable or from a newer format.</summary>
    public static EditorLayoutSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            if (!File.Exists(path))
                return new EditorLayoutSettings();
            var settings = JsonSerializer.Deserialize(File.ReadAllBytes(path), EditorJsonContext.Default.EditorLayoutSettings);
            if (settings is null || settings.Format > EditorLayoutSettings.CurrentFormat)
                return new EditorLayoutSettings();
            return Sanitize(settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning($"[Editor] Ignoring the layout file '{path}': {e.Message}");
            return new EditorLayoutSettings();
        }
    }

    /// <summary>Writes settings to <paramref name="path"/> atomically (temp file + rename); false (logged) on failure.</summary>
    public static bool Save(string path, EditorLayoutSettings settings)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(settings, EditorJsonContext.Default.EditorLayoutSettings));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Editor] Could not save the layout to '{path}': {e.Message}");
            return false;
        }
    }

    // Hand-edited or corrupted values must not produce an unusable layout.
    private static EditorLayoutSettings Sanitize(EditorLayoutSettings s) => s with
    {
        LeftWidth = Finite(s.LeftWidth, 270),
        RightWidth = Finite(s.RightWidth, 330),
        BottomHeight = Finite(s.BottomHeight, 190),
        LeftDockSplit = Math.Clamp(Finite(s.LeftDockSplit, 0.62f), 0.15f, 0.9f),
        WindowWidth = Math.Clamp(s.WindowWidth, 0, 16384),
        WindowHeight = Math.Clamp(s.WindowHeight, 0, 16384),
        OutputFilter = s.OutputFilter ?? EditorLayoutSettings.DefaultOutputFilter,
        OutputFollow = s.OutputFollow ?? true,
        PickerFavorites = CleanLists(s.PickerFavorites, int.MaxValue),
        PickerRecent = CleanLists(s.PickerRecent, MaxRecent),
        VideoTranscriptWidth = Finite(s.VideoTranscriptWidth, 320),
        VideoTimelineHeight = Finite(s.VideoTimelineHeight, 180),
        MusicPane = Enum.IsDefined(s.MusicPane) ? s.MusicPane : SongBottomPanel.PianoRoll,
        MusicPaneHeight = Finite(s.MusicPaneHeight, 300),
        MusicBrowserWidth = Finite(s.MusicBrowserWidth, 260),
        MusicBrowserCollapsed = s.MusicBrowserCollapsed?.Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Take(64).ToArray(),
    };

    // Drops null lists and blank ids (hand-edited files); caps lengths.
    private static Dictionary<string, string[]>? CleanLists(Dictionary<string, string[]>? map, int max)
    {
        if (map is null)
            return null;
        var clean = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (kind, list) in map)
            if (!string.IsNullOrEmpty(kind) && list is not null)
                clean[kind] = [.. list.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Take(max)];
        return clean;
    }

    private static float Finite(float value, float fallback) => float.IsFinite(value) && value > 0 ? value : fallback;
}

/// <summary>Source-generated JSON metadata for the editor's settings files (no reflection, trim-safe).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(EditorLayoutSettings))]
internal sealed partial class EditorJsonContext : JsonSerializerContext;
