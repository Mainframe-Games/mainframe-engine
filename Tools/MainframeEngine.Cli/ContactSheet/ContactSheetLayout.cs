using System.Globalization;

namespace MainframeEngine.Cli.ContactSheet;

/// <summary>A camera of a contact sheet row.</summary>
internal enum SheetView : byte
{
    Front,
    Side,
    ThreeQuarter,
}

/// <summary>One column of a contact sheet: a time of the clip, and whether every track has a key there.</summary>
internal readonly record struct SheetFrame(float Time, bool IsKey)
{
    public string Label => $"{Time.ToString("0.###", CultureInfo.InvariantCulture)} s{(IsKey ? "" : " ·")}";
}

/// <summary>A rectangle in sheet pixels.</summary>
internal readonly record struct SheetRect(int X, int Y, int Width, int Height);

/// <summary>
/// Where everything of a contact sheet goes (G1.17): the header, one row per view and one column per frame (or, for one
/// frame, the views side by side), the trajectory strip and the check report; and the size cells are rendered at.
/// </summary>
internal sealed class ContactSheetLayout
{
    public const int MaxFrames = 12;
    public const int Margin = 16;
    public const int HeaderHeight = 58;
    public const int LabelWidth = 96;
    public const int ColumnLabelHeight = 22;
    public const int Gap = 6;
    public const int StripHeight = 170;
    public const int ReportLineHeight = 18;
    public const float CellAspect = 4f / 3f; // height / width

    private ContactSheetLayout(IReadOnlyList<SheetView> views, IReadOnlyList<SheetFrame> frames, bool single, int width, int cellWidth,
        int reportLines)
    {
        Views = views;
        Frames = frames;
        Single = single;
        Width = width;
        CellWidth = cellWidth;
        CellHeight = (int)MathF.Round(cellWidth * CellAspect);
        var gridTop = Margin + HeaderHeight + ColumnLabelHeight;
        var rows = single ? 1 : views.Count;
        Strip = new SheetRect(Margin, gridTop + rows * (CellHeight + Gap) + Gap, width - 2 * Margin, StripHeight);
        Report = new SheetRect(Margin, Strip.Y + Strip.Height + Gap * 2, width - 2 * Margin, (reportLines + 1) * ReportLineHeight);
        Height = Report.Y + Report.Height + Margin;
    }

    public IReadOnlyList<SheetView> Views { get; }

    public IReadOnlyList<SheetFrame> Frames { get; }

    /// <summary>One frame, the views side by side (<c>--frame</c>).</summary>
    public bool Single { get; }

    public int Width { get; }

    public int Height { get; }

    public int CellWidth { get; }

    public int CellHeight { get; }

    public SheetRect Strip { get; }

    public SheetRect Report { get; }

    public SheetRect Header => new(Margin, Margin, Width - 2 * Margin, HeaderHeight);

    /// <summary>The cell of <paramref name="view"/> at frame <paramref name="frame"/> (indices into the lists).</summary>
    public SheetRect Cell(int view, int frame)
    {
        var top = Margin + HeaderHeight + ColumnLabelHeight;
        var left = Margin + LabelWidth;
        return Single
            ? new SheetRect(left + view * (CellWidth + Gap), top, CellWidth, CellHeight)
            : new SheetRect(left + frame * (CellWidth + Gap), top + view * (CellHeight + Gap), CellWidth, CellHeight);
    }

    /// <summary>Every cell, in the order they are rendered.</summary>
    public IEnumerable<(int View, int Frame)> Cells()
    {
        for (var view = 0; view < Views.Count; view++)
            for (var frame = 0; frame < Frames.Count; frame++)
                yield return (view, frame);
    }

    /// <summary>The size a cell is rendered at: twice its size (box-filtered down), at most 1 024 pixels high.</summary>
    public (int Width, int Height) RenderSize
    {
        get
        {
            var scale = Math.Min(2f, 1024f / CellHeight);
            return (Math.Max(64, (int)(CellWidth * scale)), Math.Max(64, (int)(CellHeight * scale)));
        }
    }

    public static ContactSheetLayout Create(IReadOnlyList<SheetView> views, IReadOnlyList<SheetFrame> frames, int width, int reportLines)
    {
        ArgumentOutOfRangeException.ThrowIfZero(views.Count);
        ArgumentOutOfRangeException.ThrowIfZero(frames.Count);
        var single = frames.Count == 1;
        var columns = single ? views.Count : frames.Count;
        var cellWidth = Math.Max(48, (width - 2 * Margin - LabelWidth - (columns - 1) * Gap) / columns);
        var sheetWidth = Math.Max(width, 2 * Margin + LabelWidth + columns * cellWidth + (columns - 1) * Gap);
        return new ContactSheetLayout(views, frames, single, sheetWidth, cellWidth, reportLines);
    }

    /// <summary>
    /// The frames: <paramref name="single"/> alone; else <paramref name="count"/> times spaced from 0 to the length; else the
    /// key times every track has (refinement-only times left out), thinned evenly to <see cref="MaxFrames"/>.
    /// </summary>
    public static IReadOnlyList<SheetFrame> ChooseFrames(ClipDescription keys, int? count, float? single)
    {
        ArgumentNullException.ThrowIfNull(keys);
        bool IsKey(float t) => keys.Times.Any(k => k.IsKey && k.PartialKeys.Count == 0 && MathF.Abs(k.Time - t) < 1e-3f);
        if (single is { } time)
            return [new SheetFrame(Math.Clamp(time, 0f, keys.Length), IsKey(time))];
        if (count is { } n)
        {
            if (n == 1)
                return [new SheetFrame(0f, IsKey(0f))];
            return [.. Enumerable.Range(0, n).Select(i => keys.Length * i / (n - 1)).Select(t => new SheetFrame(t, IsKey(t)))];
        }

        var times = keys.Times.Where(k => k.IsKey && k.PartialKeys.Count == 0).Select(k => k.Time).ToList();
        if (times.Count == 0)
            times = [0f, keys.Length];
        if (times.Count > MaxFrames)
            times = [.. Enumerable.Range(0, MaxFrames).Select(i => times[(int)MathF.Round(i * (times.Count - 1) / (float)(MaxFrames - 1))])];
        return [.. times.Select(t => new SheetFrame(t, true))];
    }

    public static string Name(SheetView view) => view switch
    {
        SheetView.Front => "front",
        SheetView.Side => "side",
        _ => "threequarter",
    };

    public static bool TryParse(string name, out SheetView view)
    {
        (var ok, view) = name switch
        {
            "front" => (true, SheetView.Front),
            "side" => (true, SheetView.Side),
            "threequarter" or "three-quarter" => (true, SheetView.ThreeQuarter),
            _ => (false, SheetView.Front),
        };
        return ok;
    }
}
