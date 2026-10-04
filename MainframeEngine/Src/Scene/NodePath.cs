namespace MainframeEngine;

/// <summary>
/// A path to a node, as in Godot. Relative paths start from the node they are resolved on
/// (<c>"Camera"</c>, <c>"../Player/Camera"</c>, <c>"."</c>); absolute paths start at the tree root
/// (<c>"/root/Main"</c>). A segment <c>%Name</c> looks up a scene-unique node
/// (<see cref="Node.UniqueNameInOwner"/>). Resolution (<see cref="Node.GetNode{T}"/>) does not allocate.
/// </summary>
public readonly struct NodePath : IEquatable<NodePath>
{
    private readonly string? _path;

    public NodePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _path = path;
    }

    public static NodePath Empty => default;

    public string Path => _path ?? string.Empty;

    public bool IsEmpty => string.IsNullOrEmpty(_path);

    public bool IsAbsolute => _path is { Length: > 0 } p && p[0] == '/';

    /// <summary>The path's node names (allocates; for tooling, not per-frame lookups).</summary>
    public string[] GetNames() => Path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    public static implicit operator NodePath(string path) => new(path);

    public static NodePath FromString(string path) => new(path);

    public override string ToString() => Path;

    public bool Equals(NodePath other) => string.Equals(Path, other.Path, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is NodePath other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Path);

    public static bool operator ==(NodePath left, NodePath right) => left.Equals(right);

    public static bool operator !=(NodePath left, NodePath right) => !left.Equals(right);
}
