using System.Globalization;
using System.Numerics;
using System.Text;

namespace MainframeEngine.Editor;

/// <summary>
/// The theme accent (Editor Settings): the editor's style sheets use a blue accent (<c>#3b82f6</c>, selection
/// <c>#1d4ed8</c>, primary <c>#2563eb</c>, light <c>#93c5fd</c>/<c>#bfdbfe</c>). A non-default accent writes recoloured
/// copies of <c>theme.rcss</c> and <c>dialogs.rcss</c> into an overlay content folder the UI server checks before the
/// editor's own (<see cref="UiServerOptions.SourceContentDirectories"/>), then reloads the style sheets — no restart.
/// </summary>
public static class EditorTheme
{
    private static readonly string[] Sheets = ["theme.rcss", "dialogs.rcss"];

    /// <summary><c>~/.mainframe/editor-theme</c>.</summary>
    public static string DefaultOverlayDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mainframe", "editor-theme");

    /// <summary>
    /// Writes (or, for the default accent, removes) the recoloured sheets under <paramref name="overlayDirectory"/>.
    /// <paramref name="editorContent"/> is the editor's own <c>Content</c> folder (the sheets are read from there).
    /// Returns the number of sheets written.
    /// </summary>
    public static int Apply(string overlayDirectory, string accent, string editorContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(overlayDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(editorContent);
        var target = Path.Combine(overlayDirectory, "Editor");
        var written = 0;
        foreach (var sheet in Sheets)
        {
            var output = Path.Combine(target, sheet);
            if (string.Equals(accent, EditorSettings.DefaultAccent, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(output))
                    File.Delete(output);
                continue;
            }

            var source = Path.Combine(editorContent, "Editor", sheet);
            if (!File.Exists(source))
                continue;
            AtomicFile.WriteAllBytes(output, Encoding.UTF8.GetBytes(Recolor(File.ReadAllText(source, Encoding.UTF8), accent)));
            written++;
        }

        return written;
    }

    /// <summary>The editor's <c>Content</c> folder: the project sources in Debug builds, else next to the executable.</summary>
    public static string EditorContentDirectory()
    {
        foreach (var source in UiServerOptions.SourceDirectoriesOf(typeof(EditorTheme).Assembly))
            if (File.Exists(Path.Combine(source, "Editor", "theme.rcss")))
                return source;
        return Path.Combine(AppContext.BaseDirectory, ContentPaths.FolderName);
    }

    /// <summary>Replaces the built-in blue accent family in <paramref name="rcss"/> with shades of <paramref name="accent"/>.</summary>
    public static string Recolor(string rcss, string accent)
    {
        ArgumentNullException.ThrowIfNull(rcss);
        if (!ValueText.TryParseColorHex(accent, out var rgba))
            return rcss;
        var color = new Vector3(rgba.X, rgba.Y, rgba.Z);
        return rcss
            .Replace("#3b82f6", Hex(color), StringComparison.OrdinalIgnoreCase)
            .Replace("#2563eb", Hex(color * 0.85f), StringComparison.OrdinalIgnoreCase)
            .Replace("#1d4ed8", Hex(color * 0.7f), StringComparison.OrdinalIgnoreCase)
            .Replace("#1e3a8a", Hex(color * 0.45f), StringComparison.OrdinalIgnoreCase)
            .Replace("#93c5fd", Hex(Vector3.Lerp(color, Vector3.One, 0.45f)), StringComparison.OrdinalIgnoreCase)
            .Replace("#bfdbfe", Hex(Vector3.Lerp(color, Vector3.One, 0.7f)), StringComparison.OrdinalIgnoreCase);
    }

    private static string Hex(Vector3 c) => string.Create(CultureInfo.InvariantCulture,
        $"#{Byte(c.X):x2}{Byte(c.Y):x2}{Byte(c.Z):x2}");

    private static byte Byte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0, 255);
}
