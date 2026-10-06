using System.Text.RegularExpressions;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// How a bound value is used, so a stand-in can have a fitting type. A value used several ways takes the latest member
/// of this order that applies (a style value still reads as text; text would not parse as a style).
/// </summary>
public enum RmlValueUse
{
    /// <summary>Shown as text (<c>{{ x }}</c>, <c>data-attr-*</c>, text fields).</summary>
    Text,

    /// <summary>A number (<c>| format(2)</c>, <c>| round</c>, range and number inputs, <c>data-attr-value</c>).</summary>
    Number,

    /// <summary>A condition (<c>data-if</c>, <c>data-class-*</c>, <c>data-checked</c>, <c>!x</c>).</summary>
    Bool,

    /// <summary>A file reference (<c>data-attr-src</c>, <c>data-attr-href</c>): left empty so nothing tries to load.</summary>
    Path,

    /// <summary>A style property's value (<c>data-style-*</c>); see <see cref="RmlShape.StyleProperty"/>.</summary>
    Style,
}

/// <summary>
/// The shape a document's uses imply for a bound variable: a value, a list (<c>data-for</c>, <c>[i]</c>) or an object
/// (<c>.member</c>). <see cref="Label"/> names it for stand-in text.
/// </summary>
public sealed class RmlShape(string label)
{
    private readonly Dictionary<string, RmlShape> _members = new(StringComparer.Ordinal);

    public string Label { get; } = label;

    /// <summary>Null until a use decides it; an undecided shape is a value.</summary>
    public RmlVariableKind? Kind { get; private set; }

    public RmlValueUse Use { get; private set; } = RmlValueUse.Text;

    /// <summary>The property a <see cref="RmlValueUse.Style"/> value sets (the first one seen).</summary>
    public string? StyleProperty { get; private set; }

    /// <summary>The members of an object.</summary>
    public IReadOnlyDictionary<string, RmlShape> Members => _members;

    /// <summary>The elements' shape of a list.</summary>
    public RmlShape? Element { get; private set; }

    public RmlVariableKind EffectiveKind => Kind ?? RmlVariableKind.Scalar;

    internal RmlShape Member(string name)
    {
        Kind ??= RmlVariableKind.Struct;
        if (!_members.TryGetValue(name, out var member))
            _members[name] = member = new RmlShape(name);
        return member;
    }

    internal RmlShape Item()
    {
        Kind ??= RmlVariableKind.Array;
        return Element ??= new RmlShape(Label);
    }

    // A value used in several ways takes the use whose stand-in fits the most of them (RmlValueUse's order).
    internal void UseAs(RmlValueUse use, string? styleProperty = null)
    {
        if (use < Use)
            return;
        Use = use;
        if (use == RmlValueUse.Style)
            StyleProperty ??= styleProperty;
    }
}

/// <summary>The bindings of one data model (<c>data-model="name"</c>) a document uses.</summary>
public sealed class RmlModelBindings(string name)
{
    public string Name { get; } = name;

    /// <summary>Top-level variables by name.</summary>
    public Dictionary<string, RmlShape> Variables { get; } = new(StringComparer.Ordinal);

