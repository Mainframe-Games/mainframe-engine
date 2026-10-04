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
/// <see cref="Transform2D"/>, <see cref="NodePath"/>, <see cref="Resource"/> subclasses (inline or external)
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
