using System.Text.Json;

namespace MainframeEngine.Serialization;

/// <summary>File-level constants of <c>.mscene</c> / <c>.mres</c> files.</summary>
public static class SceneFormat
{
    /// <summary>
    /// Current file format number. Files with a higher number are rejected; older ones still load. Format 2 lists the
    /// nodes flat (<c>"nodes"</c>, each with its <c>"parent"</c> path) and keys inline resources by stable ids
    /// (<c>"StandardMaterial3D_k3x9a"</c>); format 1 nested <c>"children"</c> under <c>"root"</c> and numbered the
    /// resources <c>"1"</c>, <c>"2"</c>… in discovery order (re-keyed on the next save).
    /// </summary>
    public const int Current = 2;

    public const string SceneExtension = ".mscene";
    public const string ResourceExtension = ".mres";

    internal static JsonElement UpgradeFile(JsonElement root, int format, string source)
    {
        if (format > Current)
            throw new InvalidDataException($"'{source}' has format {format}; this engine reads up to {Current}. Update the engine.");
        if (format < 1)
            throw new InvalidDataException($"'{source}' has an invalid format number {format}.");
        // Formats 1 and 2 differ in layout only; SceneDocument.Parse reads both.
        return root;
    }

    /// <summary>
    /// Lays out JSON in the scene file style: two-space indent, one member per line, short arrays of scalars on one
    /// line (<c>[0, 1.5, 0]</c>). Tools that rewrite scene or resource files use it to keep their diffs minimal.
    /// </summary>
    public static byte[] FormatJson(ReadOnlySpan<byte> json) => SceneJsonLayout.Format(json);

    internal static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Scene and resource files are written compact, then laid out by SceneJsonLayout (its "\n" on every OS keeps a
    // save on Windows byte-identical to one on macOS/Linux).
    internal static readonly JsonWriterOptions CompactWriteOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Other committed JSON (project.mfproj): "\n" on every OS (the default is Environment.NewLine).
    internal static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>One node of a parsed scene file (a type entry or a nested scene instance).</summary>
internal sealed class NodeEntry
{
    public required string Name { get; init; }
    public string? Type { get; init; }
    public int Version { get; init; } = 1;
    public string? InstanceUid { get; init; }
    public string? InstancePath { get; init; }
    public JsonElement Props { get; init; }
    public List<(string Path, JsonElement Props, int Version)> Overrides { get; } = [];
    public List<NodeEntry> Children { get; } = [];
    public List<string> Groups { get; } = [];

    /// <summary>For children added under a nested instance: path from the instance root to the parent.</summary>
    public string? ParentPath { get; set; }

    public bool IsInstance { get; init; }
}

internal readonly record struct ConnectionEntry(string From, string Signal, string To, string Method, ConnectFlags Flags);

/// <summary>A parsed <c>.mscene</c> file.</summary>
internal sealed class SceneDocument
{
    public int Format { get; init; } = SceneFormat.Current;
    public string? Uid { get; init; }
    public required Dictionary<string, JsonElement> Resources { get; init; }
    public required NodeEntry Root { get; init; }
    public required List<ConnectionEntry> Connections { get; init; }

    public static SceneDocument Parse(ReadOnlyMemory<byte> json, string source)
    {
        JsonElement root;
        using (var doc = JsonDocument.Parse(json, SceneFormat.ReadOptions))
            root = doc.RootElement.Clone();
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"'{source}' is not a scene file (expected a JSON object).");

        var format = root.TryGetProperty("format", out var f) ? f.GetInt32() : SceneFormat.Current;
        root = SceneFormat.UpgradeFile(root, format, source);

        NodeEntry rootEntry;
        if (root.TryGetProperty("nodes", out var nodes))
            rootEntry = ParseNodeList(nodes, source);
        else if (root.TryGetProperty("root", out var rootNode))
            rootEntry = ParseNode(rootNode, source); // format 1: children nested in their parents
        else
            throw new InvalidDataException($"'{source}' has no \"nodes\".");

        var connections = new List<ConnectionEntry>();
        if (root.TryGetProperty("connections", out var conns))
        {
            foreach (var c in conns.EnumerateArray())
            {
                var flags = ConnectFlags.Persist;
                if (c.TryGetProperty("flags", out var fl))
                    flags |= Enum.Parse<ConnectFlags>(fl.GetString()!);
                connections.Add(new ConnectionEntry(
                    Required(c, "from", source), Required(c, "signal", source),
                    Required(c, "to", source), Required(c, "method", source), flags));
            }
        }

        return new SceneDocument
        {
            Format = format,
            Uid = root.TryGetProperty("uid", out var uid) ? uid.GetString() : null,
            Resources = ParseResourceTable(root, source),
            Root = rootEntry,
            Connections = connections,
        };
    }

    internal static Dictionary<string, JsonElement> ParseResourceTable(JsonElement file, string source)
    {
        var table = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!file.TryGetProperty("resources", out var resources))
            return table;
        if (resources.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"'{source}': \"resources\" must be an object.");
        foreach (var r in resources.EnumerateObject())
            table[r.Name] = r.Value;
        return table;
    }

