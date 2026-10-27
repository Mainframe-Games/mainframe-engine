using System.Globalization;
using System.Text;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The Select Frames dialog (<c>sheet_slicer.rml</c>, Godot's sprite sheet dialog; ADR 0224): a sprite sheet with its
/// grid, Horizontal/Vertical counts or a cell Size, Separation and Offset, cells picked by clicking (numbered in click
/// order), Select All and Clear, and "Add N Frames". The grid is remembered per sheet (<see cref="SheetGridMemory"/>).
/// </summary>
public sealed class SheetSlicerDialog : EditorDocument
{
    private const float MaxViewWidth = 640f, MaxViewHeight = 400f; // dp

    private string _imagePath = "";
    private string _projectPath = "";
    private string _title = "";
    private Action<SpriteSheetGrid, IReadOnlyList<Vector2I>>? _onAdd;
    private RmlEventListener? _change;
    private RmlEventListener? _blur;
    private RmlEventListener? _key;

    public SheetSlicerDialog(EditorWorkspace workspace)
        : base(workspace, "sheet_slicer.rml")
    {
        Visible = false;
        Modal = true;
    }

    /// <summary>The open dialog's grid and picks (null while closed).</summary>
    public SheetSlicerModel? Model { get; private set; }

    /// <summary>
    /// Opens the dialog on the image at <paramref name="projectPath"/> (<paramref name="imageSize"/> pixels); "Add N
    /// Frames" calls <paramref name="onAdd"/> with the grid and the picked cells in order.
    /// </summary>
    public void Show(string projectPath, Vector2I imageSize, string title, Action<SpriteSheetGrid, IReadOnlyList<Vector2I>> onAdd)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);
        ArgumentNullException.ThrowIfNull(onAdd);
        _projectPath = projectPath;
        _imagePath = AssetDatabase.Current.ToAbsolutePath(projectPath);
        _title = title;
        _onAdd = onAdd;
        Model = new SheetSlicerModel(imageSize, SheetGridMemory.Get(Workspace.Session.ProjectRoot, projectPath));
        Visible = true;
        Render();
    }

    /// <summary>Closes without adding.</summary>
    public void Cancel()
    {
        Model = null;
        _onAdd = null;
        HideAndReleaseFocus();
    }

    /// <summary>"Add N Frames": remembers the grid, closes and hands the picks over (nothing when none are picked).</summary>
    public void Confirm()
    {
        if (Model is not { } model || _onAdd is not { } onAdd)
            return;
        if (model.Selected.Count == 0)
        {
            SetText("slicer-error", "Click cells to pick frames first.");
            return;
        }

        SheetGridMemory.Set(Workspace.Session.ProjectRoot, _projectPath, model.Settings);
        var (grid, cells) = (model.Grid, model.Selected.ToArray());
        Cancel();
        onAdd(grid, cells);
    }

    /// <summary>Applies a field (<c>cols</c>, <c>rows</c>, <c>size-x</c>, <c>size-y</c>, <c>sep-x</c>, <c>sep-y</c>, <c>off-x</c>, <c>off-y</c>).</summary>
    public void SetField(string field, int value)
    {
        if (Model is not { } m)
            return;
        switch (field)
        {
            case "cols": m.SetCounts(value, m.Rows); break;
            case "rows": m.SetCounts(m.Columns, value); break;
            case "size-x": m.SetCellSize(new Vector2I(value, m.CellSize.Y)); break;
            case "size-y": m.SetCellSize(new Vector2I(m.CellSize.X, value)); break;
            case "sep-x": m.SetSeparation(new Vector2I(value, m.Separation.Y)); break;
            case "sep-y": m.SetSeparation(new Vector2I(m.Separation.X, value)); break;
            case "off-x": m.SetOffset(new Vector2I(value, m.Offset.Y)); break;
            case "off-y": m.SetOffset(new Vector2I(m.Offset.X, value)); break;
            default: return;
        }

        Render();
    }

    protected override void OnAttach(RmlDocument document)
    {
        _change?.Remove();
        _blur?.Remove();
        _key?.Remove();
        var root = document.AsElement();
        _change = root.AddEventListener("change", OnChange);
        _blur = root.AddEventListener("blur", OnBlur, inCapturePhase: true);
        _key = root.AddEventListener("keydown", OnKeyDown);
        if (Visible)
            Render();
    }

    protected override void OnClickElement(RmlEvent e)
    {
        if (Model is not { } model)
            return;
        if (FindAttribute(e.Target, "data-cell") is { } cell && TryCell(cell, out var at))
        {
            model.Toggle(at);
            Render();
            return;
        }

        switch (FindAttribute(e.Target, "data-op"))
        {
            case "counts":
                model.SetCounts(model.Columns, model.Rows);
                break;
            case "size":
                model.SetCellSize(model.CellSize);
                break;
            case "all":
                model.SelectAll();
                break;
            case "clear":
                model.Clear();
                break;
            case "cancel":
                Cancel();
                return;
            case "add":
                Confirm();
                return;
            default:
                return;
        }

        Render();
    }

    private void OnChange(RmlEvent e)
    {
        if (e.GetParameter("linebreak", false))
            CommitField(e.Target);
    }

    private void OnBlur(RmlEvent e) => CommitField(e.Target);

    private void CommitField(RmlElement target)
    {
        if (target.GetAttribute("data-field") is not { } field || Model is null)
            return;
        if (int.TryParse(target.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            SetField(field, value);
        else
            Render(); // put the valid value back
    }

    private void OnKeyDown(RmlEvent e)
    {
        var key = (RmlKey)e.GetParameter("key_identifier", 0);
        if (key == RmlKey.Escape)
            Cancel();
    }

    private static bool TryCell(string text, out Vector2I cell)
    {
        cell = default;
        var comma = text.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || !int.TryParse(text.AsSpan(0, comma), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
            !int.TryParse(text.AsSpan(comma + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            return false;
        cell = new Vector2I(x, y);
        return true;
    }

    private void Render()
    {
        if (Model is not { } m || !EnsureLoaded())
            return;
        SetText("slicer-title", _title);
        SetText("slicer-error", "");
        var fields = Document.GetElementById("slicer-fields");
        if (!fields.IsNull)
            fields.SetInnerRml(FieldsRml(m));
        var view = Document.GetElementById("slicer-view");
        if (!view.IsNull)
            view.SetInnerRml(GridRml(m, _imagePath));
        var add = Document.GetElementById("slicer-add");
        if (!add.IsNull)
            add.SetInnerRml(RmlText.Escape(m.AddLabel));
    }

    internal static string FieldsRml(SheetSlicerModel m)
    {
        var rml = new StringBuilder(2048);
        var counts = m.Mode == SheetSliceMode.Counts;
        rml.Append("<button class=\"tool-button small").Append(counts ? " active" : "")
            .Append("\" data-op=\"counts\" data-tooltip=\"Split by count — Horizontal × Vertical cells; the cell size follows\"><span class=\"icon icon-sm icon-hash\"></span></button>")
            .Append("<button class=\"tool-button small").Append(counts ? "" : " active")
            .Append("\" data-op=\"size\" data-tooltip=\"Split by size — cells of a fixed size in pixels; the counts follow\"><span class=\"icon icon-sm icon-ruler-2\"></span></button>");
        Pair(rml, "Cells", "Horizontal", "Vertical", "cols", "rows", m.Columns, m.Rows, "Cells across × down");
        Pair(rml, "Size", "Size W", "Size H", "size-x", "size-y", m.CellSize.X, m.CellSize.Y, "One cell in pixels");
        Pair(rml, "Sep", "Sep X", "Sep Y", "sep-x", "sep-y", m.Separation.X, m.Separation.Y, "Separation — gap between cells in pixels");
        Pair(rml, "Offset", "Offset X", "Offset Y", "off-x", "off-y", m.Offset.X, m.Offset.Y, "Offset — margin before the first cell in pixels");
        rml.Append("<span class=\"slicer-gap\"></span>")
            .Append("<button class=\"tool-button small\" data-op=\"all\" data-tooltip=\"Select All — pick every cell, row by row\"><span class=\"icon icon-sm icon-select-all\"></span></button>")
            .Append("<button class=\"tool-button small\" data-op=\"clear\" data-tooltip=\"Clear — unpick every cell\"><span class=\"icon icon-sm icon-clear-all\"></span></button>");
        return rml.ToString();
    }

    private static void Pair(StringBuilder rml, string caption, string labelA, string labelB, string a, string b, int va, int vb, string tooltip)
    {
        rml.Append("<span class=\"slicer-pair\" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\"><span class=\"slicer-caption\">")
            .Append(caption).Append("</span>");
        Field(rml, labelA, a, va);
        Field(rml, labelB, b, vb);
        rml.Append("</span>");
    }

    private static void Field(StringBuilder rml, string label, string field, int value) =>
        rml.Append("<input type=\"text\" class=\"text slicer-field\" data-field=\"").Append(field).Append("\" title=\"").Append(label)
            .Append("\" value=\"").Append(value.ToString(CultureInfo.InvariantCulture)).Append("\"/>");

    /// <summary>The sheet scaled to fit the view, its cells as clickable boxes, picked ones numbered.</summary>
    internal static string GridRml(SheetSlicerModel m, string imagePath)
    {
        var zoom = MathF.Min(MaxViewWidth / m.ImageSize.X, MaxViewHeight / m.ImageSize.Y);
        zoom = zoom >= 1f ? MathF.Floor(MathF.Min(zoom, 16f)) : MathF.Max(zoom, 0.05f);
        var rml = new StringBuilder(256 + m.Columns * m.Rows * 160);
        rml.Append("<div class=\"slicer-sheet\" style=\"width: ").Append(RmlText.Dp(m.ImageSize.X * zoom)).Append("; height: ")
            .Append(RmlText.Dp(m.ImageSize.Y * zoom)).Append(";\"><img class=\"slicer-image\" src=\"").Append(RmlText.Escape(imagePath))
            .Append("\" style=\"width: ").Append(RmlText.Dp(m.ImageSize.X * zoom)).Append("; height: ").Append(RmlText.Dp(m.ImageSize.Y * zoom)).Append(";\"/>");
        var grid = m.Grid;
        for (var y = 0; y < m.Rows; y++)
            for (var x = 0; x < m.Columns; x++)
            {
                var cell = new Vector2I(x, y);
                var r = grid.CellRect(cell);
                var number = m.NumberOf(cell);
                rml.Append("<div class=\"slicer-cell").Append(number > 0 ? " picked" : "").Append("\" data-cell=\"")
                    .Append(x.ToString(CultureInfo.InvariantCulture)).Append(',').Append(y.ToString(CultureInfo.InvariantCulture))
                    .Append("\" style=\"left: ").Append(RmlText.Dp(r.Position.X * zoom)).Append("; top: ").Append(RmlText.Dp(r.Position.Y * zoom))
                    .Append("; width: ").Append(RmlText.Dp(r.Size.X * zoom)).Append("; height: ").Append(RmlText.Dp(r.Size.Y * zoom)).Append(";\">");
                if (number > 0)
                    rml.Append("<span class=\"slicer-number\">").Append(number.ToString(CultureInfo.InvariantCulture)).Append("</span>");
                rml.Append("</div>");
            }

        return rml.Append("</div>").ToString();
    }
}
