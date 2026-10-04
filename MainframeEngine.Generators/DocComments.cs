using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace MainframeEngine.Generators;

/// <summary>
/// Reads a symbol's XML doc <c>&lt;summary&gt;</c> as plain text for the editor (Add Node descriptions, inspector
/// tooltips). Works whether or not the project generates a documentation file: without one the compiler does not
/// parse doc comments (<see cref="ISymbol.GetDocumentationCommentXml"/> is empty), so the <c>///</c> lines are read
/// from the declaration's leading trivia instead.
/// </summary>
internal static class DocComments
{
    /// <summary>Longest description kept (tooltips and the description pane are not documentation pages).</summary>
    public const int MaxLength = 600;

    private static readonly Regex Summary = new("<summary>(.*?)</summary>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex CrefTag = new("<(?:see|seealso)\\s+(?:cref|href)\\s*=\\s*\"([^\"]*)\"\\s*/>", RegexOptions.CultureInvariant);
    private static readonly Regex CrefTagWithText = new("<(?:see|seealso)\\s+[^>]*>(.*?)</(?:see|seealso)>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex NameTag = new("<(?:see\\s+langword|paramref\\s+name|typeparamref\\s+name)\\s*=\\s*\"([^\"]*)\"\\s*/>", RegexOptions.CultureInvariant);
    private static readonly Regex AnyTag = new("<[^>]+>", RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new("[ \\t\\r\\n]+", RegexOptions.CultureInvariant);

    /// <summary>The summary of <paramref name="symbol"/> as one line of plain text, or null when it has none.</summary>
    public static string? SummaryOf(ISymbol symbol, CancellationToken ct)
    {
        var xml = symbol.GetDocumentationCommentXml(cancellationToken: ct);
        if (string.IsNullOrEmpty(xml))
            xml = FromTrivia(symbol, ct);
        return string.IsNullOrEmpty(xml) ? null : ToPlainText(xml!);
    }

    private static string? FromTrivia(ISymbol symbol, CancellationToken ct)
    {
        var builder = new StringBuilder();
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax(ct);
            // A field's declarator sits inside its declaration, which owns the comment.
            var owner = syntax.Parent?.Parent is { } declaration && syntax.RawKind == (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.VariableDeclarator
                ? declaration
                : syntax;
            foreach (var trivia in owner.GetLeadingTrivia())
            {
                var text = trivia.ToFullString();
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.Trim();
                    if (!line.StartsWith("///", System.StringComparison.Ordinal))
                        continue;
                    builder.Append(line.Substring(3)).Append('\n');
                }
            }

            if (builder.Length > 0)
                break;
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>The text of the first <c>&lt;summary&gt;</c> with tags removed (<c>cref</c>s become their last name segment).</summary>
    internal static string? ToPlainText(string xml)
    {
        var match = Summary.Match(xml);
        if (!match.Success)
            return null;
        var text = match.Groups[1].Value;
        text = CrefTagWithText.Replace(text, m => m.Groups[1].Value);
        text = CrefTag.Replace(text, m => ShortName(m.Groups[1].Value));
        text = NameTag.Replace(text, m => m.Groups[1].Value);
        text = AnyTag.Replace(text, "");
        text = text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");
        text = Whitespace.Replace(text, " ").Trim();
        if (text.Length == 0)
            return null;
        if (text.Length > MaxLength)
            text = text.Substring(0, MaxLength - 1).TrimEnd() + "…";
        return text;
    }

    // "T:MainframeEngine.Node3D" → "Node3D", "Node.Name" → "Name", "M:A.B(int)" → "B", "List{T}" → "List".
    internal static string ShortName(string cref)
    {
        var name = cref;
        if (name.Length > 2 && name[1] == ':')
            name = name.Substring(2);
        var cut = name.IndexOfAny(['(', '{', '<']);
        if (cut >= 0)
            name = name.Substring(0, cut);
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name.Substring(dot + 1) : name;
    }
}
