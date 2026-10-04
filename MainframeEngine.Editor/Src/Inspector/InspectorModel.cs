using System.Reflection;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>A titled group of inspector rows: one per <c>[ExportGroup]</c>, plus one per declaring type for ungrouped members.</summary>
/// <param name="Title">The group name, or the declaring type's name.</param>
/// <param name="Properties">The rows.</param>
/// <param name="DeclaringType">The type whose members the section lists (the first row's declaring type for a group).</param>
/// <param name="IsGroup">An <c>[ExportGroup]</c> section (its header shows a group icon instead of the type's).</param>
public sealed record InspectorSection(string Title, IReadOnlyList<InspectorProperty> Properties, Type? DeclaringType = null, bool IsGroup = false)
{
    /// <summary>The header icon's classes: the declaring type's icon, or a group icon.</summary>
    public string IconClasses => IsGroup || DeclaringType is null ? "icon icon-category icon-muted" : EditorIcons.Classes(DeclaringType);
}

/// <summary>
/// The inspector's property model for an object (node or resource): the generated <see cref="NodeTypeInfo"/>'s
/// <see cref="NodeTypeInfo.Properties"/> (base types first) turned into <see cref="InspectorProperty"/> rows, grouped into
/// sections — members without an <c>[ExportGroup]</c> under their declaring type's name ("Node3D", "Light3D"), grouped
/// ones under their group. A registered <see cref="ICustomInspector"/> may hide rows and add its own RML.
/// </summary>
public sealed class InspectorModel
{
    private InspectorModel(object target, NodeTypeInfo? type, IReadOnlyList<InspectorSection> sections, ICustomInspector? custom)
    {
        Target = target;
        TypeInfo = type;
        Sections = sections;
        CustomInspector = custom;
    }

    public object Target { get; }

    /// <summary>The registered type info (null for unregistered types, which show no properties).</summary>
    public NodeTypeInfo? TypeInfo { get; }

    public IReadOnlyList<InspectorSection> Sections { get; }

    /// <summary>The custom inspector for the target's type, if any.</summary>
    public ICustomInspector? CustomInspector { get; }

    /// <summary>Every row, in display order.</summary>
    public IEnumerable<InspectorProperty> Properties => Sections.SelectMany(s => s.Properties);

    /// <summary>The row for property <paramref name="name"/>, or null.</summary>
    public InspectorProperty? Find(string name) => Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>Builds the model for <paramref name="target"/>.</summary>
    public static InspectorModel Build(object target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var info = TypeRegistry.GetNearest(target.GetType());
        var custom = CustomInspectors.Find(target.GetType());
        if (info is null)
            return new InspectorModel(target, null, [], custom);

        var sections = new List<(string Title, List<InspectorProperty> Rows, Type Type, bool IsGroup)>();
        foreach (var property in info.Properties)
        {
            if (custom is not null && !custom.ShowProperty(target, property))
                continue;
            var title = property.Group ?? property.DeclaringType.Name;
            var section = sections.FindIndex(s => string.Equals(s.Title, title, StringComparison.Ordinal));
            if (section < 0)
            {
                sections.Add((title, [], property.DeclaringType.Type, property.Group is not null));
                section = sections.Count - 1;
            }

            sections[section].Rows.Add(new InspectorProperty(target, property));
        }

        return new InspectorModel(target, info, [.. sections.Select(s => new InspectorSection(s.Title, s.Rows, s.Type, s.IsGroup))], custom);
    }
}

/// <summary>
/// Extends the inspector for a node or resource type (the plugin hook, Godot's <c>EditorInspectorPlugin</c>): mark the
/// class <see cref="CustomInspectorAttribute"/> with the target type; it needs a public parameterless constructor.
/// </summary>
public interface ICustomInspector
{
    /// <summary>RML shown above the generated rows (null for none). Elements with <c>data-action="name"</c> call <see cref="OnAction"/>.</summary>
    string? GetHeaderRml(object target) => null;

    /// <summary>Whether a generated row is shown (hide properties the custom RML edits).</summary>
    bool ShowProperty(object target, ExportPropertyInfo property) => true;

    /// <summary>A click on an element with <c>data-action</c> in the header RML. Edit through <paramref name="scene"/> so it can be undone.</summary>
    void OnAction(object target, string action, EditedScene scene)
    {
    }
}

/// <summary>Registers an <see cref="ICustomInspector"/> for <see cref="TargetType"/> and its subclasses.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CustomInspectorAttribute(Type targetType) : Attribute
{
    public Type TargetType { get; } = targetType ?? throw new ArgumentNullException(nameof(targetType));
}

/// <summary>Finds <see cref="CustomInspectorAttribute"/> classes in the loaded assemblies (the editor's and, from E4, game assemblies).</summary>
public static class CustomInspectors
{
    private static readonly Lock Gate = new();
    private static Dictionary<Type, Type>? _byTarget;
    private static readonly Dictionary<Type, ICustomInspector?> Cache = [];

    /// <summary>The inspector for <paramref name="type"/> or its nearest base type with one, or null.</summary>
    public static ICustomInspector? Find(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        lock (Gate)
        {
            if (Cache.TryGetValue(type, out var cached))
                return cached;
            _byTarget ??= Scan(AppDomain.CurrentDomain.GetAssemblies());
            ICustomInspector? found = null;
            for (var t = type; t is not null && found is null; t = t.BaseType)
                if (_byTarget.TryGetValue(t, out var inspectorType))
                    found = (ICustomInspector?)Activator.CreateInstance(inspectorType);
            Cache[type] = found;
            return found;
        }
    }

    /// <summary>Forgets what was scanned (after loading or unloading a game assembly).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _byTarget = null;
            Cache.Clear();
        }
    }

    private static Dictionary<Type, Type> Scan(IEnumerable<Assembly> assemblies)
    {
        var result = new Dictionary<Type, Type>();
        var editor = typeof(ICustomInspector).Assembly;
        foreach (var assembly in assemblies)
        {
            if (assembly.IsDynamic || (assembly != editor && !assembly.GetReferencedAssemblies().Any(a => a.Name == editor.GetName().Name)))
                continue;
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = [.. e.Types.OfType<Type>()];
            }

            foreach (var type in types)
                if (typeof(ICustomInspector).IsAssignableFrom(type) && !type.IsAbstract &&
                    type.GetCustomAttribute<CustomInspectorAttribute>() is { } attribute)
                    result[attribute.TargetType] = type;
        }

        return result;
    }
}

/// <summary>The built-in custom inspector for <see cref="MissingNode"/>: explains why there are no editable properties.</summary>
[CustomInspector(typeof(MissingNode))]
public sealed class MissingNodeInspector : ICustomInspector
{
    public string? GetHeaderRml(object target)
    {
        var missing = (MissingNode)target;
        var properties = missing.RawProperties.ValueKind == System.Text.Json.JsonValueKind.Object
            ? missing.RawProperties.EnumerateObject().Count()
            : 0;
        return $"<div class=\"notice\">Type <b>{RmlText.Escape(missing.OriginalType)}</b> is not loaded. Its {properties} " +
               "saved propert" + (properties == 1 ? "y is" : "ies are") + " kept unchanged and written back on save.</div>";
    }
}
