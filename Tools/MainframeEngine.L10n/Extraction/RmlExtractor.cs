using System.Text;
using MainframeEngine.Localization;

namespace MainframeEngine.L10n.Extraction;

/// <summary>
/// Extracts translatable text from RmlUi documents with the runtime's own parser (<see cref="RmlLocalization.Scan"/>),
/// so every msgid is exactly what <see cref="Tr.TranslateMarkup(string, out string)"/> will look up. Elements with
/// <c>class="no-tr"</c> are skipped.
/// </summary>
internal static class RmlExtractor
{
    /// <summary>File extensions scanned in directories (RML documents and templates).</summary>
    public static readonly string[] Extensions = [".rml"];

    /// <summary>Adds the runs of one document; returns how many were added.</summary>
    public static int Extract(string file, string reference, TemplateBuilder builder)
    {
        var source = File.ReadAllText(file, Encoding.UTF8);
        var added = 0;
        foreach (var run in RmlLocalization.Scan(source))
        {
            if (run.OptedOut)
                continue;
            var comment = run.Kind == RmlTextKind.Attribute ? $"RML <{run.Element} {run.Attribute}=\"…\">" : $"RML <{run.Element}>";
            builder.Add(null, run.Text, null, $"{reference}:{run.Line}", comment);
            added++;
        }

        return added;
    }
}