    /// <summary>
    /// Format 2: the nodes in tree order, parents first. The root has no <c>"parent"</c>; every other node names its
    /// parent by path from the root (<c>"."</c> for the root itself). A parent inside a nested instance (not listed in
    /// the file) becomes <see cref="NodeEntry.ParentPath"/> relative to that instance.
    /// </summary>
    private static NodeEntry ParseNodeList(JsonElement nodes, string source)
    {
        if (nodes.ValueKind != JsonValueKind.Array || nodes.GetArrayLength() == 0)
            throw new InvalidDataException($"'{source}': \"nodes\" must be a non-empty array.");

        NodeEntry? root = null;
        var byPath = new Dictionary<string, NodeEntry>(StringComparer.Ordinal);
        foreach (var e in nodes.EnumerateArray())
        {
            var entry = ParseNode(e, source, nestedChildren: false);
            if (root is null)
            {
                if (e.TryGetProperty("parent", out _))
                    throw new InvalidDataException($"'{source}': the first node is the root; it cannot have a \"parent\".");
                root = entry;
                byPath["."] = entry;
                continue;
            }

            if (!e.TryGetProperty("parent", out var p) || p.GetString() is not { Length: > 0 } parentPath)
                throw new InvalidDataException($"'{source}': node '{entry.Name}' has no \"parent\" (only the first node is the root).");
            if (entry.Name.Length == 0)
                throw new InvalidDataException($"'{source}': a node under '{parentPath}' has no \"name\".");

            Attach(entry, parentPath, byPath, source);
            var path = parentPath == "." ? entry.Name : $"{parentPath}/{entry.Name}";
            if (!byPath.TryAdd(path, entry))
                throw new InvalidDataException($"'{source}': two nodes are named '{path}'.");
        }

        return root!;
    }

    // Adds entry under the node at parentPath: a listed node, or a node inside the nearest listed instance above it.
    private static void Attach(NodeEntry entry, string parentPath, Dictionary<string, NodeEntry> byPath, string source)
    {
        if (byPath.TryGetValue(parentPath, out var parent))
        {
            parent.Children.Add(entry);
            return;
        }

        for (var cut = parentPath.LastIndexOf('/'); cut > 0; cut = parentPath.LastIndexOf('/', cut - 1))
        {
            if (!byPath.TryGetValue(parentPath[..cut], out var ancestor))
                continue;
            if (!ancestor.IsInstance)
                break;
            entry.ParentPath = parentPath[(cut + 1)..];
            ancestor.Children.Add(entry);
            return;
        }

        throw new InvalidDataException($"'{source}': parent '{parentPath}' of '{entry.Name}' is not an earlier node or inside an instance.");
    }

    private static NodeEntry ParseNode(JsonElement e, string source, bool nestedChildren = true)
    {
        if (e.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"'{source}': node entries must be objects.");

        var isInstance = e.TryGetProperty("instance", out var inst);
        var instanceUid = isInstance ? inst.GetString() : null;
        var instancePath = isInstance && e.TryGetProperty("path", out var p) ? p.GetString() : null;
        var type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (isInstance && instanceUid is null && instancePath is null)
            throw new InvalidDataException($"'{source}': an instance needs a UID or a path.");
        if (!isInstance && type is null)
            throw new InvalidDataException($"'{source}': a node needs a \"type\" or an \"instance\".");

        var entry = new NodeEntry
        {
            Name = e.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
            Type = type,
            Version = e.TryGetProperty("v", out var v) ? v.GetInt32() : 1,
            IsInstance = isInstance,
            InstanceUid = instanceUid,
            InstancePath = instancePath,
            Props = e.TryGetProperty("props", out var props) ? props : default,
            ParentPath = nestedChildren && e.TryGetProperty("parent", out var parent) ? parent.GetString() : null,
        };

        if (e.TryGetProperty("overrides", out var overrides))
        {
            var versions = e.TryGetProperty("overrideVersions", out var ov) ? ov : default;
            foreach (var o in overrides.EnumerateObject())
            {
                var version = versions.ValueKind == JsonValueKind.Object && versions.TryGetProperty(o.Name, out var v1) ? v1.GetInt32() : 1;
                entry.Overrides.Add((o.Name, o.Value, version));
            }
        }
        if (e.TryGetProperty("groups", out var groups))
            foreach (var g in groups.EnumerateArray())
                entry.Groups.Add(g.GetString()!);
        if (nestedChildren && e.TryGetProperty("children", out var children))
            foreach (var c in children.EnumerateArray())
                entry.Children.Add(ParseNode(c, source));
        return entry;
    }

    private static string Required(JsonElement e, string name, string source) =>
        e.TryGetProperty(name, out var value) && value.GetString() is { } s
            ? s
            : throw new InvalidDataException($"'{source}': connection is missing \"{name}\".");
}
