using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using MainframeEngine.Serialization;
using DrawingColor = System.Drawing.Color;

namespace MainframeEngine.Editor;

/// <summary>The widget the inspector uses for a property, chosen from its value type and <see cref="ExportHints"/>.</summary>
public enum PropertyEditorKind
{
    Bool,
    IntegerNumber,
    FloatNumber,

    /// <summary>A number with a <c>Range</c> hint: slider plus field.</summary>
    Range,

    Text,

    /// <summary><c>Multiline</c> hint: a text area.</summary>
    MultilineText,

    /// <summary><c>File</c> hint: a path field with a file picker.</summary>
    FilePath,

    /// <summary><c>Directory</c> hint: a path field with a folder picker.</summary>
    DirectoryPath,

    Enum,

    /// <summary>A <c>[Flags]</c> enum or the <c>Flags</c> hint: one checkbox per flag.</summary>
    Flags,

    Vector2,
    Vector3,
    Vector4,

    /// <summary>A quaternion edited as Euler angles in degrees (X, then Y, then Z, like <see cref="Node3D.RotationDegrees"/>).</summary>
    Quaternion,

    /// <summary><see cref="System.Drawing.Color"/>, or a <c>Vector3</c>/<c>Vector4</c> named <c>…Color</c> (0..1 components).</summary>
    Color,

    NodePath,
    Resource,

    /// <summary>Arrays and <c>List&lt;T&gt;</c>: element editors with add/remove.</summary>
    Array,

    /// <summary><see cref="Transform3D"/>/<see cref="Transform2D"/>: shown read-only.</summary>
    Transform,

    /// <summary>A type the inspector has no editor for (shown as text, read-only).</summary>
    Unsupported,
}

/// <summary>
/// One row of the inspector: an <see cref="ExportPropertyInfo"/> of a target object (node or resource) with its editor
/// kind, hint data (range, enum values, file filter, node type) and value formatting/parsing. Built by
/// <see cref="InspectorModel"/> from the generated <see cref="NodeTypeInfo"/>; no reflection on the target.
/// </summary>
public sealed class InspectorProperty
{
    internal InspectorProperty(object target, ExportPropertyInfo info)
        : this([target], info)
    {
    }

    /// <summary>A row editing <paramref name="info"/> on several objects at once (multi-selection); the last is the primary.</summary>
    internal InspectorProperty(IReadOnlyList<object> targets, ExportPropertyInfo info)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
            throw new ArgumentException("An inspector row needs at least one target.", nameof(targets));
        Targets = targets;
        Target = targets[^1];
        Info = info;
        Label = Humanize(info.Name);
        var type = info.ValueType;
        var nullable = Nullable.GetUnderlyingType(type);
        ValueType = nullable ?? type;
        Kind = Classify(info, ValueType);

        if (info.Hints.TryGetRange(out var min, out var max, out var step))
        {
            HasRange = true;
            Min = min;
            Max = max;
            Step = step > 0 ? step : IsIntegerType(ValueType) ? 1 : (max - min) / 100.0;
        }

        if (ValueType.IsEnum)
        {
            EnumNames = Enum.GetNames(ValueType);
            var values = Enum.GetValuesAsUnderlyingType(ValueType);
            EnumValues = new long[values.Length];
            for (var i = 0; i < values.Length; i++)
                EnumValues[i] = Convert.ToInt64(values.GetValue(i), CultureInfo.InvariantCulture);
        }

