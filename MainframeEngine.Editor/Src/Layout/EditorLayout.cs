using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>The output panel's level filter (bit per <see cref="OutputLevel"/>).</summary>
    public int OutputFilter { get; init; } = 0b1111;

    /// <summary>The inspector's property-name column width in dp; 0 sizes it to the longest name.</summary>
    public float InspectorLabelWidth { get; init; }

    /// <summary>The output panel folds repeated lines into one with a ×N badge.</summary>
    public bool OutputCollapse { get; init; }

    /// <summary>The output panel scrolls to each new line (off: the scroll position stays).</summary>
    public bool OutputFollow { get; init; } = true;

    /// <summary>The sound designer (ZzfxStream inspector) replays the sound after every committed change.</summary>
    public bool SoundDesignerAutoPlay { get; init; } = true;

    /// <summary>Favourite entries per picker (<c>node</c>, <c>resource</c>, <c>scene</c>): type names or project paths.</summary>
    public Dictionary<string, string[]>? PickerFavorites { get; init; }

    /// <summary>Recently created entries per picker, newest first.</summary>
    public Dictionary<string, string[]>? PickerRecent { get; init; }
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

    /// <summary>The splitter's hit/draw rectangle.</summary>
    public LayoutRect SplitterRect(Splitter splitter) => splitter switch
    {
        Splitter.Left => new LayoutRect(SceneTree.Right, SceneTree.Y, SplitterSize, WindowHeight - SceneTree.Y),
        Splitter.Right => new LayoutRect(Inspector.X - SplitterSize, Inspector.Y, SplitterSize, Inspector.Height),
        Splitter.Bottom => new LayoutRect(Viewport.X, Viewport.Bottom, Viewport.Width, SplitterSize),
        Splitter.LeftDock => new LayoutRect(SceneTree.X, SceneTree.Bottom, SceneTree.Width, SplitterSize),
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

    /// <summary>The inspector's name column width in dp (0: sized to the names).</summary>
    public void SetInspectorLabelWidth(float width) => Settings = Settings with { InspectorLabelWidth = MathF.Max(0, width) };

    public void SetOutputOptions(bool collapse, bool follow) => Settings = Settings with { OutputCollapse = collapse, OutputFollow = follow };

    public void SetSoundDesignerAutoPlay(bool autoPlay) => Settings = Settings with { SoundDesignerAutoPlay = autoPlay };

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
        PickerFavorites = CleanLists(s.PickerFavorites, int.MaxValue),
        PickerRecent = CleanLists(s.PickerRecent, MaxRecent),
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
