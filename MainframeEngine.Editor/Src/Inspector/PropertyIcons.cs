using System.Text;

namespace MainframeEngine.Editor;

/// <summary>
/// The icon before an inspector property's name (docs/design/editor.md#inspector): the member's own icon
/// (<c>[Export(Icon = …)]</c> / <c>[EditorIcon]</c>) when it has one; a resource slot shows its resource type's icon and a
/// <see cref="NodePath"/> its target node's; then a semantic icon from the name (Position, Rotation, Scale, Visible,
/// colours, volume, mass, speed, text …); else the value kind's icon (number <c>hash</c>, bool <c>toggle-left</c>, text
/// <c>letter-t</c>, vector <c>axis-x</c>, enum <c>list</c>, array <c>brackets</c> …).
/// </summary>
public static class PropertyIcons
{
    // Exact property names → icon.
    private static readonly Dictionary<string, string> ByName = new(StringComparer.Ordinal)
    {
        ["Position"] = "arrows-move",
        ["GlobalPosition"] = "arrows-move",
        ["Offset"] = "arrows-move",
        ["Rotation"] = "rotate",
        ["RotationDegrees"] = "rotate",
        ["GlobalRotation"] = "rotate",
        ["Scale"] = "arrows-maximize",
        ["Visible"] = "eye",
        ["Text"] = "letter-t",
        ["Title"] = "letter-t",
        ["Volume"] = "volume",
        ["VolumeDb"] = "volume",
        ["Mass"] = "weight",
        ["Energy"] = "bolt",
        ["Intensity"] = "bolt",
        ["Range"] = "ruler-2",
        ["Distance"] = "ruler-2",
        ["MaxDistance"] = "ruler-2",
        ["UnitSize"] = "ruler-2",
        ["Near"] = "ruler-2",
        ["Far"] = "ruler-2",
        ["Fov"] = "aspect-ratio",
        ["FieldOfView"] = "aspect-ratio",
        ["Size"] = "dimensions",
        ["Extents"] = "dimensions",
        ["Radius"] = "dimensions",
        ["Height"] = "dimensions",
        ["Width"] = "dimensions",
        ["Length"] = "dimensions",
        ["CollisionLayer"] = "stack-2",
        ["CollisionMask"] = "stack-2",
        ["Layer"] = "stack-2",
        ["ProcessMode"] = "cpu",
        ["ProcessPriority"] = "cpu",
        ["PhysicsProcessPriority"] = "cpu",
        ["Autoplay"] = "player-play",
        ["Autostart"] = "player-play",
        ["Playing"] = "player-play",
        ["Loop"] = "repeat",
        ["Looping"] = "repeat",
        ["Bus"] = "adjustments",
        ["WaitTime"] = "hourglass",
        ["Duration"] = "hourglass",
        ["OneShot"] = "clock",
        ["CastShadows"] = "shadow",
        ["Shadows"] = "shadow",
        ["ShadowEnabled"] = "shadow",
        ["Exposure"] = "contrast",
        ["AutoTranslateMode"] = "language",
        ["Locale"] = "language",
        ["UniqueNameInOwner"] = "at",
        ["Current"] = "star",
        ["GravityScale"] = "arrow-bar-to-down",
        ["Gravity"] = "arrow-bar-to-down",
        ["Friction"] = "droplet",
        ["Bounce"] = "ball-bowling",
        ["Pitch"] = "music",
        ["PitchScale"] = "music",
        ["Tags"] = "tag",
        ["Name"] = "tag",
    };

    /// <summary>The icon name for <paramref name="property"/> showing <paramref name="value"/>.</summary>
    public static string For(InspectorProperty property, object? value)
    {
        ArgumentNullException.ThrowIfNull(property);
        return Resolve(property, value).Icon;
    }

    /// <summary>The icon's class list (<c>"icon icon-NAME [family]"</c>): resource and node icons keep their family tint.</summary>
    public static string Classes(InspectorProperty property, object? value)
    {
        ArgumentNullException.ThrowIfNull(property);
        var (icon, family) = Resolve(property, value);
        var builder = new StringBuilder("icon icon-", 40).Append(icon);
        if (family is not null)
            builder.Append(' ').Append(family);
        return builder.ToString();
    }

    private static (string Icon, string? Family) Resolve(InspectorProperty p, object? value)
    {
        if (p.Info.Hints.Icon is { Length: > 0 } declared)
            return (declared, null);
        switch (p.Kind)
        {
            case PropertyEditorKind.Resource:
                {
                    var type = value?.GetType() ?? p.ResourceType ?? typeof(Resource);
                    return (EditorIcons.For(type), EditorIcons.Family(type));
                }

            case PropertyEditorKind.NodePath:
                {
                    var target = value is NodePath { IsEmpty: false } path && p.Target is Node { IsInsideTree: true } owner
                        ? owner.GetNodeOrNull(path)
                        : null;
                    if (target is not null)
                        return (EditorIcons.For(target), target is MissingNode ? "icon-missing" : EditorIcons.Family(target.GetType()));
                    if (p.NodeType is { } nodeType)
                        return (EditorIcons.For(nodeType), EditorIcons.Family(nodeType));
                    return ("target", null);
                }
        }

        if (SemanticIcon(p.Name) is { } semantic)
            return (semantic, null);
        return (KindIcon(p.Kind), null);
    }

    /// <summary>The icon a property name implies (exact names, then <c>…Color</c>, <c>…Velocity</c>, <c>…Speed</c>), or null.</summary>
    public static string? SemanticIcon(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (ByName.TryGetValue(name, out var icon))
            return icon;
        if (name.EndsWith("Color", StringComparison.Ordinal) || name.EndsWith("Colour", StringComparison.Ordinal))
            return "palette";
        if (name.EndsWith("Velocity", StringComparison.Ordinal) || name.EndsWith("Speed", StringComparison.Ordinal))
            return "gauge";
        if (name.StartsWith("Shadow", StringComparison.Ordinal))
            return "shadow";
        return null;
    }

    /// <summary>The icon of a value kind.</summary>
    public static string KindIcon(PropertyEditorKind kind) => kind switch
    {
        PropertyEditorKind.Bool => "toggle-left",
        PropertyEditorKind.IntegerNumber or PropertyEditorKind.FloatNumber => "hash",
        PropertyEditorKind.Range => "adjustments-horizontal",
        PropertyEditorKind.Text => "letter-t",
        PropertyEditorKind.MultilineText => "file-text",
        PropertyEditorKind.FilePath => "file",
        PropertyEditorKind.DirectoryPath => "folder",
        PropertyEditorKind.Enum => "list",
        PropertyEditorKind.Flags => "list-check",
        PropertyEditorKind.Vector2 or PropertyEditorKind.Vector3 or PropertyEditorKind.Vector4 => "axis-x",
        PropertyEditorKind.Quaternion => "rotate",
        PropertyEditorKind.Color => "palette",
        PropertyEditorKind.NodePath => "target",
        PropertyEditorKind.Resource => "package",
        PropertyEditorKind.Array => "brackets",
        PropertyEditorKind.Transform => "transform",
        _ => "question-mark",
    };
}
