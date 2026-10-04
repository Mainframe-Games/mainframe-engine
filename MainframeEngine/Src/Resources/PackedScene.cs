using System.Text.Json;
using MainframeEngine.Serialization;

namespace MainframeEngine;

/// <summary>
/// A scene stored as data (<c>.mscene</c>), as in Godot. <see cref="Instantiate"/> builds a fresh node tree
/// <b>outside</b> the scene tree (no lifecycle callbacks run until the caller adds it): nodes are created by
/// their registered factories, exported properties are applied with generated setters, nested scene instances
/// are instantiated recursively with their overrides, and persisted signal connections are bound.
/// </summary>
/// <remarks>
/// Nodes from the file get <see cref="Node.Owner"/> = the scene root, so saving the tree again writes exactly
/// those nodes. Inline resources are created once per <see cref="PackedScene"/> and shared by all its instances;
/// external ones are loaded once through <see cref="ResourceLoader"/> and released with the scene.
/// </remarks>
public sealed class PackedScene : Resource
{
    [ThreadStatic]
    private static HashSet<PackedScene>? _instantiating;

    private SceneDocument? _document;
    private ResourceTable? _resources;
    private Dictionary<string, PackedScene> _subScenes = new(StringComparer.Ordinal);

    // Tables and sub-scenes of replaced content: nodes instantiated from it may still use them, so their
    // references are only dropped when the scene unloads.
    private readonly List<ResourceTable> _retiredTables = [];
    private readonly List<PackedScene> _retiredSubScenes = [];

    /// <summary>An empty scene (assign content with <see cref="Pack"/> or load one with <see cref="ResourceLoader"/>).</summary>
    public PackedScene()
    {
    }

    // Imported scenes (models): a node tree cloned by Instantiate instead of a parsed document.
    private Node? _template;

    /// <summary>True once the scene has content.</summary>
    public bool CanInstantiate => _document is not null || _template is not null;

    /// <summary>True for scenes produced by an importer (models): instances are clones of an imported node tree.</summary>
    public bool IsImported => _template is not null;

    /// <summary>
    /// A scene whose instances are copies of <paramref name="template"/> (exported properties copied, resources
    /// shared); used by importers. The template must stay outside the scene tree.
    /// </summary>
    internal static PackedScene FromTemplate(Node template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new PackedScene { _template = template };
    }

    /// <summary>The JSON this scene was parsed from.</summary>
    public ReadOnlyMemory<byte> Json { get; private set; }

    /// <summary>Parses scene JSON (the content of a <c>.mscene</c> file).</summary>
    public static PackedScene Parse(ReadOnlyMemory<byte> json, string? sourcePath = null)
    {
        var scene = new PackedScene();
        scene.SetContent(json, sourcePath);
        return scene;
    }

    /// <summary>
    /// Packs the tree rooted at <paramref name="root"/> (its owned nodes) into a new in-memory scene, like
    /// Godot's <c>PackedScene.pack</c>.
    /// </summary>
    public static PackedScene Pack(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Parse(new SceneWriter().WriteScene(root, uid: null));
    }

    /// <summary>Replaces the content (used when the editor re-saves a cached scene).</summary>
    internal void SetContent(ReadOnlyMemory<byte> json, string? sourcePath)
    {
        var document = SceneDocument.Parse(json, sourcePath ?? "<memory>");
        RetireResources();
        _document = document;
        Json = json;
        if (document.Uid is not null)
            Uid = document.Uid;
    }

    /// <summary>
    /// Builds a new instance of the scene. The root's <see cref="Node.SceneFilePath"/> is the scene's file.
    /// </summary>
    public Node Instantiate()
    {
        if (_template is not null && _document is null)
        {
            var copy = ModelImporter.Clone(_template);
            copy.SceneFilePath = ResourcePath;
            return copy;
        }

        var document = _document ?? throw new InvalidOperationException("The PackedScene is empty.");
        var source = ResourcePath ?? "<memory>";

        _instantiating ??= [];
        if (!_instantiating.Add(this))
            throw new InvalidDataException($"'{source}' instances itself (directly or through a nested scene).");

        try
        {
            _resources ??= new ResourceTable(document.Resources, source);
            var owners = new List<(Node Node, Node Owner)>();
            var root = BuildNode(document.Root, sceneRoot: null, source, owners);
            // Owners must be ancestors, so they are assigned once the whole tree is assembled.
            foreach (var (node, owner) in owners)
                node.Owner = owner;
            root.SceneFilePath = ResourcePath;
            BindConnections(root, document, source);
            return root;
        }
        finally
        {
            _instantiating.Remove(this);
        }
    }

    /// <summary>Instantiates and casts the root.</summary>
    public T Instantiate<T>() where T : Node
    {
        var root = Instantiate();
        if (root is T typed)
            return typed;
        root.Free();
        throw new InvalidCastException($"The root of '{ResourcePath ?? "<memory>"}' is a {root.GetType().Name}, not a {typeof(T).Name}.");
    }

