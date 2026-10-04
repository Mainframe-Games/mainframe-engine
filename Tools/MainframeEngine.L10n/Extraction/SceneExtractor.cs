using System.Text.Json;

namespace MainframeEngine.L10n.Extraction;

/// <summary>
/// Extracts <c>[Export(Translatable = true)]</c> string values from scene (<c>.mscene</c>) and resource (<c>.mres</c>)
/// files: node properties, nested-instance properties and overrides (typed through the instanced scene file), and
/// inline resources. Each reference carries the JSON line of the value; the extracted comment names the node path and
/// property for translators.
/// </summary>
internal sealed class SceneExtractor(TranslatablePropertyIndex index, string projectRoot)
{
    private const int MaxInstanceDepth = 16;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, Dictionary<string, string>?> _instanceTypes = new(StringComparer.Ordinal);

    /// <summary>File extensions scanned in directories.</summary>
    public static readonly string[] Extensions = [".mscene", ".mres"];

    /// <summary>Node or resource types found in files that the index does not know (pass <c>--assembly</c>).</summary>
    public SortedSet<string> UnknownTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>Adds the translatable values of one file; returns how many were added.</summary>
    public int Extract(string file, string reference, TemplateBuilder builder)
    {
        var bytes = File.ReadAllBytes(file);
        var lines = JsonLineIndex.Build(bytes);
        using var document = JsonDocument.Parse(bytes, JsonOptions);
        var root = document.RootElement;
        var context = new FileContext(reference, lines, builder);

        if (root.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Object)
        {
            foreach (var resource in resources.EnumerateObject())
                ExtractResource(context, resource.Value, $"resources/{resource.Name}", $"resource {resource.Name}");
        }

        if (root.TryGetProperty("root", out var node) && node.ValueKind == JsonValueKind.Object)
            ExtractNode(context, node, "root", parentPath: null);
        else if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            ExtractProps(context, type.GetString()!, root, "", Path.GetFileName(file)); // a .mres file

        return context.Added;
    }

