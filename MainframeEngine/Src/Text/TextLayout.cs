namespace MainframeEngine;

/// <summary>
/// Line breaking shared by the text nodes (G11.2: <see cref="Label2D"/>, <see cref="Label3D"/>): paragraphs at
/// <c>\n</c> (and <c>\r\n</c>), then Godot's greedy autowrap inside a width. Widths are measured with the font's
/// fallbacks, so wrapped CJK text breaks where it is drawn.
/// </summary>
internal static class TextLayout
{
    /// <summary>
    /// Appends the lines of <paramref name="text"/> to <paramref name="lines"/>: one per paragraph, each wrapped to
    /// <paramref name="width"/> pixels by <paramref name="mode"/> (no wrapping when it is <see cref="AutowrapMode.Off"/>
    /// or the width is 0).
    /// </summary>
    public static void BreakLines(Font font, string text, int fontSize, float width, AutowrapMode mode, List<string> lines, IReadOnlyList<Font>? extraFallbacks = null)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(lines);
        if (string.IsNullOrEmpty(text))
            return;
        var wrap = mode != AutowrapMode.Off && width > 0f;
        var breaker = new Breaker(font, fontSize, width, mode, lines, extraFallbacks);
        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            if (!wrap)
                lines.Add(paragraph);
            else
                breaker.Wrap(paragraph);
    }

    private readonly struct Breaker(Font font, int fontSize, float width, AutowrapMode mode, List<string> lines, IReadOnlyList<Font>? extra)
    {
        private float Measure(string text) => font.MeasureLine(text, fontSize, extra);

        // Greedy wrapping, as Godot's: words go on the line while it fits; the spaces at a break are dropped.
        public void Wrap(string paragraph)
        {
            if (mode == AutowrapMode.Arbitrary)
            {
                BreakArbitrary(paragraph);
                return;
            }

            var line = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (Measure(candidate) <= width)
                {
                    line = candidate;
                    continue;
                }

                if (line.Length > 0)
                    lines.Add(line);
                if (mode == AutowrapMode.WordSmart && Measure(word) > width)
                    line = BreakArbitrary(word, keepLast: true);
                else
                    line = word; // Word: a word wider than the line overflows it
            }

            lines.Add(line);
        }

        // Breaks between characters; with keepLast the unfinished last piece is returned instead of added.
        private string BreakArbitrary(string text, bool keepLast = false)
        {
            var start = 0;
            for (var end = 1; end <= text.Length; end++)
            {
                if (end - start > 1 && Measure(text[start..end]) > width)
                {
                    lines.Add(text[start..(end - 1)].TrimEnd(' '));
                    start = end - 1;
                    while (start < text.Length - 1 && text[start] == ' ')
                        start++;
                }
            }

            var last = text[start..];
            if (keepLast)
                return last;
            lines.Add(last);
            return "";
        }
    }
}
