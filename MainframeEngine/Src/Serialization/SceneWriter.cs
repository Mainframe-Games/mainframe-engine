using System.Buffers;
using System.Text.Json;

namespace MainframeEngine.Serialization;

/// <summary>
/// Writes a node tree (or a resource) as a <c>.mscene</c> (<c>.mres</c>) document. Only nodes owned by the root
/// are written, as a flat list in tree order with each node's parent path; nested scene instances become an
/// instance reference plus the properties that differ from a pristine instance of the sub-scene; properties equal
/// to the type's default are skipped; resources are collected into the file's table (inline) or referenced by UID
/// (external), under keys that stay the same from save to save.
/// </summary>
internal sealed class SceneWriter : SerializationContext
{
    // The discovery pass writes fragments (several top-level values) to a null stream.
    private static readonly JsonWriterOptions DiscoveryOptions = new() { SkipValidation = true };

    private readonly List<Resource> _resources = [];
    private readonly Dictionary<Resource, string> _keys = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _usedKeys = new(StringComparer.Ordinal);
    private readonly List<PackedScene> _loadedSubScenes = [];
    private readonly AssetDatabase _assets = AssetDatabase.Current;
    private Node _root = null!;

    // Where the value being written lives (node path or resource key, and member): the seed of a new resource's key.
    private string _siteOwner = string.Empty;
    private string _siteMember = string.Empty;

    public override string AddResource(Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (_keys.TryGetValue(resource, out var key))
            return key;
        if (resource is PackedScene { IsExternal: false })
            throw new InvalidOperationException("A PackedScene can only be referenced once it is saved to a file.");

        key = NewKey(resource);
        _keys[resource] = key;
        _usedKeys.Add(key);
        _resources.Add(resource);
        return key;
    }

    /// <summary>
    /// The resource's table key, <c>Type_xxxxx</c>. An inline resource keeps the key it was loaded or last saved with;
    /// a new one gets a key hashed from where it is first used (node path and property), so saving the same tree twice
    /// writes the same keys and adding a resource never renames the others. External references are keyed by their UID.
    /// </summary>
    private string NewKey(Resource resource)
    {
        if (resource.IsExternal)
            return UniqueKey(TypeNameOf(resource), resource.Uid ?? resource.ResourcePath!);
        if (resource.SceneLocalId is { Length: > 0 } kept && !_usedKeys.Contains(kept))
            return kept;
        var key = UniqueKey(TypeNameOf(resource), $"{_siteOwner}:{_siteMember}");
        resource.SceneLocalId = key;
        return key;
    }

    private string UniqueKey(string typeName, string seed)
    {
        for (var attempt = 0; ; attempt++)
        {
            var key = $"{typeName}_{Hash5(attempt == 0 ? seed : $"{seed}#{attempt}")}";
            if (!_usedKeys.Contains(key))
                return key;
        }
    }

    private static string TypeNameOf(Resource resource) => resource is MissingResource missing
        ? missing.OriginalType
        : TypeRegistry.Get(resource.GetType())?.Name ?? resource.GetType().Name;

