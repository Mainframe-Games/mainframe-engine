using System.Text.Json;

namespace MainframeEngine;

/// <summary>
/// Stands in for a node whose type is not registered (a class was renamed or deleted, or its assembly is not
/// loaded). It keeps the serialized type name and properties so saving the scene again loses nothing, like
/// Godot's handling of missing scripts. Its children load normally.
/// </summary>
[EditorIcon("help-hexagon")]
public sealed class MissingNode : Node
{
    public MissingNode()
        : this(string.Empty, 1, default)
    {
    }

    internal MissingNode(string originalType, int version, JsonElement properties)
    {
        OriginalType = originalType;
        Version = version;
        RawProperties = properties.ValueKind == JsonValueKind.Undefined ? default : properties.Clone();
    }

    /// <summary>The type name stored in the file.</summary>
    public string OriginalType { get; }

    /// <summary>The serialized version stored in the file.</summary>
    public int Version { get; }

    /// <summary>The properties as stored in the file (written back unchanged).</summary>
    public JsonElement RawProperties { get; }

    /// <summary>Resources the raw properties reference (<c>{"res": key}</c>), kept so saving writes them again.</summary>
    internal IReadOnlyDictionary<string, Resource>? ResourceReferences { get; set; }
}

/// <summary>Stands in for a resource whose type is not registered; see <see cref="MissingNode"/>.</summary>
[EditorIcon("help-hexagon")]
public sealed class MissingResource : Resource
{
    public MissingResource()
        : this(string.Empty, 1, default)
    {
    }

    internal MissingResource(string originalType, int version, JsonElement properties)
    {
        OriginalType = originalType;
        Version = version;
        RawProperties = properties.ValueKind == JsonValueKind.Undefined ? default : properties.Clone();
    }

    public string OriginalType { get; }

    public int Version { get; }

    public JsonElement RawProperties { get; }

    /// <summary>Resources the raw properties reference, kept so saving writes them again.</summary>
    internal IReadOnlyDictionary<string, Resource>? ResourceReferences { get; set; }
}