    /// <summary>Event callbacks (<c>data-event-click="buy(item)"</c>).</summary>
    public SortedSet<string> Events { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The data bindings an RML document uses, read from its source: per data model, the variables with the shapes their
/// uses imply, and the event callbacks — enough to bind stand-in models (<see cref="UiPreviewData"/>) so a previewed
/// document renders its views without the game code that normally creates the models. Follows RmlUi's syntax: data
/// views and controllers (<c>data-if</c>, <c>data-for="item, i : list"</c>, <c>data-attr-*</c>, <c>data-event-*</c>,
/// …), <c>{{ }}</c> text, <c>it</c>/<c>it_index</c> and <c>ev</c>, transforms after <c>|</c>.
/// </summary>
public sealed partial class RmlBindings
{
    /// <summary>Names RmlUi reserves (never top-level variables).</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "it", "it_index", "ev", "true", "false", "size", "literal" };

    private readonly Dictionary<string, RmlModelBindings> _models = new(StringComparer.Ordinal);

    private RmlBindings()
    {
    }

    /// <summary>The data models the document uses, in order of appearance.</summary>
    public IReadOnlyCollection<RmlModelBindings> Models => _models.Values;

    public static RmlBindings Empty { get; } = new();

    /// <summary>Reads the bindings of the document at <paramref name="path"/>; empty when it cannot be read.</summary>
    public static RmlBindings ParseFile(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    /// <summary>Reads the bindings of RML source text (tolerant: not every document is well-formed XML).</summary>
    public static RmlBindings Parse(string rml)
    {
        ArgumentNullException.ThrowIfNull(rml);
        var bindings = new RmlBindings();
        bindings.Scan(Comment().Replace(rml, ""));
        return bindings;
    }

    // ── Document walk ────────────────────────────────────────────────────────────────────────────────────────────

    // An open element: its model and the aliases (data-for iterators) visible inside it.
    private sealed record Scope(string Tag, RmlModelBindings? Model, Dictionary<string, RmlShape?> Aliases);

    private void Scan(string rml)
    {
        var stack = new List<Scope> { new("", null, new Dictionary<string, RmlShape?>(StringComparer.Ordinal)) };
        var raw = 0; // inside <style>/<script>: text is not RML
        foreach (Match token in Token().Matches(rml))
        {
            if (token.Groups["text"].Success)
            {
                if (raw == 0 && stack[^1].Model is not null)
                    foreach (Match text in Mustache().Matches(token.Value))
                        Expression(text.Groups[1].Value, stack[^1], RmlValueUse.Text, isEvent: false);
                continue;
            }

            var tag = token.Groups["tag"].Value;
            if (token.Groups["close"].Value.Length > 0)
            {
                if (tag.Equals("style", StringComparison.OrdinalIgnoreCase) || tag.Equals("script", StringComparison.OrdinalIgnoreCase))
                    raw = Math.Max(0, raw - 1);
                for (var i = stack.Count - 1; i > 0; i--)
                    if (string.Equals(stack[i].Tag, tag, StringComparison.OrdinalIgnoreCase))
                    {
                        stack.RemoveRange(i, stack.Count - i);
                        break;
                    }

                continue;
            }

            var scope = Open(tag, token.Groups["attrs"].Value, stack[^1]);
            if (token.Groups["self"].Value.Length > 0)
                continue;
            if (tag.Equals("style", StringComparison.OrdinalIgnoreCase) || tag.Equals("script", StringComparison.OrdinalIgnoreCase))
                raw++;
            stack.Add(scope);
        }
    }

    private Scope Open(string tag, string attributeText, Scope parent)
    {
        var attributes = new List<(string Name, string Value)>();
        foreach (Match a in Attribute().Matches(attributeText))
            attributes.Add((a.Groups[1].Value, a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Success ? a.Groups[4].Value : a.Groups[5].Value));

        var model = parent.Model;
        var aliases = parent.Aliases;
        foreach (var (name, value) in attributes)
            if (name == "data-model" && value.Trim() is { Length: > 0 } modelName)
            {
                if (!_models.TryGetValue(modelName, out model))
                    _models[modelName] = model = new RmlModelBindings(modelName);
                aliases = new Dictionary<string, RmlShape?>(StringComparer.Ordinal);
            }

        var scope = new Scope(tag, model, aliases);
        if (model is null)
            return scope;

        // data-for first: its iterator is visible in the element's own attributes and below.
        foreach (var (name, value) in attributes)
            if (name == "data-for")
                scope = For(value, scope);

        var inputType = attributes.Find(a => a.Name == "type").Value;
        foreach (var (name, value) in attributes)
        {
            if (!name.StartsWith("data-", StringComparison.Ordinal) || name is "data-model" or "data-for")
                continue;
            if (name.StartsWith("data-event-", StringComparison.Ordinal))
                Expression(value, scope, RmlValueUse.Text, isEvent: true);
            else if (name.StartsWith("data-style-", StringComparison.Ordinal))
                Expression(value, scope, RmlValueUse.Style, isEvent: false, styleProperty: name["data-style-".Length..]);
            else if (name is "data-attr-src" or "data-attr-href")
                Expression(value, scope, RmlValueUse.Path, isEvent: false);
            else if (name is "data-attr-value" or "data-attr-min" or "data-attr-max" or "data-attr-step")
                Expression(value, scope, RmlValueUse.Number, isEvent: false);
            else if (name is "data-if" or "data-visible" or "data-checked" || name.StartsWith("data-class-", StringComparison.Ordinal))
                Expression(value, scope, RmlValueUse.Bool, isEvent: false);
            else if (name == "data-value")
                Expression(value, scope, inputType is "range" or "number" ? RmlValueUse.Number : inputType is "checkbox" ? RmlValueUse.Bool : RmlValueUse.Text, isEvent: false);
            else if (name is "data-rml" || name.StartsWith("data-attr-", StringComparison.Ordinal))
                Expression(value, scope, RmlValueUse.Text, isEvent: false);
        }

        return scope;
    }

    // data-for="[iterator[, index] :] container": the container is a list; the iterator names its elements.
    private Scope For(string expression, Scope scope)
    {
        var colon = expression.LastIndexOf(':');
        var names = colon >= 0 ? expression[..colon].Split(',', StringSplitOptions.TrimEntries) : [];
        var iterator = names.Length > 0 && names[0].Length > 0 ? names[0] : "it";
        var index = names.Length > 1 && names[1].Length > 0 ? names[1] : "it_index";
        var container = Resolve(expression[(colon + 1)..].Trim(), scope, isEvent: false);
        var aliases = new Dictionary<string, RmlShape?>(scope.Aliases, StringComparer.Ordinal)
        {
            [iterator] = container?.Item(),
            [index] = null, // a number RmlUi provides
        };
        return scope with { Aliases = aliases };
    }

    // ── Expressions ──────────────────────────────────────────────────────────────────────────────────────────────

    private void Expression(string expression, Scope scope, RmlValueUse use, bool isEvent, string? styleProperty = null)
    {
        var tokens = ExpressionToken().Matches(expression);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.Groups["path"].Success)
                continue;
            var path = token.Value;
            var next = i + 1 < tokens.Count ? tokens[i + 1].Value : "";
            var previous = i > 0 ? tokens[i - 1].Value : "";
            if (next == "(")
            {
                // name(...): a transform after '|', else (in data-event) an event callback.
                if (previous != "|" && isEvent && scope.Model is { } model && !path.Contains('.', StringComparison.Ordinal) && !path.Contains('[', StringComparison.Ordinal))
                    model.Events.Add(path);
                continue;
            }

            if (previous == "|")
                continue; // a transform without arguments: x | to_upper
            if (Resolve(path, scope, isEvent) is not { } shape)
                continue;
            var local = previous == "!" || next is "&&" or "||" || previous is "&&" or "||" ? RmlValueUse.Bool
                : next == "|" && i + 2 < tokens.Count && tokens[i + 2].Value is "format" or "round" ? RmlValueUse.Number
                : use;
            if (shape.Kind is null or RmlVariableKind.Scalar)
                shape.UseAs(local, styleProperty);
        }
    }

    // The shape a path (a.b[0].c) addresses, created along the way; null for literals, ev and index aliases.
    private RmlShape? Resolve(string path, Scope scope, bool isEvent)
    {
        if (scope.Model is not { } model)
            return null;
        var segments = PathSegment().Matches(path);
        if (segments.Count == 0)
            return null;
        var root = segments[0].Groups["name"].Value;
        if (root is "true" or "false" || isEvent && root == "ev")
            return null;
        RmlShape? shape;
        if (scope.Aliases.TryGetValue(root, out var alias))
        {
            shape = alias; // null: an index alias (a number)
        }
        else if (Reserved.Contains(root))
        {
            return null;
        }
        else
        {
            if (!model.Variables.TryGetValue(root, out shape))
                model.Variables[root] = shape = new RmlShape(root);
        }

        for (var i = 1; i < segments.Count && shape is not null; i++)
        {
            var segment = segments[i];
            if (segment.Groups["name"].Success)
            {
                shape = shape.Member(segment.Groups["name"].Value);
            }
            else
            {
                Expression(segment.Groups["index"].Value, scope, RmlValueUse.Number, isEvent);
                shape = shape.Item();
            }
        }

        return shape;
    }

    // Tags (attribute values may hold '>'), and the text between them.
    [GeneratedRegex("""<(?<close>/?)(?<tag>[A-Za-z_][\w:.-]*)(?<attrs>(?:[^>"']|"[^"]*"|'[^']*')*?)(?<self>/?)>|(?<text>[^<]+)""")]
    private static partial Regex Token();

    [GeneratedRegex("""([A-Za-z_][\w:.-]*)\s*(=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>]+)))?""")]
    private static partial Regex Attribute();

    [GeneratedRegex(@"\{\{(.*?)\}\}", RegexOptions.Singleline)]
    private static partial Regex Mustache();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    // Strings, numbers, paths (a.b[expr].c — one level of brackets), operators.
    [GeneratedRegex("""'[^']*'|"[^"]*"|\d+(?:\.\d+)?|(?<path>[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*|\[[^\[\]]*\])*)|&&|\|\||[!=<>]=?|[()|,+\-*/?:;]""")]
    private static partial Regex ExpressionToken();

    [GeneratedRegex(@"(?<name>[A-Za-z_]\w*)|\[(?<index>[^\[\]]*)\]")]
    private static partial Regex PathSegment();
}
