using System.Text;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// Stand-in data models for a previewed document. Its models are created by game code, so the preview binds stubs
/// shaped by what the document uses (<see cref="RmlBindings"/>): text shows the variable's name, conditions are true,
/// numbers 0, style values a neutral value their property accepts (<see cref="StyleValue"/>), file references are
/// empty, lists have <see cref="ListSize"/> rows and events do nothing. Values written by inputs and assignments
/// are kept, so the document stays interactive. Bound in the preview layer's context before the document loads and
/// rebuilt when the document changes.
/// </summary>
public sealed class UiPreviewData : IDisposable
{
    /// <summary>Rows of a stand-in list.</summary>
    public const int ListSize = 3;

    private readonly List<RmlDataModel> _models = [];

    /// <summary>What the document binds (the models' names and shapes).</summary>
    public RmlBindings Bindings { get; private set; } = RmlBindings.Empty;

    /// <summary>The stand-in models currently bound.</summary>
    public IReadOnlyList<RmlDataModel> Models => _models;

    /// <summary>Binds stand-ins for <paramref name="bindings"/> in <paramref name="context"/>, replacing the previous ones.</summary>
    public void Bind(RmlContext context, RmlBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(bindings);
        Dispose();
        Bindings = bindings;
        foreach (var use in bindings.Models)
        {
            if (Exists(context, use.Name))
                continue; // a real model of that name (none in the editor today) wins
            RmlDataModel model;
            try
            {
                model = context.CreateDataModel(use.Name);
            }
            catch (RmlException e)
            {
                Log.Warning($"[Editor] UI preview: could not create a stand-in for data model '{use.Name}': {e.Message}");
                continue;
            }

            _models.Add(model);
            foreach (var (name, shape) in use.Variables)
                TryBind(model, name, () => model.BindVariable(name, shape.EffectiveKind, new StubSource(shape, model, name)));
            foreach (var name in use.Events)
                if (!use.Variables.ContainsKey(name))
                    TryBind(model, name, () => model.Event(name, static () => { }));
        }
    }

    private static bool Exists(RmlContext context, string name)
    {
        foreach (var model in context.DataModels)
            if (model.IsValid && model.Name == name)
                return true;
        return false;
    }

    private static void TryBind(RmlDataModel model, string name, Action bind)
    {
        try
        {
            bind();
        }
        catch (RmlException e)
        {
            Log.Warning($"[Editor] UI preview: could not bind a stand-in for '{name}' in data model '{model.Name}': {e.Message}");
        }
    }

    public void Dispose()
    {
        foreach (var model in _models)
            model.Dispose();
        _models.Clear();
    }

    /// <summary>
    /// A stand-in value RCSS accepts for <paramref name="property"/> (RmlUi has no <c>inherit</c>/<c>initial</c>): grey
    /// for colours, <c>auto</c>/<c>none</c>/<c>0px</c> for sizes and offsets, the neutral value for the rest.
    /// </summary>
    public static string StyleValue(string property) => property switch
    {
        _ when property.Contains("color", StringComparison.Ordinal) => "#7f8796",
        "opacity" or "flex-grow" or "flex-shrink" => "1",
        "visibility" => "visible",
        "display" => "block",
        "font-size" => "1em",
        "line-height" => "1.2",
        "font-weight" => "normal",
        "z-index" => "auto",
        "max-width" or "max-height" or "transform" or "decorator" or "filter" or "backdrop-filter" or "box-shadow" or "mask-image" => "none",
        "width" or "height" or "left" or "right" or "top" or "bottom" or "flex-basis" => "auto",
        _ when property.StartsWith("margin", StringComparison.Ordinal) => "auto",
        _ when property.StartsWith("padding", StringComparison.Ordinal) || property.StartsWith("border", StringComparison.Ordinal) ||
               property.StartsWith("min-", StringComparison.Ordinal) || property.EndsWith("gap", StringComparison.Ordinal) => "0px",
        _ => "auto",
    };

    /// <summary>A variable walked through its shape: tokens name nodes on demand; written values are kept per node.</summary>
    private sealed class StubSource(RmlShape root, RmlDataModel model, string variable) : RmlVariableSource
    {
        private readonly List<(RmlShape Shape, int Index)> _nodes = [(root, -1)]; // token = list index
        private readonly Dictionary<(ulong Parent, int Index, string? Member), ulong> _children = [];
        private readonly Dictionary<ulong, object> _values = [];

        public override int Size(ulong node) => Shape(node)?.EffectiveKind == RmlVariableKind.Array ? ListSize : 0;

        public override RmlVariableKind? Child(ulong node, int index, ReadOnlySpan<byte> name, out ulong child)
        {
            child = 0;
            if (Shape(node) is not { } shape)
                return null;
            RmlShape next;
            string? member = null;
            if (shape.EffectiveKind == RmlVariableKind.Array && index >= 0)
            {
                next = shape.Item();
            }
            else if (shape.EffectiveKind == RmlVariableKind.Struct && index < 0)
            {
                member = Encoding.UTF8.GetString(name);
                next = shape.Member(member); // a member the scan missed reads as text
            }
            else
            {
                return null;
            }

            var key = (node, index, member);
            if (!_children.TryGetValue(key, out child))
            {
                child = (ulong)_nodes.Count;
                _nodes.Add((next, index));
                _children[key] = child;
            }

            return next.EffectiveKind;
        }

        public override bool Read(ulong node, RmlVariant value)
        {
            if (Shape(node) is not { } shape)
                return false;
            if (_values.TryGetValue(node, out var stored))
            {
                switch (stored)
                {
                    case bool b: value.Set(b); break;
                    case double d: value.Set(d); break;
                    default: value.Set((string)stored); break;
                }

                return true;
            }

            switch (shape.Use)
            {
                case RmlValueUse.Bool: value.Set(true); break;
                case RmlValueUse.Number: value.Set(0); break;
                case RmlValueUse.Path: value.Set(""); break;
                case RmlValueUse.Style: value.Set(StyleValue(shape.StyleProperty ?? "")); break;
                default: value.Set(_nodes[(int)node].Index >= 0 ? $"{shape.Label} {_nodes[(int)node].Index + 1}" : shape.Label); break;
            }

            return true;
        }

        public override bool Write(ulong node, RmlVariant value)
        {
            if (Shape(node) is null)
                return false;
            _values[node] = value.Type switch
            {
                RmlVariantType.Bool => value.GetBool(),
                RmlVariantType.Int or RmlVariantType.Float => value.GetDouble(),
                _ => value.GetString(),
            };
            model.Dirty(variable);
            return true;
        }

        private RmlShape? Shape(ulong node) => node < (ulong)_nodes.Count ? _nodes[(int)node].Shape : null;
    }
}
