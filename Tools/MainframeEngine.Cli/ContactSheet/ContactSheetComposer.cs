using System.Globalization;

namespace MainframeEngine.Cli.ContactSheet;

/// <summary>One sample of the trajectory strip: heights above the ground (metres) and the feet's contacts.</summary>
internal readonly record struct StripSample(float Time, float Hips, float LeftFoot, float RightFoot, float LeftHand, float RightHand,
    bool LeftContact, bool RightContact);

/// <summary>What a contact sheet says besides its cells.</summary>
internal sealed class ContactSheetContent
{
    public required string Title { get; init; }

    public required string Subtitle { get; init; }

    public required float Length { get; init; }

    public required IReadOnlyList<StripSample> Strip { get; init; }

    public required IReadOnlyList<string> Report { get; init; }

    /// <summary>The cells' pictures by (view, frame); a missing one is drawn as an empty, crossed cell.</summary>
    public required IReadOnlyDictionary<(int View, int Frame), FrameCapture> Cells { get; init; }
}

/// <summary>Draws a contact sheet (G1.17) from its layout, its cells and its text into a <see cref="SheetCanvas"/>.</summary>
internal static class ContactSheetComposer
{
    public static readonly Rgba Background = Rgba.Hex(0x1E2126);
    public static readonly Rgba Panel = Rgba.Hex(0x272B31);
    public static readonly Rgba Text = Rgba.Hex(0xE6E8EB);
    public static readonly Rgba Dim = Rgba.Hex(0x9AA1AB);
    public static readonly Rgba Grid = Rgba.Hex(0x3A3F47);
    public static readonly Rgba HipsColour = Rgba.Hex(0xF2C14E);
    public static readonly Rgba LeftFootColour = Rgba.Hex(0x4EA8F2);
    public static readonly Rgba RightFootColour = Rgba.Hex(0xF25F5C);
    public static readonly Rgba LeftHandColour = Rgba.Hex(0x7FD1B9);
    public static readonly Rgba RightHandColour = Rgba.Hex(0xC497F2);
    public static readonly Rgba ErrorColour = Rgba.Hex(0xF25F5C);
    public static readonly Rgba WarningColour = Rgba.Hex(0xF2C14E);

    public static SheetCanvas Compose(ContactSheetLayout layout, ContactSheetContent content, Font labels, Font mono)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(content);
        var canvas = new SheetCanvas(layout.Width, layout.Height, Background);

        // Header.
        var header = layout.Header;
        canvas.Text(labels, content.Title, 22, header.X, header.Y, Text);
        canvas.Text(labels, content.Subtitle, 15, header.X, header.Y + 32, Dim);

        // Column and row labels, cells.
        for (var column = 0; column < (layout.Single ? layout.Views.Count : layout.Frames.Count); column++)
        {
            var cell = layout.Single ? layout.Cell(column, 0) : layout.Cell(0, column);
            var label = layout.Single
                ? $"{ContactSheetLayout.Name(layout.Views[column])} · {layout.Frames[0].Label}"
                : layout.Frames[column].Label;
            canvas.Text(labels, label, 14, cell.X + 2, cell.Y - ContactSheetLayout.ColumnLabelHeight + 2, layout.Frames[layout.Single ? 0 : column].IsKey ? Text : Dim);
        }

        if (!layout.Single)
            for (var view = 0; view < layout.Views.Count; view++)
            {
                var cell = layout.Cell(view, 0);
                canvas.Text(labels, ContactSheetLayout.Name(layout.Views[view]), 15, ContactSheetLayout.Margin, cell.Y + cell.Height / 2 - 10, Text);
            }

        foreach (var (view, frame) in layout.Cells())
        {
            var cell = layout.Cell(view, frame);
            if (content.Cells.TryGetValue((view, frame), out var capture))
            {
                canvas.Blit(capture.Pixels, capture.Width, capture.Height, cell.X, cell.Y, cell.Width, cell.Height);
            }
            else
            {
                canvas.Fill(cell.X, cell.Y, cell.Width, cell.Height, Panel);
                canvas.Line(cell.X, cell.Y, cell.X + cell.Width, cell.Y + cell.Height, ErrorColour);
            }

            canvas.Rectangle(cell.X, cell.Y, cell.Width, cell.Height, Grid);
        }

