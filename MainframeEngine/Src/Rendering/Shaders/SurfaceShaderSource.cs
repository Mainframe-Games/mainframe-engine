using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MainframeEngine;

/// <summary>Which engine pipeline a surface shader draws in: its first statement, <c>import mainframe.spatial;</c> or <c>…canvas;</c>.</summary>
public enum SurfaceShaderKind : byte
{
    Spatial,
    Canvas,
}

/// <summary>
/// What mf-shaders reads from a surface shader's text (ADR 0237); slangc and its reflection give the rest. This is the
/// first statement (the kind), which optional parameters <c>vertex()</c> and <c>fragment()</c> take, the literal
/// initializers of <c>struct Material</c> (its parameters' defaults), and whether the code names <c>normalMap</c> and
/// <c>lightVertex</c> (the colour pass applies them only then).
/// </summary>
public sealed partial class SurfaceShaderSource
{
    private SurfaceShaderSource(string name, SurfaceShaderKind kind)
    {
        Name = name;
        Kind = kind;
    }

    /// <summary>The file name errors name.</summary>
    public string Name { get; }

    public SurfaceShaderKind Kind { get; }

    /// <summary><c>vertex()</c>'s optional parameters, in order, or null without <c>vertex()</c>.</summary>
    public EntryParameters? Vertex { get; private init; }

    /// <summary><c>fragment()</c>'s optional parameters, in order, or null without <c>fragment()</c>.</summary>
    public EntryParameters? Fragment { get; private init; }

    /// <summary>True when the shader declares <c>struct Material</c> (its parameters and render modes).</summary>
    public bool HasMaterial { get; private init; }

    /// <summary>True when the shader declares <c>struct Varyings</c> (what <c>vertex()</c> passes to <c>fragment()</c>).</summary>
    public bool HasVaryings { get; private init; }

    /// <summary>The literal defaults of <c>struct Material</c>'s fields, flattened (true = 1); fields without one are absent.</summary>
    public IReadOnlyDictionary<string, float[]> Defaults { get; private init; } = new Dictionary<string, float[]>();

    /// <summary>True when the code names <c>normalMap</c> (the colour pass then bends the normal by it).</summary>
    public bool UsesNormalMap { get; private init; }

    /// <summary>True when the code names <c>lightVertex</c> (the colour pass then lights that position).</summary>
    public bool UsesLightVertex { get; private init; }

    /// <summary>True when the code assigns <c>alpha</c> (a warning when the material is opaque).</summary>
    public bool WritesAlpha { get; private init; }

    /// <summary>True when the code names <c>alphaScissorThreshold</c> (a warning unless the material is a cutout).</summary>
    public bool UsesAlphaScissor { get; private init; }

    /// <summary>The optional parameters of an entry function: the material and the varyings, and which comes first.</summary>
    public sealed record EntryParameters(bool Material, bool Varyings, bool VaryingsFirst);

    /// <summary>The kind of surface shader <paramref name="source"/> is, or null when its first statement is neither import.</summary>
    public static SurfaceShaderKind? KindOf(string source)
    {
        var m = FirstImportRegex().Match(StripComments(source ?? ""));
        if (!m.Success)
            return null;
        return m.Groups["kind"].Value == "spatial" ? SurfaceShaderKind.Spatial : SurfaceShaderKind.Canvas;
    }

    /// <summary>True when <paramref name="source"/> is a surface shader (not a helper module other shaders import).</summary>
    public static bool IsSurfaceShader(string source) => KindOf(source) is not null;

    /// <summary>Reads <paramref name="source"/>, named <paramref name="name"/> in errors.</summary>
    /// <exception cref="FormatException">Not a surface shader, or a default that is not literal (with the line).</exception>
    public static SurfaceShaderSource Parse(string source, string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        var code = StripComments(source);
        var kind = KindOf(source) ?? throw new FormatException(
            $"{name}(1): a surface shader starts with `import mainframe.spatial;` or `import mainframe.canvas;` (ADR 0237).");
        return new SurfaceShaderSource(name, kind)
        {
            Vertex = Entry(code, "vertex", name),
            Fragment = Entry(code, "fragment", name),
            HasMaterial = MaterialStructRegex().IsMatch(code),
            HasVaryings = VaryingsStructRegex().IsMatch(code),
            Defaults = ReadDefaults(code, name),
            UsesNormalMap = NormalMapRegex().IsMatch(code),
            UsesLightVertex = LightVertexRegex().IsMatch(code),
            WritesAlpha = AlphaWriteRegex().IsMatch(code),
            UsesAlphaScissor = AlphaScissorRegex().IsMatch(code),
        };
    }

    // The parameters after the first (inout SurfaceVertex v / SurfaceFragment s): any of Material, Varyings, in either order.
    private static EntryParameters? Entry(string code, string function, string name)
    {
        var matches = EntryRegex().Matches(code).Where(m => m.Groups["name"].Value == function).ToList();
        if (matches.Count == 0)
            return null;
        if (matches.Count > 1)
            throw new FormatException($"{name}({LineOf(code, matches[1].Index)}): {function}() is declared more than once.");
        var parameters = matches[0].Groups["params"].Value;
        var material = MaterialParamRegex().Match(parameters);
        var varyings = VaryingsParamRegex().Match(parameters);
        return new EntryParameters(material.Success, varyings.Success, varyings.Success && (!material.Success || varyings.Index < material.Index));
    }

