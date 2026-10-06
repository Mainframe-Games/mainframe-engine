using System.Text.RegularExpressions;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>What a <c>&lt;link&gt;</c> of an RML document pulls in.</summary>
public enum RmlLinkKind
{
    /// <summary><c>type="text/rcss"</c>: a style sheet.</summary>
    StyleSheet,

    /// <summary><c>type="text/template"</c>: a template document (its own style sheets apply too).</summary>
    Template,
}

/// <summary>
/// One linked file of an RML document: the <c>href</c> as written, the file it resolves to (null when missing) and,
/// for a style sheet linked by a template, the template's <c>href</c>.
/// </summary>
public sealed record RmlLink(string Href, string? FullPath, RmlLinkKind Kind, string? Via = null)
{
    public bool Exists => FullPath is not null;
}

/// <summary>
/// The style sheets and templates an <c>.rml</c> document links (the UI preview lists them): <c>&lt;link&gt;</c> tags
/// resolved like the UI server does — <see cref="RmlPaths.Join"/> against the document, then
/// <see cref="ContentPaths.Resolve(string)"/> (the project's <c>Content/</c>, then the application's) — plus the style
/// sheets of linked templates, one level deep.
/// </summary>
public sealed partial class RmlLinks
{
    private RmlLinks(IReadOnlyList<RmlLink> links, bool inlineStyle)
    {
        Links = links;
        HasInlineStyle = inlineStyle;
    }

    /// <summary>Linked files in document order (a template's style sheets follow the template).</summary>
    public IReadOnlyList<RmlLink> Links { get; }

    /// <summary>The document has <c>&lt;style&gt;</c> blocks of its own.</summary>
    public bool HasInlineStyle { get; }

    public static RmlLinks Empty { get; } = new([], false);

    /// <summary>Reads the links of the document at <paramref name="rmlPath"/> (an absolute path); empty when it cannot be read.</summary>
    public static RmlLinks Parse(string rmlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rmlPath);
        var path = Path.GetFullPath(rmlPath);
        if (!TryRead(path, out var text))
            return Empty;
        var links = new List<RmlLink>();
        foreach (var (href, kind) in ReadLinks(text))
        {
            var full = Resolve(path, href);
            links.Add(new RmlLink(href, full, kind));
            if (kind != RmlLinkKind.Template || full is null || !TryRead(full, out var template))
                continue;
            foreach (var (templateHref, templateKind) in ReadLinks(template))
                if (templateKind == RmlLinkKind.StyleSheet)
                    links.Add(new RmlLink(templateHref, Resolve(full, templateHref), RmlLinkKind.StyleSheet, href));
        }

        return new RmlLinks(links, StyleTag().IsMatch(StripComments(text)));
    }

    /// <summary>The file <paramref name="href"/> in the document at <paramref name="documentPath"/> loads, or null.</summary>
    public static string? Resolve(string documentPath, string href)
    {
        ArgumentNullException.ThrowIfNull(documentPath);
        ArgumentNullException.ThrowIfNull(href);
        if (href.Length == 0)
            return null;
        var joined = RmlPaths.Join(documentPath.Replace('\\', '/'), href);
        try
        {
            var full = ContentPaths.Resolve(joined);
            return File.Exists(full) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IEnumerable<(string Href, RmlLinkKind Kind)> ReadLinks(string text)
    {
        foreach (Match tag in LinkTag().Matches(StripComments(text)))
        {
            string? type = null, href = null;
            foreach (Match attribute in Attribute().Matches(tag.Value))
            {
                var value = attribute.Groups[3].Success ? attribute.Groups[3].Value : attribute.Groups[4].Value;
                if (attribute.Groups[1].Value.Equals("type", StringComparison.OrdinalIgnoreCase))
                    type = value.Trim();
                else if (attribute.Groups[1].Value.Equals("href", StringComparison.OrdinalIgnoreCase))
                    href = value.Trim();
            }

            if (string.IsNullOrEmpty(href))
                continue;
            if (string.Equals(type, "text/rcss", StringComparison.OrdinalIgnoreCase))
                yield return (href, RmlLinkKind.StyleSheet);
            else if (string.Equals(type, "text/template", StringComparison.OrdinalIgnoreCase))
                yield return (href, RmlLinkKind.Template);
        }
    }

    private static string StripComments(string text) => Comment().Replace(text, "");

    private static bool TryRead(string path, out string text)
    {
        try
        {
            text = File.ReadAllText(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            text = "";
            return false;
        }
    }

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTag();

    [GeneratedRegex(@"([A-Za-z_][\w:-]*)\s*=\s*(""([^""]*)""|'([^']*)')")]
    private static partial Regex Attribute();

    [GeneratedRegex(@"<style\b", RegexOptions.IgnoreCase)]
    private static partial Regex StyleTag();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();
}