        DrawStrip(canvas, layout, content, labels);
        DrawReport(canvas, layout, content, mono);
        return canvas;
    }

    private static void DrawStrip(SheetCanvas canvas, ContactSheetLayout layout, ContactSheetContent content, Font labels)
    {
        var strip = layout.Strip;
        canvas.Fill(strip.X, strip.Y, strip.Width, strip.Height, Panel);
        const int bands = 2, bandHeight = 7, axis = 30, top = 32;
        var plot = new SheetRect(strip.X + 44, strip.Y + top, strip.Width - 54, strip.Height - top - axis - bands * (bandHeight + 2));
        canvas.Text(labels, "heights (m) over time · feet contacts below", 13, strip.X + 8, strip.Y + 3, Dim);

        var series = new (string Name, Rgba Colour, Func<StripSample, float> Value)[]
        {
            ("hips", HipsColour, s => s.Hips), ("leftFoot", LeftFootColour, s => s.LeftFoot), ("rightFoot", RightFootColour, s => s.RightFoot),
            ("leftHand", LeftHandColour, s => s.LeftHand), ("rightHand", RightHandColour, s => s.RightHand),
        };
        var legendX = strip.X + strip.Width - 8;
        foreach (var (name, colour, _) in series.Reverse())
        {
            var width = labels.RenderLine(name, 13).Width;
            legendX -= width + 14;
            canvas.Fill(legendX, strip.Y + 8, 10, 3, colour);
            canvas.Text(labels, name, 13, legendX + 13, strip.Y + 3, Dim);
        }

        var samples = content.Strip;
        var max = samples.Count == 0 ? 1f : MathF.Max(0.1f, samples.Max(s => MathF.Max(s.Hips, MathF.Max(s.LeftHand, s.RightHand))));
        max = MathF.Ceiling(max * 4f) / 4f;
        float X(float t) => plot.X + (content.Length <= 0f ? 0f : t / content.Length) * plot.Width;
        float Y(float h) => plot.Y + plot.Height - Math.Clamp(h / max, -0.05f, 1.05f) * plot.Height;

        // Grid: height lines every 0.25 m (or 0.5 m), the frame times, the time axis.
        var step = max > 1.5f ? 0.5f : 0.25f;
        for (var h = 0f; h <= max + 1e-4f; h += step)
        {
            canvas.Line(plot.X, Y(h), plot.X + plot.Width, Y(h), Grid);
            canvas.Text(labels, h.ToString("0.##", CultureInfo.InvariantCulture), 11, strip.X + 6, (int)Y(h) - 8, Dim);
        }

        foreach (var frame in layout.Frames)
            canvas.Line(X(frame.Time), plot.Y, X(frame.Time), plot.Y + plot.Height, Grid);
        for (var tick = 0; tick <= 4; tick++)
        {
            var t = content.Length * tick / 4f;
            canvas.Text(labels, $"{t.ToString("0.##", CultureInfo.InvariantCulture)} s", 11, (int)X(t) - 10,
                plot.Y + plot.Height + bands * (bandHeight + 2) + 6, Dim);
        }

        foreach (var (_, colour, value) in series)
            for (var i = 1; i < samples.Count; i++)
                canvas.Line(X(samples[i - 1].Time), Y(value(samples[i - 1])), X(samples[i].Time), Y(value(samples[i])), colour, 2);

        // Contact bands: left foot, then right foot.
        var bandTop = plot.Y + plot.Height + 4;
        for (var i = 0; i + 1 < samples.Count; i++)
        {
            var x0 = (int)X(samples[i].Time);
            var x1 = (int)MathF.Ceiling(X(samples[i + 1].Time));
            canvas.Fill(x0, bandTop, x1 - x0, bandHeight, samples[i].LeftContact ? LeftFootColour : Grid);
            canvas.Fill(x0, bandTop + bandHeight + 2, x1 - x0, bandHeight, samples[i].RightContact ? RightFootColour : Grid);
        }
    }

    private static void DrawReport(SheetCanvas canvas, ContactSheetLayout layout, ContactSheetContent content, Font mono)
    {
        var report = layout.Report;
        var characters = Math.Max(20, (int)(report.Width / (mono.RenderLine("MMMMMMMMMM", 13).Width / 10f)));
        for (var i = 0; i < content.Report.Count; i++)
        {
            var line = content.Report[i];
            var colour = line.TrimStart().StartsWith("error", StringComparison.Ordinal) ? ErrorColour
                : line.TrimStart().StartsWith("warning", StringComparison.Ordinal) ? WarningColour
                : i == 0 ? Text : Dim;
            if (line.Length > characters)
                line = string.Concat(line.AsSpan(0, characters - 1), "…");
            canvas.Text(mono, line, 13, report.X, report.Y + i * ContactSheetLayout.ReportLineHeight, colour);
        }
    }
}
