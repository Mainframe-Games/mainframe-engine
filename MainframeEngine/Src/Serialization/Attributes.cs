namespace MainframeEngine;

/// <summary>
/// Marks a property or field of a <see cref="Node"/> or <see cref="Resource"/> as serialized (written to
/// <c>.mscene</c>/<c>.mres</c> when it differs from the type's default) and shown in the editor inspector.
/// The member must be readable and writable from the declaring assembly (public or internal).
/// <c>MainframeEngine.Generators</c> emits typed accessors for it; no reflection is used at runtime.
/// </summary>
/// <remarks>
/// Supported types: <c>bool</c>, integer and floating-point primitives, <c>string</c>, enums,
/// <c>Vector2/3/4</c>, <c>Quaternion</c>, <c>System.Drawing.Color</c>, <see cref="Transform3D"/>,
/// <see cref="Transform2D"/>, <see cref="Rect2"/>, <see cref="NodePath"/>, <see cref="Resource"/> subclasses (inline or external)
/// and arrays / <c>List&lt;T&gt;</c> of these.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
public sealed class ExportAttribute : Attribute
{
    /// <summary>Inspector range <c>"min,max[,step]"</c>, e.g. <c>"0,16,0.01"</c>.</summary>
    public string? Range { get; set; }

    /// <summary>The value is a file path; the filter is a glob list such as <c>"*.png,*.jpg"</c> (empty for any file).</summary>
    public string? File { get; set; }

    /// <summary>The value is a folder path.</summary>
    public bool Directory { get; set; }

    /// <summary>Edit the string in a multi-line text box.</summary>
    public bool Multiline { get; set; }

    /// <summary>Edit the enum as a set of flags.</summary>
    public bool Flags { get; set; }

    /// <summary>For <see cref="NodePath"/> values: the node type the path must point to.</summary>
    public Type? NodeType { get; set; }

    /// <summary>
    /// The string is player-facing text: <c>mf-l10n extract</c> writes its scene values to the translation template,
    /// and the node shows it through <see cref="Node.Atr"/> (re-translating in <see cref="Node.OnLocaleChanged"/>).
    /// The stored value stays the source text (the msgid). Only <c>string</c>, <c>string[]</c> and
    /// <c>List&lt;string&gt;</c> members may be translatable (MFG010).
    /// </summary>
    public bool Translatable { get; set; }

    /// <summary>
    /// The inspector icon shown before the property's name: a Tabler icon name such as <c>"gauge"</c> (see
    /// <see cref="EditorIconAttribute"/>). Default: chosen by the editor from the name and value type.
    /// </summary>
    public string? Icon { get; set; }
}

/// <summary>
/// The colour family of an editor icon (Godot-style tints; the editor's <c>icon-3d</c> … <c>icon-resource</c> classes).
/// <see cref="Inherit"/> takes the nearest base type's family.
/// </summary>
public enum EditorIconFamily
{
    /// <summary>Use the nearest base type's family (the default).</summary>
    Inherit = 0,

    /// <summary>Plain nodes and logic (grey).</summary>
    Logic,

    /// <summary>3D nodes (red).</summary>
    Space3D,

    /// <summary>2D nodes (blue).</summary>
    Space2D,

    /// <summary>User interface (green).</summary>
    Ui,

    /// <summary>Audio (teal).</summary>
    Audio,

    /// <summary>Physics bodies, areas and shapes (amber).</summary>
    Physics,

    /// <summary>Networking (purple).</summary>
    Network,

    /// <summary>Resources (neutral).</summary>
    Resource,
}

/// <summary>
/// The editor icon of a node or resource type (scene tree, Add Node dialog, inspector header, file system) or of an
/// exported member (inspector row): a <a href="https://tabler.io/icons">Tabler</a> icon name such as <c>"cube"</c>,
/// which must be in the editor's icon atlas (<c>MainframeEngine.Editor/Content/icons/icons.txt</c>). Types without one
/// use their nearest base type's icon; an empty name keeps the inherited icon and only sets <see cref="Family"/>.
/// <c>MainframeEngine.Generators</c> records it in the type's <see cref="Serialization.NodeTypeInfo"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class EditorIconAttribute(string name) : Attribute
{
    /// <summary>The Tabler icon name ("" to inherit).</summary>
    public string Name { get; } = name ?? "";

    /// <summary>The icon's colour family (types only); <see cref="EditorIconFamily.Inherit"/> takes the base type's.</summary>
    public EditorIconFamily Family { get; set; }
}

/// <summary>
/// Starts an inspector group: this and the following <see cref="ExportAttribute"/> members of the class
/// belong to it, until the next group. An empty name ends grouping.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
public sealed class ExportGroupAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// Marks a game-defined value type (struct) as storable in scenes and resources: <c>[Export]</c> members may have this
/// type once a codec is registered with <see cref="Serialization.Codecs.Register{T}"/> (for example
/// <see cref="Serialization.Codecs.FloatArray{T}"/>) before anything is loaded — typically in a module initializer.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class SerializableValueAttribute : Attribute;

/// <summary>
/// Marks a C# event as a signal: listed in the editor's Signals panel and connectable by name
/// (<see cref="Node.Connect"/>, serialized scene connections). The event stays a normal C# event.
/// </summary>
[AttributeUsage(AttributeTargets.Event, Inherited = true)]
public sealed class SignalAttribute : Attribute;

/// <summary>Allows a non-public method to be the target of a named signal connection.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class SignalHandlerAttribute : Attribute;

/// <summary>The node's process callbacks also run in the editor (Godot's <c>@tool</c>).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ToolAttribute : Attribute;

/// <summary>
/// Overrides the type name written to scene files (default: the class name). Type names must be unique across
/// every loaded assembly.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TypeNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// The current serialized layout version of a type (default 1). Files store the version they were written with;
/// on load, <see cref="SerializedMigrationAttribute"/> methods upgrade older data step by step.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SerializedVersionAttribute(int version) : Attribute
{
    public int Version { get; } = version;
}

/// <summary>
/// Marks a <c>static void M(MainframeEngine.Serialization.PropertyBag properties)</c> method that upgrades this
/// type's serialized properties from <see cref="FromVersion"/> to <c>FromVersion + 1</c> (rename, convert or
/// drop properties) before they are applied.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class SerializedMigrationAttribute(int fromVersion) : Attribute
{
    public int FromVersion { get; } = fromVersion;
}