    private Node BuildNode(NodeEntry entry, Node? sceneRoot, string source, List<(Node Node, Node Owner)> owners)
    {
        Node node;
        if (entry.IsInstance)
        {
            node = LoadSubScene(entry, source).Instantiate();
            if (entry.Name.Length > 0)
                node.Name = entry.Name;
            var where = $"{source} ({entry.Name})";
            // Instance props and overrides carry the version of the type they were written with ("v",
            // "overrideVersions"), so renamed properties migrate like any other entry.
            if (TypeRegistry.Get(node.GetType()) is { } rootInfo)
                PropertyApplier.Apply(node, rootInfo, entry.Props, entry.Version, _resources!, where);
            foreach (var (path, props, version) in entry.Overrides)
            {
                var target = node.GetNodeOrNull(path);
                if (target is null || TypeRegistry.Get(target.GetType()) is not { } info)
                {
                    Log.Warning($"[Scene] {where}: override target '{path}' not found in the instanced scene; ignored.");
                    continue;
                }

                PropertyApplier.Apply(target, info, props, version, _resources!, $"{where}/{path}");
            }
        }
        else
        {
            var info = TypeRegistry.Get(entry.Type!);
            if (info is { IsNode: true, IsAbstract: false })
            {
                node = (Node)info.CreateInstance();
                PropertyApplier.Apply(node, info, entry.Props, entry.Version, _resources!, $"{source} ({entry.Name})");
            }
            else if (info is null && RemovedNodeTypes.TryUpgrade(entry.Type!, entry.Props, out var upgraded, out var remaining))
            {
                // A type removed from the engine (e.g. M3's Box3d/Quad): load its replacement.
                node = upgraded!;
                PropertyApplier.Apply(node, TypeRegistry.GetRequired(node.GetType()), remaining, _resources!, $"{source} ({entry.Name})");
            }
            else
            {
                Log.Warning($"[Scene] '{source}': unknown node type '{entry.Type}' for '{entry.Name}'; kept as MissingNode.");
                node = new MissingNode(entry.Type!, entry.Version, entry.Props)
                {
                    ResourceReferences = RawProperties.ResolveReferences(entry.Props, _resources!),
                };
            }

            if (entry.Name.Length > 0)
                node.Name = entry.Name;
        }

        foreach (var group in entry.Groups)
            node.AddToGroup(group, persistent: true);

        var owner = sceneRoot ?? node;
        foreach (var childEntry in entry.Children)
        {
            var child = BuildNode(childEntry, owner, source, owners);
            var parent = childEntry.ParentPath is { } parentPath
                ? node.GetNodeOrNull(parentPath)
                  ?? throw new InvalidDataException($"'{source}': parent '{parentPath}' of '{childEntry.Name}' not found in '{entry.Name}'.")
                : node;
            parent.AddChild(child);
            owners.Add((child, owner));
        }

        return node;
    }

    private PackedScene LoadSubScene(NodeEntry entry, string source)
    {
        var key = entry.InstanceUid ?? entry.InstancePath!;
        if (_subScenes.TryGetValue(key, out var scene))
            return scene;
        scene = ResourceLoader.LoadReference(entry.InstanceUid, entry.InstancePath, source) as PackedScene
                ?? throw new InvalidDataException($"'{source}': instance '{key}' is not a scene.");
        _subScenes[key] = scene;
        return scene;
    }

    private static void BindConnections(Node root, SceneDocument document, string source)
    {
        foreach (var c in document.Connections)
        {
            var from = root.GetNodeOrNull(c.From);
            var to = root.GetNodeOrNull(c.To);
            if (from is null || to is null)
            {
                Log.Warning($"[Scene] '{source}': connection {c.From}.{c.Signal} → {c.To}.{c.Method} skipped (node not found).");
                continue;
            }

            try
            {
                from.Connect(c.Signal, to, c.Method, c.Flags | ConnectFlags.Persist).OriginScene = root;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                Log.Warning($"[Scene] '{source}': connection {c.From}.{c.Signal} → {c.To}.{c.Method} skipped: {e.Message}");
            }
        }
    }

    private void RetireResources()
    {
        if (_resources is not null)
            _retiredTables.Add(_resources);
        _resources = null;
        _retiredSubScenes.AddRange(_subScenes.Values);
        _subScenes = new Dictionary<string, PackedScene>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Drops the inline-resource tables (current and retired) holding resources whose type comes from
    /// <paramref name="assembly"/>, releasing their external references, so an unloaded game assembly is not kept
    /// alive by this cached scene. The next instantiation builds a fresh table. Returns how many tables were dropped.
    /// </summary>
    internal int ForgetResourcesOf(System.Reflection.Assembly assembly)
    {
        var dropped = 0;
        if (_resources is not null && _resources.References(assembly))
        {
            _resources.ReleaseExternal();
            _resources = null;
            dropped++;
        }

        for (var i = _retiredTables.Count - 1; i >= 0; i--)
        {
            if (!_retiredTables[i].References(assembly))
                continue;
            _retiredTables[i].ReleaseExternal();
            _retiredTables.RemoveAt(i);
            dropped++;
        }

        return dropped;
    }

    protected internal override void OnUnloaded()
    {
        RetireResources();
        foreach (var table in _retiredTables)
            table.ReleaseExternal();
        _retiredTables.Clear();
        foreach (var sub in _retiredSubScenes)
            if (sub.ReferenceCount > 0)
                sub.Release();
        _retiredSubScenes.Clear();
        base.OnUnloaded();
    }

    /// <summary>The scene's raw JSON as an element (for tools).</summary>
    public JsonElement ToJsonElement()
    {
        using var doc = JsonDocument.Parse(Json);
        return doc.RootElement.Clone();
    }
}
