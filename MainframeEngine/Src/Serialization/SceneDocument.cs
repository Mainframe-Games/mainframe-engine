using System.Text.Json;

namespace MainframeEngine.Serialization;

/// <summary>File-level constants of <c>.mscene</c> / <c>.mres</c> files.</summary>
public static class SceneFormat
{
    /// <summary>
    /// Current file format number. Files with a higher number are rejected; lower numbers are upgraded by
    /// <see cref="UpgradeFile"/> before parsing (format 1 is the first, so there is nothing to upgrade yet).
    /// </summary>
    public const int Current = 1;

    public const string SceneExtension = ".mscene";
    public const string ResourceExtension = ".mres";

    internal static JsonElement UpgradeFile(JsonElement root, int format, string source)
    {
        if (format > Current)
            throw new InvalidDataException($"'{source}' has format {format}; this engine reads up to {Current}. Update the engine.");
        if (format < 1)
            throw new InvalidDataException($"'{source}' has an invalid format number {format}.");
        // Future: `if (format == 1) root = UpgradeFormat1To2(root);` etc.
        return root;
    }

    internal static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    internal static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
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
    public string? ParentPath { get; init; }

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

        if (!root.TryGetProperty("root", out var rootNode))
            throw new InvalidDataException($"'{source}' has no \"root\" node.");

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
            Root = ParseNode(rootNode, source),
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

    private static NodeEntry ParseNode(JsonElement e, string source)
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
            ParentPath = e.TryGetProperty("parent", out var parent) ? parent.GetString() : null,
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
        if (e.TryGetProperty("children", out var children))
            foreach (var c in children.EnumerateArray())
                entry.Children.Add(ParseNode(c, source));
        return entry;
    }

    private static string Required(JsonElement e, string name, string source) =>
        e.TryGetProperty(name, out var value) && value.GetString() is { } s
            ? s
            : throw new InvalidDataException($"'{source}': connection is missing \"{name}\".");
}