    // Five base-36 digits (60 million values) of a 64-bit FNV-1a hash: short, and collisions within a file are retried.
    private static string Hash5(string seed)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(seed))
            hash = (hash ^ b) * 1099511628211UL;

        Span<char> digits = stackalloc char[5];
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            digits[i] = "0123456789abcdefghijklmnopqrstuvwxyz"[(int)(hash % 36)];
            hash /= 36;
        }

        return new string(digits);
    }

    /// <summary>Serializes the scene rooted at <paramref name="root"/>.</summary>
    public byte[] WriteScene(Node root, string? uid)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = root;
        try
        {
            // Pass 1 discovers every resource (nodes, then resources referencing resources), so pass 2 can write
            // the table before the nodes with stable keys.
            using (var sink = new Utf8JsonWriter(Stream.Null, DiscoveryOptions))
            {
                WriteNodes(sink);
                for (var i = 0; i < _resources.Count; i++)
                    WriteResourceEntry(sink, _resources[i]);
            }

            var discovered = _resources.Count;
            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer, SceneFormat.CompactWriteOptions))
            {
                w.WriteStartObject();
                w.WriteNumber("format", SceneFormat.Current);
                if (uid is not null)
                    w.WriteString("uid", uid);
                WriteResourceTable(w);
                WriteNodes(w);
                WriteConnections(w);
                w.WriteEndObject();
            }

            if (_resources.Count != discovered)
                throw new InvalidOperationException("Exported resource properties changed while saving; save again.");
            return SceneJsonLayout.Format(buffer.WrittenSpan);
        }
        finally
        {
            ReleaseSubScenes();
        }
    }

    /// <summary>Serializes <paramref name="resource"/> as a standalone resource file.</summary>
    public byte[] WriteResource(Resource resource, string uid)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var info = InfoFor(resource);

        // The resource itself is the file, not a table entry; its sub-resources go in the table.
        using (var sink = new Utf8JsonWriter(Stream.Null, DiscoveryOptions))
        {
            _siteOwner = string.Empty;
            WriteProps(sink, resource, info, info.DefaultInstance, "props");
            for (var i = 0; i < _resources.Count; i++)
                WriteResourceEntry(sink, _resources[i]);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, SceneFormat.CompactWriteOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("format", SceneFormat.Current);
            w.WriteString("uid", uid);
            w.WriteString("type", info.Name);
            if (info.Version != 1)
                w.WriteNumber("v", info.Version);
            WriteResourceTable(w);
            WriteProps(w, resource, info, info.DefaultInstance, "props");
            w.WriteEndObject();
        }

        return SceneJsonLayout.Format(buffer.WrittenSpan);
    }

    private void WriteResourceTable(Utf8JsonWriter w)
    {
        if (_resources.Count == 0)
            return;
        w.WriteStartObject("resources");
        for (var i = 0; i < _resources.Count; i++)
        {
            w.WritePropertyName(_keys[_resources[i]]);
            WriteResourceEntry(w, _resources[i]);
        }

        w.WriteEndObject();
    }

    private void WriteResourceEntry(Utf8JsonWriter w, Resource resource)
    {
        _siteOwner = _keys[resource];
        w.WriteStartObject();
        if (resource.IsExternal)
        {
            if (resource.Uid is not null)
                w.WriteString("ref", resource.Uid);
            w.WriteString("path", _assets.ToProjectPath(resource.ResourcePath!));
        }
        else if (resource is MissingResource missing)
        {
            w.WriteString("type", missing.OriginalType);
            if (missing.Version != 1)
                w.WriteNumber("v", missing.Version);
            if (missing.RawProperties.ValueKind == JsonValueKind.Object)
            {
                w.WritePropertyName("props");
                _siteMember = "props";
                RawProperties.Write(w, missing.RawProperties, missing.ResourceReferences, this);
            }
        }
        else
        {
            var info = InfoFor(resource);
            w.WriteString("type", info.Name);
            if (info.Version != 1)
                w.WriteNumber("v", info.Version);
            WriteProps(w, resource, info, info.DefaultInstance, "props");
        }

        w.WriteEndObject();
    }

    /// <summary>
    /// The <c>"nodes"</c> list: the root, then every node owned by it in tree order (parents before children). Inside a
    /// nested instance, the instance's own nodes are not listed but are searched for nodes this scene added under them.
    /// </summary>
    private void WriteNodes(Utf8JsonWriter w)
    {
        w.WriteStartArray("nodes");
        WriteNode(w, _root);
        WriteDescendants(w, _root, insideInstance: false);
        w.WriteEndArray();
    }

    private void WriteDescendants(Utf8JsonWriter w, Node node, bool insideInstance)
    {
        foreach (var child in node.Children)
        {
            if (ReferenceEquals(child.Owner, _root))
            {
                WriteNode(w, child);
                WriteDescendants(w, child, insideInstance: child.SceneFilePath is not null);
            }
            else if (insideInstance)
            {
                WriteDescendants(w, child, insideInstance: true);
            }
        }
    }

    private void WriteNode(Utf8JsonWriter w, Node node)
    {
        var isRoot = ReferenceEquals(node, _root);
        _siteOwner = isRoot ? "." : _root.GetPathTo(node).Path;
        w.WriteStartObject();
        w.WriteString("name", node.Name);
        if (!isRoot)
            w.WriteString("parent", _root.GetPathTo(node.Parent!).Path);

        if (!isRoot && node.SceneFilePath is not null)
        {
            WriteInstance(w, node);
        }
        else if (node is MissingNode missing)
        {
            w.WriteString("type", missing.OriginalType);
            if (missing.Version != 1)
                w.WriteNumber("v", missing.Version);
            if (missing.RawProperties.ValueKind == JsonValueKind.Object)
            {
                w.WritePropertyName("props");
                _siteMember = "props";
                RawProperties.Write(w, missing.RawProperties, missing.ResourceReferences, this);
            }

            WriteGroups(w, node, null);
        }
        else
        {
            var info = InfoFor(node);
            w.WriteString("type", info.Name);
            if (info.Version != 1)
                w.WriteNumber("v", info.Version);
            WriteProps(w, node, info, info.DefaultInstance, "props");
            WriteGroups(w, node, null);
        }

        w.WriteEndObject();
    }

    private void WriteInstance(Utf8JsonWriter w, Node instance)
    {
        var sub = ResourceLoader.Load<PackedScene>(instance.SceneFilePath!);
        _loadedSubScenes.Add(sub);

        var pristine = sub.Instantiate();
        try
        {
            if (sub.Uid is not null)
                w.WriteString("instance", sub.Uid);
            else
                w.WriteNull("instance");
            w.WriteString("path", _assets.ToProjectPath(sub.ResourcePath ?? instance.SceneFilePath!));
            var rootInfo = InfoFor(instance);
            if (rootInfo.Version != 1)
                w.WriteNumber("v", rootInfo.Version);

            WriteProps(w, instance, rootInfo, pristine, "props");
            WriteGroups(w, instance, pristine);
            WriteOverrides(w, instance, pristine);
        }
        finally
        {
            pristine.Free();
        }
    }

    // Nodes inside the instance (owned by it or by nested instances) whose properties differ from the
    // pristine sub-scene, keyed by path from the instance root.
    private void WriteOverrides(Utf8JsonWriter w, Node instance, Node pristine)
    {
        var started = false;
        List<(string Path, int Version)>? versions = null;
        var stack = new Stack<Node>();
        for (var i = instance.ChildCount - 1; i >= 0; i--)
            stack.Push(instance.GetChild(i));

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (ReferenceEquals(node.Owner, _root))
                continue; // added by this scene: listed as a node of its own

            var path = instance.GetPathTo(node).Path;
            if (pristine.GetNodeOrNull(path) is { } counterpart && counterpart.GetType() == node.GetType()
                && TypeRegistry.Get(node.GetType()) is { } info && HasDifferences(node, info, counterpart))
            {
                if (!started)
                {
                    w.WriteStartObject("overrides");
                    started = true;
                }

                _siteOwner = _root.GetPathTo(node).Path;
                WriteProps(w, node, info, counterpart, path);
                if (info.Version != 1)
                    (versions ??= []).Add((path, info.Version));
            }

            for (var i = node.ChildCount - 1; i >= 0; i--)
                stack.Push(node.GetChild(i));
        }

        if (started)
            w.WriteEndObject();

        if (versions is null)
            return;
        w.WriteStartObject("overrideVersions");
        foreach (var (path, version) in versions)
            w.WriteNumber(path, version);
        w.WriteEndObject();
    }

    private static bool HasDifferences(object target, NodeTypeInfo info, object reference)
    {
        foreach (var property in info.Properties)
            if (!property.ValueEquals(target, reference))
                return true;
        return false;
    }

    private void WriteProps(Utf8JsonWriter w, object target, NodeTypeInfo info, object? reference, string propertyName)
    {
        var started = false;
        foreach (var property in info.Properties)
        {
            if (reference is not null && property.ValueEquals(target, reference))
                continue;
            if (!started)
            {
                w.WriteStartObject(propertyName);
                started = true;
            }

            w.WritePropertyName(property.Name);
            _siteMember = property.Name;
            property.Write(w, target, this);
        }

        if (started)
            w.WriteEndObject();
    }

    private static void WriteGroups(Utf8JsonWriter w, Node node, Node? reference)
    {
        var started = false;
        foreach (var group in node.Groups)
        {
            if (!group.Persistent || (reference is not null && reference.IsInGroup(group.Name)))
                continue;
            if (!started)
            {
                w.WriteStartArray("groups");
                started = true;
            }

            w.WriteStringValue(group.Name);
        }

        if (started)
            w.WriteEndArray();
    }

    private void WriteConnections(Utf8JsonWriter w)
    {
        var started = false;
        var stack = new Stack<Node>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var c in node.GetSignalConnections())
            {
                if ((c.Flags & ConnectFlags.Persist) == 0
                    || (c.OriginScene is not null && !ReferenceEquals(c.OriginScene, _root))
                    || !(ReferenceEquals(c.Target, _root) || _root.IsAncestorOf(c.Target)))
                    continue;
                if (!started)
                {
                    w.WriteStartArray("connections");
                    started = true;
                }

                w.WriteStartObject();
                w.WriteString("from", _root.GetPathTo(c.Source).Path);
                w.WriteString("signal", c.Signal);
                w.WriteString("to", _root.GetPathTo(c.Target).Path);
                w.WriteString("method", c.Method);
                var extra = c.Flags & ~ConnectFlags.Persist;
                if (extra != ConnectFlags.None)
                    w.WriteString("flags", extra.ToString());
                w.WriteEndObject();
            }

            for (var i = node.ChildCount - 1; i >= 0; i--)
                stack.Push(node.GetChild(i));
        }

        if (started)
            w.WriteEndArray();
    }

    private static NodeTypeInfo InfoFor(object value) =>
        TypeRegistry.Get(value.GetType())
        ?? throw new InvalidOperationException(
            $"{value.GetType().FullName} is not registered, so it cannot be saved. Reference MainframeEngine.Generators " +
            "as an analyzer in its project and make the type public or internal.");

    private void ReleaseSubScenes()
    {
        foreach (var scene in _loadedSubScenes)
            if (scene.ReferenceCount > 0)
                scene.Release();
        _loadedSubScenes.Clear();
    }
}
