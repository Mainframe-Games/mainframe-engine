using System.Text.RegularExpressions;

namespace MainframeEngine;

/// <summary>One row of the table in <c>THIRD_PARTY_NOTICES.md</c>, as plain text.</summary>
/// <param name="Name">The component, without its parenthetical detail ("FreeType", "Jolt Physics").</param>
/// <param name="Version">The version column; null for "—".</param>
/// <param name="Licence">The licence column.</param>
/// <param name="ShippedIn">The "shipped in" column.</param>
/// <param name="Credit">A credit the notice requires verbatim (a <c>&gt; …</c> quote under the component's heading), or null.</param>
internal readonly record struct ThirdPartyNotice(string Name, string? Version, string Licence, string ShippedIn, string? Credit)
{
    /// <summary>
    /// The component reaches players' machines in a game: not editor-only ("editor only: …", "editor <c>Content/icons</c>"),
    /// not a development tool or a reference ("nothing …"), not the Demo's, the tests' or the Forest's own content.
    /// </summary>
    public bool ShipsInGames =>
        !(ShippedIn.StartsWith("editor", StringComparison.OrdinalIgnoreCase) || ShippedIn.StartsWith("nothing", StringComparison.OrdinalIgnoreCase)
          || ShippedIn.StartsWith("Demo", StringComparison.OrdinalIgnoreCase) || ShippedIn.StartsWith("the Forest", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads <c>THIRD_PARTY_NOTICES.md</c>, embedded in the engine assembly at build (<see cref="ResourceName"/>), into
/// <see cref="ThirdPartyNotice"/> rows for <see cref="Credits"/>: the table's component, version, licence and "shipped in"
/// columns, plus the credit lines a notice requires verbatim (FreeType's).
/// </summary>
internal static partial class ThirdPartyNotices
{
    public const string ResourceName = "MainframeEngine.THIRD_PARTY_NOTICES.md";

    /// <summary>The embedded file's text.</summary>
    public static string ReadEmbedded()
    {
        using var stream = typeof(ThirdPartyNotices).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"The engine assembly has no {ResourceName} resource.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The rows of the first table whose header starts with "| Component |", in order.</summary>
    public static IReadOnlyList<ThirdPartyNotice> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var credits = new Dictionary<string, string>(StringComparer.Ordinal);
        string? heading = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
                heading = PlainText(line[3..]);
            else if (heading is not null && line.StartsWith("> ", StringComparison.Ordinal))
                credits.TryAdd(heading, PlainText(line[2..]));
        }

        var rows = new List<ThirdPartyNotice>();
        var inTable = false;
        foreach (var line in lines)
        {
            if (!inTable)
            {
                inTable = line.StartsWith("| Component |", StringComparison.Ordinal);
                continue;
            }

            if (!line.StartsWith('|'))
                break; // the table ends at its first line that is not a row
            if (line.StartsWith("|---", StringComparison.Ordinal))
                continue;
            var cells = line.Trim().Trim('|').Split('|');
            if (cells.Length < 5)
                continue;
            var component = PlainText(cells[0]);
            var cut = component.IndexOf(" (", StringComparison.Ordinal);
            var name = cut > 0 ? component[..cut] : component;
            var version = PlainText(cells[1]);
            rows.Add(new ThirdPartyNotice(name, version is "" or "—" ? null : version, PlainText(cells[2]), PlainText(cells[4]),
                credits.GetValueOrDefault(name)));
        }

        return rows;
    }

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    /// <summary>Markdown inline text as plain text: links become their text, code and bold marks go.</summary>
    internal static string PlainText(string markdown) =>
        Link().Replace(markdown, "$1").Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal).Trim();
}