    private static Dictionary<string, float[]> ReadDefaults(string code, string name)
    {
        var defaults = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var m = MaterialStructRegex().Match(code);
        if (!m.Success)
            return defaults;
        var open = m.Index + m.Length - 1;
        var close = MatchingBrace(code, open);
        if (close < 0)
            throw new FormatException($"{name}({LineOf(code, open)}): struct Material is not closed.");
        var start = open + 1;
        var depth = 0;
        for (var i = start; i < close; i++)
        {
            var c = code[i];
            if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}')
                depth--;
            else if (c == ';' && depth == 0)
            {
                Field(code[start..i], start);
                start = i + 1;
            }
        }

        if (code[start..close].Trim().Length > 0)
            Field(code[start..close], start);
        return defaults;

        void Field(string text, int offset)
        {
            var field = FieldRegex().Match(AttributesRegex().Replace(text, " "));
            if (!field.Success || !field.Groups["init"].Success)
                return;
            var fieldName = field.Groups["name"].Value;
            var at = $"{name}({LineOf(code, offset + text.Length - text.TrimStart().Length)})";
            if (field.Groups["array"].Success)
                throw new FormatException($"{at}: Material.{fieldName}: array parameters can't have defaults yet.");
            defaults[fieldName] = ParseLiteral(field.Groups["init"].Value.Trim(), $"{at}: Material.{fieldName}");
        }
    }

    // A literal default: numbers, true/false and constructors of the scalar/vector/matrix types, nothing computed.
    internal static float[] ParseLiteral(string text, string at)
    {
        if (LiteralShapeRegex().Replace(text, "").Length > 0)
            throw new FormatException(
                $"{at}: a default must be a literal, such as 0.5, true or float4(0.4, 0.8, 0.3, 1.0) (found '{text}'); compute it in the shader instead.");
        var values = new List<float>();
        foreach (Match token in LiteralTokenRegex().Matches(text))
        {
            var t = token.Value;
            values.Add(t switch
            {
                "true" => 1f,
                "false" => 0f,
                _ => float.Parse(t.TrimEnd('f', 'F', 'h', 'H', 'u', 'U', 'l', 'L'), NumberStyles.Float, CultureInfo.InvariantCulture),
            });
        }

        if (values.Count == 0)
            throw new FormatException($"{at}: the default '{text}' has no value.");
        return [.. values];
    }

    // Comments become spaces/newlines so line numbers in errors stay meaningful.
    internal static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                    i++;
                if (i < source.Length)
                    sb.Append('\n');
            }
            else if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    if (source[i] == '\n')
                        sb.Append('\n');
                    i++;
                }

                i++;
            }
            else
            {
                sb.Append(source[i]);
            }
        }

        return sb.ToString();
    }

    private static int MatchingBrace(string code, int open)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{')
                depth++;
            else if (code[i] == '}' && --depth == 0)
                return i;
        }

        return -1;
    }

    private static string LineOf(string code, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < code.Length; i++)
            if (code[i] == '\n')
                line++;
        return line.ToString(CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"\A\s*import\s+mainframe\s*\.\s*(?<kind>spatial|canvas)\s*;")]
    private static partial Regex FirstImportRegex();

    [GeneratedRegex(@"\bvoid\s+(?<name>vertex|fragment)\s*\((?<params>[^)]*)\)\s*\{")]
    private static partial Regex EntryRegex();

    [GeneratedRegex(@"\bMaterial\b")]
    private static partial Regex MaterialParamRegex();

    [GeneratedRegex(@"\bVaryings\b")]
    private static partial Regex VaryingsParamRegex();

    [GeneratedRegex(@"\bstruct\s+Material\b[^{;]*\{")]
    private static partial Regex MaterialStructRegex();

    [GeneratedRegex(@"\bstruct\s+Varyings\b[^{;]*\{")]
    private static partial Regex VaryingsStructRegex();

    // The [attr, attr(…)] groups before a field (attribute arguments may hold strings and parentheses, never ']').
    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)+")]
    private static partial Regex AttributesRegex();

    [GeneratedRegex(@"^\s*(?:(?:public|internal|private|static|const|uniform|nointerpolation)\s+)*(?<type>[A-Za-z_]\w*(?:\s*<[^>]*>)?)\s+(?<name>[A-Za-z_]\w*)\s*(?<array>\[\s*\w*\s*\])?\s*(?:=\s*(?<init>.+?))?\s*$", RegexOptions.Singleline)]
    private static partial Regex FieldRegex();

    // Everything a literal default may contain; whatever this leaves is not a literal.
    [GeneratedRegex(@"\b(?:(?:float|half|double|int|uint|bool)(?:[1-4](?:x[1-4])?)?|true|false)\b|[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?[fFhHuUlL]*|[\s(),{}]")]
    private static partial Regex LiteralShapeRegex();

    [GeneratedRegex(@"\btrue\b|\bfalse\b|(?<![\w.])[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?[fFhHuUlL]*")]
    private static partial Regex LiteralTokenRegex();

    [GeneratedRegex(@"\bnormalMap\b")]
    private static partial Regex NormalMapRegex();

    [GeneratedRegex(@"\blightVertex\b")]
    private static partial Regex LightVertexRegex();

    // alpha assigned (=, +=, …; not ==).
    [GeneratedRegex(@"\.alpha\s*[-+*/]?=(?!=)")]
    private static partial Regex AlphaWriteRegex();

    [GeneratedRegex(@"\balphaScissorThreshold\b")]
    private static partial Regex AlphaScissorRegex();
}