    private void ExtractResource(FileContext context, JsonElement resource, string jsonPath, string label)
    {
        if (resource.ValueKind == JsonValueKind.Object && resource.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            ExtractProps(context, type.GetString()!, resource, jsonPath, label);
    }

    private void ExtractNode(FileContext context, JsonElement node, string jsonPath, string? parentPath)
    {
        var name = node.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "?";
        var nodePath = parentPath is null ? name : $"{parentPath}/{name}";

        if (node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            ExtractProps(context, type.GetString()!, node, jsonPath, $"node {nodePath}");
        }
        else if (node.TryGetProperty("instance", out _))
        {
            var types = node.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                ? InstanceTypes(p.GetString()!, depth: 0)
                : null;
            if (types is null)
            {
                UnknownTypes.Add($"(instance of {(p.ValueKind == JsonValueKind.String ? p.GetString() : "unknown scene")})");
            }
            else
            {
                if (types.TryGetValue(".", out var rootType))
                    ExtractProps(context, rootType, node, jsonPath, $"node {nodePath}");
                if (node.TryGetProperty("overrides", out var overrides) && overrides.ValueKind == JsonValueKind.Object)
                {
                    foreach (var target in overrides.EnumerateObject())
                    {
                        if (types.TryGetValue(target.Name, out var targetType))
                            ExtractValues(context, targetType, target.Value, $"{jsonPath}/overrides/{target.Name}", $"node {nodePath}/{target.Name}");
                        else
                            UnknownTypes.Add($"(override target {target.Name})");
                    }
                }
            }
        }

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var child in children.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.Object)
                    ExtractNode(context, child, $"{jsonPath}/children/{i}", nodePath);
                i++;
            }
        }
    }

    private void ExtractProps(FileContext context, string type, JsonElement owner, string jsonPath, string label)
    {
        if (!owner.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Object)
            return;
        ExtractValues(context, type, props, $"{jsonPath}/props".TrimStart('/'), label);
    }

    private void ExtractValues(FileContext context, string type, JsonElement props, string jsonPath, string label)
    {
        if (!index.IsKnown(type))
        {
            UnknownTypes.Add(type);
            return;
        }

        foreach (var property in props.EnumerateObject())
        {
            if (!index.IsTranslatable(type, property.Name))
                continue;
            var valuePath = $"{jsonPath}/{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                context.Add(property.Value.GetString()!, valuePath, $"{label} ({type}.{property.Name})");
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        context.Add(item.GetString()!, $"{valuePath}/{i}", $"{label} ({type}.{property.Name}[{i}])");
                    i++;
                }
            }
        }
    }

    /// <summary>
    /// Node path → type for the scene at <paramref name="scenePath"/> (project-relative), including nodes of scenes it
    /// instances, so instance properties and overrides can be typed. Null when the file cannot be read.
    /// </summary>
    private Dictionary<string, string>? InstanceTypes(string scenePath, int depth)
    {
        if (_instanceTypes.TryGetValue(scenePath, out var cached))
            return cached;
        _instanceTypes[scenePath] = null; // cycle guard
        if (depth > MaxInstanceDepth)
            return null;
        var file = Path.Combine(projectRoot, scenePath);
        if (!File.Exists(file))
            return null;

        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file), JsonOptions);
            if (document.RootElement.TryGetProperty("root", out var root))
                CollectTypes(root, ".", types, depth);
        }
        catch (JsonException)
        {
            return null;
        }

        _instanceTypes[scenePath] = types;
        return types;
    }

    private void CollectTypes(JsonElement node, string path, Dictionary<string, string> types, int depth)
    {
        if (node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            types[path] = type.GetString()!;
        }
        else if (node.TryGetProperty("path", out var scene) && scene.ValueKind == JsonValueKind.String
                 && InstanceTypes(scene.GetString()!, depth + 1) is { } inner)
        {
            foreach (var (innerPath, innerType) in inner)
                types[innerPath == "." ? path : path == "." ? innerPath : $"{path}/{innerPath}"] = innerType;
        }

        if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
            return;
        foreach (var child in children.EnumerateArray())
        {
            if (child.ValueKind != JsonValueKind.Object || !child.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                continue;
            var parent = child.TryGetProperty("parent", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : null;
            var basePath = parent is null or "." ? path : path == "." ? parent : $"{path}/{parent}";
            CollectTypes(child, basePath == "." ? name.GetString()! : $"{basePath}/{name.GetString()}", types, depth);
        }
    }

    private sealed class FileContext(string reference, JsonLineIndex lines, TemplateBuilder builder)
    {
        public int Added { get; private set; }

        public void Add(string text, string jsonPath, string comment)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            var line = lines.LineOf(jsonPath);
            builder.Add(null, text, null, line > 0 ? $"{reference}:{line}" : reference, comment);
            Added++;
        }
    }
}

/// <summary>Line numbers of string values in a JSON file, by slash-separated path (<c>root/children/0/props/Title</c>).</summary>
internal sealed class JsonLineIndex
{
    private readonly Dictionary<string, int> _lines = new(StringComparer.Ordinal);

    public int LineOf(string path) => _lines.GetValueOrDefault(path);

    public static JsonLineIndex Build(byte[] utf8)
    {
        var index = new JsonLineIndex();
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        // Each frame: the path of the container, and the next array index (-1 for objects).
        var stack = new Stack<(string Path, int NextIndex)>();
        string? property = null;
        var line = 1;
        long scanned = 0;

        string ValuePath()
        {
            if (stack.Count == 0)
                return string.Empty;
            var (path, next) = stack.Pop();
            string segment;
            if (next >= 0)
            {
                segment = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
                stack.Push((path, next + 1));
            }
            else
            {
                segment = property ?? string.Empty;
                stack.Push((path, next));
            }

            return path.Length == 0 ? segment : $"{path}/{segment}";
        }

        while (reader.Read())
        {
            var start = reader.TokenStartIndex;
            for (; scanned < start; scanned++)
            {
                if (utf8[scanned] == (byte)'\n')
                    line++;
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    property = reader.GetString();
                    break;
                case JsonTokenType.StartObject:
                    stack.Push((stack.Count == 0 ? string.Empty : ValuePath(), -1));
                    break;
                case JsonTokenType.StartArray:
                    stack.Push((ValuePath(), 0));
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    stack.Pop();
                    break;
                case JsonTokenType.String:
                    index._lines[ValuePath()] = line;
                    break;
                default:
                    if (stack.Count > 0)
                        ValuePath(); // numbers, booleans, null: advance array indices
                    break;
            }
        }

        return index;
    }
}
