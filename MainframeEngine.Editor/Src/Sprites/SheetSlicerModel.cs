using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MainframeEngine.Editor;

/// <summary>How the Select Frames dialog sizes its grid (Godot's dominant parameter).</summary>
public enum SheetSliceMode
{
    /// <summary>Horizontal × vertical cell counts; the cell size follows.</summary>
    Counts,

    /// <summary>A cell size in pixels; the counts follow.</summary>
    Size,
}

/// <summary>The grid settings the dialog remembers per sheet.</summary>
public sealed record SheetGridSettings(SheetSliceMode Mode, int Columns, int Rows, int CellWidth, int CellHeight,
    int SeparationX, int SeparationY, int OffsetX, int OffsetY);

/// <summary>
/// The Select Frames dialog's model (Godot's sprite sheet dialog, ADR 0224): the grid from counts or a cell size, with
/// separation and offset (margins), and the cells picked, in the order they were clicked (that is the frame order).
/// Pure logic, so it is unit-tested; <see cref="SheetSlicerDialog"/> is the view.
/// </summary>
public sealed class SheetSlicerModel
{
    private readonly List<Vector2I> _selected = [];

    public SheetSlicerModel(Vector2I imageSize, SheetGridSettings? settings = null)
    {
        ImageSize = new Vector2I(Math.Max(1, imageSize.X), Math.Max(1, imageSize.Y));
        if (settings is not null)
            Apply(settings);
        else
            SetCounts(4, 4); // Godot's default split
    }

    public Vector2I ImageSize { get; }

    public SheetSliceMode Mode { get; private set; }

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public Vector2I CellSize { get; private set; }

    public Vector2I Separation { get; private set; }

    public Vector2I Offset { get; private set; }

    /// <summary>The grid the cells are cut from.</summary>
    public SpriteSheetGrid Grid => new(CellSize, Offset, Separation);

    /// <summary>The picked cells in click order.</summary>
    public IReadOnlyList<Vector2I> Selected => _selected;

    /// <summary>The dialog's primary button: "Add 3 Frames".</summary>
    public string AddLabel => _selected.Count == 1 ? "Add 1 Frame" : $"Add {_selected.Count.ToString(CultureInfo.InvariantCulture)} Frames";

    /// <summary>The settings to remember for this sheet.</summary>
    public SheetGridSettings Settings =>
        new(Mode, Columns, Rows, CellSize.X, CellSize.Y, Separation.X, Separation.Y, Offset.X, Offset.Y);

    /// <summary>Splits the image into <paramref name="columns"/> × <paramref name="rows"/> cells.</summary>
    public void SetCounts(int columns, int rows)
    {
        Mode = SheetSliceMode.Counts;
        Columns = Math.Clamp(columns, 1, 1024);
        Rows = Math.Clamp(rows, 1, 1024);
        Update();
    }

    /// <summary>Cuts cells of <paramref name="size"/> pixels.</summary>
    public void SetCellSize(Vector2I size)
    {
        Mode = SheetSliceMode.Size;
        CellSize = new Vector2I(Math.Clamp(size.X, 1, ImageSize.X), Math.Clamp(size.Y, 1, ImageSize.Y));
        Update();
    }

    public void SetSeparation(Vector2I separation)
    {
        Separation = new Vector2I(Math.Clamp(separation.X, 0, ImageSize.X), Math.Clamp(separation.Y, 0, ImageSize.Y));
        Update();
    }

    public void SetOffset(Vector2I offset)
    {
        Offset = new Vector2I(Math.Clamp(offset.X, 0, ImageSize.X - 1), Math.Clamp(offset.Y, 0, ImageSize.Y - 1));
        Update();
    }

    public void Apply(SheetGridSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Separation = new Vector2I(Math.Max(0, settings.SeparationX), Math.Max(0, settings.SeparationY));
        Offset = new Vector2I(Math.Max(0, settings.OffsetX), Math.Max(0, settings.OffsetY));
        if (settings.Mode == SheetSliceMode.Size)
            SetCellSize(new Vector2I(settings.CellWidth, settings.CellHeight));
        else
            SetCounts(settings.Columns, settings.Rows);
    }

    /// <summary>Picks <paramref name="cell"/> (appended: the next frame) or unpicks it.</summary>
    public void Toggle(Vector2I cell)
    {
        if (!Contains(cell))
            return;
        if (!_selected.Remove(cell))
            _selected.Add(cell);
    }

    /// <summary>Picks every cell not yet picked, row by row (Godot's Select All).</summary>
    public void SelectAll()
    {
        for (var y = 0; y < Rows; y++)
            for (var x = 0; x < Columns; x++)
                if (!_selected.Contains(new Vector2I(x, y)))
                    _selected.Add(new Vector2I(x, y));
    }

    public void Clear() => _selected.Clear();

    /// <summary>The 1-based frame number <paramref name="cell"/> will get, or 0 when it is not picked.</summary>
    public int NumberOf(Vector2I cell) => _selected.IndexOf(cell) + 1;

    public bool Contains(Vector2I cell) => cell.X >= 0 && cell.Y >= 0 && cell.X < Columns && cell.Y < Rows;

    // The non-dominant side follows (Godot's _sheet_spin_changed), and picks outside the new grid are dropped.
    private void Update()
    {
        if (Mode == SheetSliceMode.Counts)
            CellSize = SpriteSheetGrid.FromCounts(ImageSize, Columns, Rows, Offset, Separation).CellSize;
        else
        {
            var size = Grid.GridSize(ImageSize);
            (Columns, Rows) = (Math.Max(1, size.X), Math.Max(1, size.Y));
        }

        _selected.RemoveAll(c => !Contains(c));
    }
}

/// <summary>
/// The last grid used per sheet, in <c>&lt;project&gt;/.mainframe/cache/sheet_grids.json</c> (editor state beside the
/// thumbnail cache; not committed). Keys are project paths.
/// </summary>
public static class SheetGridMemory
{
    public static string FileOf(string projectRoot) => Path.Combine(projectRoot, ".mainframe", "cache", "sheet_grids.json");

    public static SheetGridSettings? Get(string? projectRoot, string projectPath)
    {
        if (projectRoot is null)
            return null;
        return Read(projectRoot).TryGetValue(projectPath, out var settings) ? settings : null;
    }

    public static void Set(string? projectRoot, string projectPath, SheetGridSettings settings)
    {
        if (projectRoot is null)
            return;
        var all = Read(projectRoot);
        all[projectPath] = settings;
        try
        {
            var file = FileOf(projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(all, SheetGridJson.Default.DictionaryStringSheetGridSettings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Editor] Could not remember the sheet grid: {e.Message}");
        }
    }

    private static Dictionary<string, SheetGridSettings> Read(string projectRoot)
    {
        try
        {
            var file = FileOf(projectRoot);
            if (File.Exists(file) && JsonSerializer.Deserialize(File.ReadAllText(file), SheetGridJson.Default.DictionaryStringSheetGridSettings) is { } read)
                return new Dictionary<string, SheetGridSettings>(read, StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged cache is only forgotten settings.
        }

        return new Dictionary<string, SheetGridSettings>(StringComparer.Ordinal);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Dictionary<string, SheetGridSettings>))]
internal sealed partial class SheetGridJson : JsonSerializerContext;