        if (Kind == PropertyEditorKind.Array)
            ElementType = ValueType.IsArray ? ValueType.GetElementType() : ValueType.GetGenericArguments()[0];
        if (Kind == PropertyEditorKind.Resource)
            ResourceType = ValueType;
    }

    /// <summary>The node or resource the property belongs to (the primary one when several are edited).</summary>
    public object Target { get; }

    /// <summary>Every object the row edits (one, or the multi-selection with <see cref="Target"/> last).</summary>
    public IReadOnlyList<object> Targets { get; }

    /// <summary>True when the row edits several objects.</summary>
    public bool IsMulti => Targets.Count > 1;

    /// <summary>True when the edited objects do not all have the same value (shown as "—").</summary>
    public bool IsMixed
    {
        get
        {
            if (Targets.Count < 2)
                return false;
            foreach (var target in Targets)
                if (!ReferenceEquals(target, Target) && !Info.ValueEquals(Target, target))
                    return true;
            return false;
        }
    }

    /// <summary>True when field <paramref name="component"/> (a vector axis, a colour channel) differs between the edited objects.</summary>
    public bool IsComponentMixed(int component)
    {
        if (Targets.Count < 2)
            return false;
        var text = FormatComponent(Info.GetValue(Target), component);
        foreach (var target in Targets)
            if (!ReferenceEquals(target, Target) && !string.Equals(FormatComponent(Info.GetValue(target), component), text, StringComparison.Ordinal))
                return true;
        return false;
    }

    public ExportPropertyInfo Info { get; }

    public string Name => Info.Name;

    /// <summary>"CastShadows" → "Cast Shadows".</summary>
    public string Label { get; }

    public string? Group => Info.Group;

    public PropertyEditorKind Kind { get; }

    /// <summary>The value type (nullable unwrapped).</summary>
    public Type ValueType { get; }

    public bool HasRange { get; }
    public double Min { get; }
    public double Max { get; }
    public double Step { get; }

    public string[] EnumNames { get; } = [];
    public long[] EnumValues { get; } = [];

    /// <summary>Element type of an <see cref="PropertyEditorKind.Array"/>.</summary>
    public Type? ElementType { get; }

    /// <summary>Declared type of a <see cref="PropertyEditorKind.Resource"/> slot (new resources must derive from it).</summary>
    public Type? ResourceType { get; }

    /// <summary>The <c>File</c> hint's patterns (<c>*.png</c>, …); empty for any file.</summary>
    public IReadOnlyList<string> FileFilter => SplitFilter(Info.Hints.File);

    /// <summary>The <c>NodeType</c> hint of a <see cref="NodePath"/> (the picker lists only these).</summary>
    public Type? NodeType => Info.Hints.NodeType;

    /// <summary>Number of editable fields: vectors and colours have several, everything else one.</summary>
    public int Components => Kind switch
    {
        PropertyEditorKind.Vector2 => 2,
        PropertyEditorKind.Vector3 or PropertyEditorKind.Quaternion => 3,
        PropertyEditorKind.Vector4 => 4,
        PropertyEditorKind.Color => ValueType == typeof(Vector3) ? 3 : 4,
        _ => 1,
    };

    /// <summary>Whether the colour components are 0..1 floats (vectors) or 0..255 bytes (<see cref="System.Drawing.Color"/>).</summary>
    public bool IsFloatColor => Kind == PropertyEditorKind.Color && ValueType != typeof(DrawingColor);

    public bool IsReadOnly => Kind is PropertyEditorKind.Transform or PropertyEditorKind.Unsupported;

    private string? _tooltip;

    /// <summary>
    /// The row's tooltip: "Label — doc summary", then the hints (range, file filter, node type …), then the member and its
    /// value type (<c>RotationDegrees: Vector3</c>).
    /// </summary>
    public string Tooltip => _tooltip ??= BuildTooltip();

    private string BuildTooltip()
    {
        var builder = new StringBuilder(Label);
        if (Info.Hints.Description is { Length: > 0 } description)
            builder.Append(" — ").Append(description);
        if (HasRange)
            builder.Append('\n').Append("Range ").Append(ValueText.Number(Min)).Append(" … ").Append(ValueText.Number(Max))
                .Append(", step ").Append(ValueText.Number(Step));
        if (Kind == PropertyEditorKind.FilePath)
            builder.Append('\n').Append(FileFilter.Count == 0 ? "Any file" : "Files: " + string.Join(", ", FileFilter));
        if (Kind == PropertyEditorKind.DirectoryPath)
            builder.Append('\n').Append("A folder");
        if (NodeType is { } nodeType)
            builder.Append('\n').Append("Points to a ").Append(nodeType.Name);
        if (Info.Hints.Translatable)
            builder.Append('\n').Append("Translated (the text is the source string)");
        builder.Append('\n').Append(Name).Append(": ").Append(TypeName(Info.ValueType));
        return builder.ToString();
    }

    /// <summary>A short C#-style name: <c>float</c>, <c>Vector3</c>, <c>List&lt;int&gt;</c>, <c>Mesh?</c>.</summary>
    public static string TypeName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (Nullable.GetUnderlyingType(type) is { } inner)
            return TypeName(inner) + "?";
        if (type.IsArray)
            return TypeName(type.GetElementType()!) + "[]";
        if (type.IsGenericType)
        {
            var name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
        }

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => "bool",
            TypeCode.Byte => "byte",
            TypeCode.SByte => "sbyte",
            TypeCode.Int16 => "short",
            TypeCode.UInt16 => "ushort",
            TypeCode.Int32 when !type.IsEnum => "int",
            TypeCode.UInt32 when !type.IsEnum => "uint",
            TypeCode.Int64 when !type.IsEnum => "long",
            TypeCode.UInt64 when !type.IsEnum => "ulong",
            TypeCode.Single => "float",
            TypeCode.Double => "double",
            TypeCode.String => "string",
            _ => type.Name,
        };
    }

    public object? GetValue() => Info.GetValue(Target);

    // ── Formatting ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The text of field <paramref name="component"/> (vector components, colour channels, or the whole value).</summary>
    public string FormatComponent(int component) => FormatComponent(GetValue(), component);

    internal string FormatComponent(object? value, int component)
    {
        switch (Kind)
        {
            case PropertyEditorKind.Vector2 or PropertyEditorKind.Vector3 or PropertyEditorKind.Vector4:
            case PropertyEditorKind.Color when IsFloatColor:
                return ValueText.Number(ComponentsOf(value)[component]);
            case PropertyEditorKind.Quaternion:
                return ValueText.Number(EulerAngles.FromQuaternion(value is Quaternion q ? q : Quaternion.Identity)[component]);
            case PropertyEditorKind.Color:
                var c = value is DrawingColor dc ? dc : DrawingColor.Empty;
                return (component switch { 0 => c.R, 1 => c.G, 2 => c.B, _ => c.A }).ToString(CultureInfo.InvariantCulture);
            default:
                return Format(value);
        }
    }

    /// <summary>The whole value as text (single-field editors, read-only rows, tooltips).</summary>
    public string Format(object? value) => Kind switch
    {
        PropertyEditorKind.Color => ValueText.ColorHex(value),
        PropertyEditorKind.Resource => ValueText.Resource(value as Resource),
        PropertyEditorKind.Array => value is ICollection list ? $"{list.Count} item{(list.Count == 1 ? "" : "s")}" : "null",
        _ => ValueText.Format(value),
    };

    /// <summary>The components of a vector/colour value as floats.</summary>
    public static float[] ComponentsOf(object? value) => value switch
    {
        Vector2 v => [v.X, v.Y],
        Vector3 v => [v.X, v.Y, v.Z],
        Vector4 v => [v.X, v.Y, v.Z, v.W],
        Quaternion q => [q.X, q.Y, q.Z, q.W],
        DrawingColor c => [c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f],
        _ => [0, 0, 0, 0],
    };

    /// <summary>Whether <paramref name="value"/> is "on" for flag <paramref name="flag"/>.</summary>
    public static bool HasFlag(object? value, long flag) =>
        value is not null && (Convert.ToInt64(value, CultureInfo.InvariantCulture) & flag) == flag && flag != 0;

    /// <summary>The flags worth a checkbox: single bits (combined members are shortcuts).</summary>
    public IEnumerable<(string Name, long Value)> FlagChoices()
    {
        for (var i = 0; i < EnumValues.Length; i++)
            if (EnumValues[i] != 0 && (EnumValues[i] & (EnumValues[i] - 1)) == 0)
                yield return (EnumNames[i], EnumValues[i]);
    }

    // ── Parsing ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The property's new value when field <paramref name="component"/> is set to <paramref name="text"/> (the other
    /// components keep their current values). False when the text does not parse.
    /// </summary>
    public bool TryParseComponent(int component, string text, out object? value) =>
        TryParseComponent(GetValue(), component, text, out value);

    internal bool TryParseComponent(object? current, int component, string text, out object? value)
    {
        value = null;
        text = text.Trim();
        switch (Kind)
        {
            case PropertyEditorKind.Vector2 or PropertyEditorKind.Vector3 or PropertyEditorKind.Vector4 or PropertyEditorKind.Color
                when IsFloatColor || Kind != PropertyEditorKind.Color:
                {
                    if (!ValueText.TryParseFloat(text, out var f))
                        return false;
                    var parts = ComponentsOf(current);
                    parts[component] = f;
                    value = ValueType == typeof(Vector2) ? new Vector2(parts[0], parts[1])
                        : ValueType == typeof(Vector3) ? new Vector3(parts[0], parts[1], parts[2])
                        : new Vector4(parts[0], parts[1], parts[2], parts[3]);
                    return true;
                }

            case PropertyEditorKind.Quaternion:
                {
                    if (!ValueText.TryParseFloat(text, out var f))
                        return false;
                    var degrees = EulerAngles.FromQuaternion(current is Quaternion q ? q : Quaternion.Identity);
                    degrees = component switch { 0 => degrees with { X = f }, 1 => degrees with { Y = f }, _ => degrees with { Z = f } };
                    value = EulerAngles.ToQuaternion(degrees);
                    return true;
                }

            case PropertyEditorKind.Color:
                {
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel))
                        return false;
                    channel = Math.Clamp(channel, 0, 255);
                    var c = current is DrawingColor dc ? dc : DrawingColor.Black;
                    value = component switch
                    {
                        0 => DrawingColor.FromArgb(c.A, channel, c.G, c.B),
                        1 => DrawingColor.FromArgb(c.A, c.R, channel, c.B),
                        2 => DrawingColor.FromArgb(c.A, c.R, c.G, channel),
                        _ => DrawingColor.FromArgb(channel, c.R, c.G, c.B),
                    };
                    return true;
                }

            default:
                return TryParse(text, out value);
        }
    }

    /// <summary>Parses a whole value from text (numbers clamp to a range hint; enums by name; colours as <c>#rrggbb[aa]</c>).</summary>
    public bool TryParse(string text, out object? value)
    {
        ArgumentNullException.ThrowIfNull(text);
        value = null;
        switch (Kind)
        {
            case PropertyEditorKind.Bool:
                if (!bool.TryParse(text.Trim(), out var b))
                    return false;
                value = b;
                return true;
            case PropertyEditorKind.IntegerNumber or PropertyEditorKind.FloatNumber or PropertyEditorKind.Range:
                return TryParseNumber(text, out value);
            case PropertyEditorKind.Text or PropertyEditorKind.MultilineText or PropertyEditorKind.FilePath or PropertyEditorKind.DirectoryPath:
                value = text;
                return true;
            case PropertyEditorKind.NodePath:
                value = new NodePath(text.Trim());
                return true;
            case PropertyEditorKind.Enum or PropertyEditorKind.Flags:
                if (Enum.TryParse(ValueType, text.Trim(), ignoreCase: true, out var e))
                {
                    value = e;
                    return true;
                }

                if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
                {
                    value = Enum.ToObject(ValueType, raw);
                    return true;
                }

                return false;
            case PropertyEditorKind.Color:
                if (!ValueText.TryParseColorHex(text, out var rgba))
                    return false;
                value = ValueType == typeof(DrawingColor)
                    ? DrawingColor.FromArgb(ToByte(rgba.W), ToByte(rgba.X), ToByte(rgba.Y), ToByte(rgba.Z))
                    : ValueType == typeof(Vector3) ? new Vector3(rgba.X, rgba.Y, rgba.Z) : rgba;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The enum value with underlying value <paramref name="raw"/> (flags: the combination).</summary>
    public object EnumFromRaw(long raw) => Enum.ToObject(ValueType, raw);

    private bool TryParseNumber(string text, out object? value)
    {
        value = null;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
            return false;
        if (HasRange)
            d = Math.Clamp(d, Math.Min(Min, Max), Math.Max(Min, Max));
        try
        {
            value = ValueType == typeof(float) ? (float)d
                : ValueType == typeof(double) ? d
                : Convert.ChangeType(Math.Round(d), ValueType, CultureInfo.InvariantCulture);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0, 255);

    // ── Classification ─────────────────────────────────────────────────────────────────────────────────────────

    internal static PropertyEditorKind Classify(ExportPropertyInfo info, Type type)
    {
        var hints = info.Hints;
        if (type == typeof(bool))
            return PropertyEditorKind.Bool;
        if (IsIntegerType(type) || type == typeof(float) || type == typeof(double))
            return hints.Range is not null ? PropertyEditorKind.Range : IsIntegerType(type) ? PropertyEditorKind.IntegerNumber : PropertyEditorKind.FloatNumber;
        if (type == typeof(string))
        {
            if (hints.File is not null)
                return PropertyEditorKind.FilePath;
            if (hints.Directory)
                return PropertyEditorKind.DirectoryPath;
            return hints.Multiline ? PropertyEditorKind.MultilineText : PropertyEditorKind.Text;
        }

        if (type.IsEnum)
            return hints.Flags || type.IsDefined(typeof(FlagsAttribute), inherit: false) ? PropertyEditorKind.Flags : PropertyEditorKind.Enum;
        var namedColor = info.Name.EndsWith("Color", StringComparison.Ordinal) || info.Name.EndsWith("Colour", StringComparison.Ordinal);
        if (type == typeof(Vector2))
            return PropertyEditorKind.Vector2;
        if (type == typeof(Vector3))
            return namedColor ? PropertyEditorKind.Color : PropertyEditorKind.Vector3;
        if (type == typeof(Vector4))
            return namedColor ? PropertyEditorKind.Color : PropertyEditorKind.Vector4;
        if (type == typeof(Quaternion))
            return PropertyEditorKind.Quaternion;
        if (type == typeof(DrawingColor))
            return PropertyEditorKind.Color;
        if (type == typeof(NodePath))
            return PropertyEditorKind.NodePath;
        if (typeof(Resource).IsAssignableFrom(type))
            return PropertyEditorKind.Resource;
        if (type == typeof(Transform3D) || type == typeof(Transform2D))
            return PropertyEditorKind.Transform;
        if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
            return PropertyEditorKind.Array;
        return PropertyEditorKind.Unsupported;
    }

    internal static bool IsIntegerType(Type type) =>
        type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
        type == typeof(short) || type == typeof(ushort) || type == typeof(byte) || type == typeof(sbyte);

    /// <summary>"CastShadows" → "Cast Shadows", "UvScale" → "Uv Scale", "FOV" → "FOV".</summary>
    public static string Humanize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '_')
            {
                builder.Append(' ');
                continue;
            }

            if (i > 0 && char.IsUpper(c) && name[i - 1] != '_' &&
                (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
                builder.Append(' ');
            builder.Append(c);
        }

        return builder.ToString();
    }

    internal static IReadOnlyList<string> SplitFilter(string? filter) =>
        string.IsNullOrWhiteSpace(filter)
            ? []
            : filter.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public override string ToString() => $"{Label} ({Kind})";
}
