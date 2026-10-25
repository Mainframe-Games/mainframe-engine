using DrawingColor = System.Drawing.Color;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Silk.NET.Assimp;
using AiMaterial = Silk.NET.Assimp.Material;
using AiMesh = Silk.NET.Assimp.Mesh;
using AiNode = Silk.NET.Assimp.Node;
using AiScene = Silk.NET.Assimp.Scene;
using AiTexture = Silk.NET.Assimp.Texture;
using AiTextureType = Silk.NET.Assimp.TextureType;

namespace MainframeEngine;


/// <summary>An event of an imported clip (<see cref="ModelClipSettings.Events"/>): a name raised at a time in seconds.</summary>
public readonly record struct ModelClipEvent(string Name, float Time);

/// <summary>
/// Per-clip import settings (ADR 0208), matched by the clip's name (after Blender's <c>Armature|</c> prefix is removed): its
/// loop mode (glTF has no loop flag), a new name, and its events.
/// </summary>
public sealed record ModelClipSettings
{
    /// <summary>The clip (animation) name in the file.</summary>
    public string Name { get; init; } = "";

    /// <summary>The loop mode; null keeps what the file and the <c>-loop</c> suffix say.</summary>
    public AnimationLoopMode? Loop { get; init; }

    /// <summary>Imports the clip under this name instead (null: its own).</summary>
    public string? Rename { get; init; }

    /// <summary>Events of the clip (null: the file's, from glTF animation extras); an empty list removes them.</summary>
    public IReadOnlyList<ModelClipEvent>? Events { get; init; }

    public bool Equals(ModelClipSettings? other) =>
        other is not null && Name == other.Name && Loop == other.Loop && Rename == other.Rename &&
        (Events is null ? other.Events is null : other.Events is not null && Events.SequenceEqual(other.Events));

    public override int GetHashCode() => HashCode.Combine(Name, Loop, Rename, Events?.Count ?? -1);
}

/// <summary>
/// Import settings of a model file, stored in its <c>.meta</c> sidecar (<c>"importer": "model"</c>):
/// <code>{ "scale": 1, "generateNormals": true, "importMaterials": true, "optimizeMeshes": false, "importAnimations": true,
///   "optimizeAnimations": true, "clips": [ { "name": "run", "loop": "Linear" },
///   { "name": "swing", "events": [ { "name": "HammerFist", "time": 0.42 } ] } ] }</code>
/// </summary>
public sealed record ModelImportSettings
{
    public const string ImporterName = "model";

    public static ModelImportSettings Default { get; } = new();

    /// <summary>Uniform scale applied to the imported root (e.g. 0.01 for centimetre FBX files).</summary>
    public float Scale { get; init; } = 1f;

    /// <summary>Generate smooth normals for meshes that have none.</summary>
    public bool GenerateNormals { get; init; } = true;

    /// <summary>Convert the file's materials and textures; false gives every surface the default material.</summary>
    public bool ImportMaterials { get; init; } = true;

    /// <summary>Let Assimp merge meshes to reduce draw calls (loses per-node mesh identity).</summary>
    public bool OptimizeMeshes { get; init; }

    /// <summary>Import the file's animation clips into an <see cref="AnimationPlayer"/> (ADR 0208).</summary>
    public bool ImportAnimations { get; init; } = true;

    /// <summary>Drop keys that linear interpolation reproduces (within a small tolerance) and channels that never move.</summary>
    public bool OptimizeAnimations { get; init; } = true;

    /// <summary>Per-clip loop modes, names and events.</summary>
    public IReadOnlyList<ModelClipSettings> Clips { get; init; } = [];

    /// <summary>The settings of the clip named <paramref name="name"/>, or null.</summary>
    public ModelClipSettings? FindClip(string name)
    {
        foreach (var clip in Clips)
            if (string.Equals(clip.Name, name, StringComparison.Ordinal))
                return clip;
        return null;
    }

    public bool Equals(ModelImportSettings? other) =>
        other is not null && Scale.Equals(other.Scale) && GenerateNormals == other.GenerateNormals && ImportMaterials == other.ImportMaterials &&
        OptimizeMeshes == other.OptimizeMeshes && ImportAnimations == other.ImportAnimations && OptimizeAnimations == other.OptimizeAnimations &&
        Clips.SequenceEqual(other.Clips);

    public override int GetHashCode() => HashCode.Combine(Scale, GenerateNormals, ImportMaterials, OptimizeMeshes, ImportAnimations, OptimizeAnimations, Clips.Count);

    public static ModelImportSettings FromMeta(AssetMeta? meta, string? where = null)
    {
        var settings = Default;
        if (meta?.Settings is not { } values)
            return settings;

        foreach (var (key, value) in values)
        {
            try
            {
                settings = key switch
                {
                    "scale" => settings with { Scale = value.GetSingle() },
                    "generateNormals" => settings with { GenerateNormals = value.GetBoolean() },
                    "importMaterials" => settings with { ImportMaterials = value.GetBoolean() },
                    "optimizeMeshes" => settings with { OptimizeMeshes = value.GetBoolean() },
                    "importAnimations" => settings with { ImportAnimations = value.GetBoolean() },
                    "optimizeAnimations" => settings with { OptimizeAnimations = value.GetBoolean() },
                    "clips" => settings with { Clips = ReadClips(value, where) },
                    _ => Unknown(settings, key, where),
                };
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException)
            {
                Log.Warning($"[Import] {where ?? "model"}: invalid setting '{key}' ({value}): {e.Message}; using the default.");
            }
        }

        if (!(settings.Scale > 0f) || !float.IsFinite(settings.Scale))
        {
            Log.Warning($"[Import] {where ?? "model"}: scale must be positive; using 1.");
            settings = settings with { Scale = 1f };
        }

        return settings;
    }

    public Dictionary<string, JsonElement> ToMetaSettings() => new(StringComparer.Ordinal)
    {
        ["scale"] = JsonSerializer.SerializeToElement(Scale, AssetJsonContext.Default.Single),
        ["generateNormals"] = JsonSerializer.SerializeToElement(GenerateNormals, AssetJsonContext.Default.Boolean),
        ["importMaterials"] = JsonSerializer.SerializeToElement(ImportMaterials, AssetJsonContext.Default.Boolean),
        ["optimizeMeshes"] = JsonSerializer.SerializeToElement(OptimizeMeshes, AssetJsonContext.Default.Boolean),
        ["importAnimations"] = JsonSerializer.SerializeToElement(ImportAnimations, AssetJsonContext.Default.Boolean),
        ["optimizeAnimations"] = JsonSerializer.SerializeToElement(OptimizeAnimations, AssetJsonContext.Default.Boolean),
        ["clips"] = WriteClips(Clips),
    };

    // [ { "name": "run", "loop": "Linear" | true, "rename": "jog", "events": [ { "name": "Step", "time": 0.25 } ] } ]
    private static List<ModelClipSettings> ReadClips(JsonElement value, string? where)
    {
        var clips = new List<ModelClipSettings>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var name) || name.GetString() is not { Length: > 0 } clipName)
            {
                Log.Warning($"[Import] {where ?? "model"}: a clip setting without a name is ignored.");
                continue;
            }

            var clip = new ModelClipSettings { Name = clipName };
            if (item.TryGetProperty("loop", out var loop))
                clip = clip with { Loop = ParseLoop(loop) };
            if (item.TryGetProperty("rename", out var rename) && rename.GetString() is { Length: > 0 } newName)
                clip = clip with { Rename = newName };
            if (item.TryGetProperty("events", out var events))
                clip = clip with { Events = ParseEvents(events) };
            clips.Add(clip);
        }

        return clips;
    }

    /// <summary>A loop flag: <c>true</c>/<c>false</c> or a <see cref="AnimationLoopMode"/> name (null when unreadable).</summary>
    internal static AnimationLoopMode? ParseLoop(JsonElement loop) => loop.ValueKind switch
    {
        JsonValueKind.True => AnimationLoopMode.Linear,
        JsonValueKind.False => AnimationLoopMode.None,
        JsonValueKind.String when Enum.TryParse<AnimationLoopMode>(loop.GetString(), ignoreCase: true, out var mode) => mode,
        _ => null,
    };

    /// <summary>Events as <c>[ { "name": n, "time": t } ]</c> or <c>{ "name": t }</c> (a time or a list of times), sorted by time.</summary>
    internal static List<ModelClipEvent> ParseEvents(JsonElement events)
    {
        var list = new List<ModelClipEvent>();
        if (events.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in events.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name &&
                    e.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number)
                    list.Add(new ModelClipEvent(name, t.GetSingle()));
        }
        else if (events.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in events.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number)
                    list.Add(new ModelClipEvent(property.Name, property.Value.GetSingle()));
                else if (property.Value.ValueKind == JsonValueKind.Array)
                    foreach (var t in property.Value.EnumerateArray())
                        if (t.ValueKind == JsonValueKind.Number)
                            list.Add(new ModelClipEvent(property.Name, t.GetSingle()));
            }
        }

        list.Sort(static (a, b) => a.Time.CompareTo(b.Time));
        return list;
    }

    private static JsonElement WriteClips(IReadOnlyList<ModelClipSettings> clips)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var clip in clips)
            {
                writer.WriteStartObject();
                writer.WriteString("name", clip.Name);
                if (clip.Loop is { } loop)
                    writer.WriteString("loop", loop.ToString());
                if (clip.Rename is { } rename)
                    writer.WriteString("rename", rename);
                if (clip.Events is { } events)
                {
                    writer.WriteStartArray("events");
                    foreach (var e in events)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", e.Name);
                        writer.WriteNumber("time", e.Time);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static ModelImportSettings Unknown(ModelImportSettings settings, string key, string? where)
    {
        Log.Warning($"[Import] {where ?? "model"}: unknown setting '{key}'; ignored.");
        return settings;
    }
}

/// <summary>
/// Models (glTF/GLB, FBX, OBJ, Collada) → a <see cref="PackedScene"/> of <see cref="Node3D"/>s and
/// <see cref="MeshInstance3D"/>s through Assimp (Silk.NET.Assimp; ADR "Assimp for import"). Each Assimp node becomes
/// a node with its transform; its meshes become one <see cref="ArrayMesh"/> with a surface per mesh (shared when
/// several nodes use the same meshes); materials become <see cref="StandardMaterial3D"/>s (base colour/diffuse,
/// normal and emissive textures, alpha mode and cutoff, double-sided) and textures load through
/// <see cref="ResourceLoader"/> (their own <c>.meta</c> settings apply) or from the file's embedded images.
/// </summary>
/// <remarks>
/// <para>Imports are cached in memory (<see cref="CacheCount"/>) by path, file size, modification time and settings,
/// so instancing a model again, or reloading it after the loader released it, does not re-run Assimp. Each
/// <see cref="PackedScene.Instantiate"/> clones the imported template; meshes, materials and textures are shared.</para>
/// <para>Colours in model files are linear (glTF); they are converted to the engine's sRGB-authored material
/// colours. Not imported yet: cameras, lights, morph targets, PBR metallic/roughness (Blinn-Phong for now).</para>
/// <para>ADR 0208: bones become one <see cref="Skeleton3D"/> (at the armature node), skinned meshes
/// <see cref="MeshInstance3D"/>s under it with a <see cref="Skin"/> and up to four weights per vertex, other nodes under
/// bones <see cref="BoneAttachment3D"/>s, and the clips <see cref="Animation"/>s in an <see cref="AnimationLibrary"/> on an
/// <see cref="AnimationPlayer"/> child of the root (loop modes and events from glTF animation extras and the
/// <c>.meta</c> clips).</para>
/// </remarks>
public sealed unsafe class ModelImporter : IAssetImporter
{
    private const int MaxCacheEntries = 32;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> CacheOrder = new();
    private static Assimp? _assimp;

    private sealed record CacheEntry(long Length, DateTime LastWriteUtc, ModelImportSettings Settings, Node Template);

    public string Name => ModelImportSettings.ImporterName;

    public IReadOnlyList<string> Extensions { get; } = [".gltf", ".glb", ".fbx", ".obj", ".dae"];

    /// <summary>Imports currently cached.</summary>
    public static int CacheCount
    {
        get
        {
            lock (Gate)
                return Cache.Count;
        }
    }

    /// <summary>Assimp imports performed (cache misses) since startup.</summary>
    public static int ImportCount { get; private set; }

    public Resource Import(string fullPath, string projectPath, AssetMeta? meta)
    {
        var settings = ModelImportSettings.FromMeta(meta, projectPath);
        return PackedScene.FromTemplate(GetTemplate(fullPath, projectPath, settings));
    }

    /// <summary>Drops every cached import (the engine does at shutdown with <see cref="ResourceLoader.ClearCache"/>).</summary>
    public static void ClearCache()
    {
        // Templates are never inside a tree (no GPU or native state): dropping them is enough; scenes made from
        // them keep working.
        lock (Gate)
        {
            Cache.Clear();
            CacheOrder.Clear();
        }
    }

    /// <summary>Imports <paramref name="path"/> into a new node tree (no caching), e.g. for tools.</summary>
    public static Node ImportNodes(string path, ModelImportSettings? settings = null)
    {
        var fullPath = AssetDatabase.Current.ToAbsolutePath(path);
        return Clone(GetTemplate(fullPath, AssetDatabase.Current.ToProjectPath(fullPath), settings ?? ModelImportSettings.Default));
    }

    private static Node GetTemplate(string fullPath, string projectPath, ModelImportSettings settings)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException($"Model not found: '{projectPath}'.", fullPath);

        lock (Gate)
        {
            if (Cache.TryGetValue(fullPath, out var cached) && cached.Length == info.Length &&
                cached.LastWriteUtc == info.LastWriteTimeUtc && cached.Settings == settings)
                return cached.Template;
        }

        var template = ImportWithAssimp(fullPath, projectPath, settings);
        ImportCount++;
        lock (Gate)
        {
            if (!Cache.Remove(fullPath))
                CacheOrder.Enqueue(fullPath);
            Cache[fullPath] = new CacheEntry(info.Length, info.LastWriteTimeUtc, settings, template);
            while (Cache.Count > MaxCacheEntries && CacheOrder.TryDequeue(out var oldest))
                Cache.Remove(oldest);
        }

        return template;
    }

    private static Assimp Api
    {
        get
        {
            if (_assimp is not null)
                return _assimp;
            try
            {
                return _assimp = new Assimp(Assimp.CreateDefaultContext(AssimpLibraryNames()));
            }
            catch (Exception e) when (e is DllNotFoundException or FileNotFoundException or InvalidOperationException or PlatformNotSupportedException)
            {
                throw new InvalidOperationException($"The Assimp native library could not be loaded ({e.Message}); models cannot be imported.", e);
            }
        }
    }

    /// <summary>
    /// Silk.NET's library name (as <c>AssimpLibraryNameContainer</c> lists it), then the same file in this RID's
    /// <c>runtimes/&lt;rid&gt;/native</c> folder. Silk finds that folder only for RIDs in its own distro list or .NET's
    /// frozen RID graph, which has no newer distros (Ubuntu 24.04 reports <c>ubuntu.24.04-x64</c>), so it missed the
    /// package's linux-x64 library there.
    /// </summary>
    internal static string[] AssimpLibraryNames()
    {
        var name = OperatingSystem.IsWindows() ? "Assimp64.dll" : OperatingSystem.IsMacOS() ? "libassimp.5.dylib" : "libassimp.so.5";
        return [name, Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", name)];
    }

    private static Node3D ImportWithAssimp(string fullPath, string projectPath, ModelImportSettings settings)
    {
        var flags = PostProcessSteps.Triangulate | PostProcessSteps.JoinIdenticalVertices | PostProcessSteps.SortByPrimitiveType |
                    PostProcessSteps.ImproveCacheLocality | PostProcessSteps.ValidateDataStructure |
                    PostProcessSteps.FlipUVs; // top-left UV origin, as Vulkan samples images
        if (settings.GenerateNormals)
            flags |= PostProcessSteps.GenerateSmoothNormals;
        if (settings.OptimizeMeshes)
            flags |= PostProcessSteps.OptimizeMeshes;
        flags |= PostProcessSteps.LimitBoneWeights; // ADR 0208: at most four influences per vertex (no-op without bones)

        var api = Api;
        AiScene* scene;
        lock (Gate)
        {
            // FBX: no "$AssimpFbx$" pivot helper nodes between bones (they would become bones and break the clip channels).
            var properties = api.CreatePropertyStore();
            api.SetImportPropertyInteger(properties, "IMPORT_FBX_PRESERVE_PIVOTS", 0);
            scene = api.ImportFileExWithProperties(fullPath, (uint)flags, (FileIO*)null, properties);
            api.ReleasePropertyStore(properties);
        }
        if (scene is null || scene->MRootNode is null || (scene->MFlags & (uint)SceneFlags.Incomplete) != 0)
        {
            var error = api.GetErrorStringS();
            if (scene is not null)
                api.ReleaseImport(scene);
            throw new InvalidDataException($"Assimp could not import '{projectPath}': {error}");
        }

        try
        {
            return new Builder(scene, fullPath, Path.GetDirectoryName(fullPath)!, projectPath, settings).Build(Path.GetFileNameWithoutExtension(fullPath));
        }
        finally
        {
            api.ReleaseImport(scene);
        }
    }

    /// <summary>A copy of <paramref name="template"/> (exported properties copied, resources shared), every node owned by the copy's root.</summary>
    internal static Node Clone(Node template)
    {
        var owned = new List<Node>();
        var root = CloneNode(template, owned);
        foreach (var node in owned)
            node.Owner = root;
        return root;
    }

    private static Node CloneNode(Node source, List<Node> owned)
    {
        var info = Serialization.TypeRegistry.GetRequired(source.GetType());
        var copy = (Node)info.CreateInstance();
        foreach (var property in info.Properties)
            property.CopyValue(source, copy);
        copy.Name = source.Name;
        foreach (var child in source.Children)
        {
            var childCopy = CloneNode(child, owned);
            copy.AddChild(childCopy);
            owned.Add(childCopy);
        }

        return copy;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The skeleton is part of the imported template, which the caller owns.")]
    private sealed class Builder(AiScene* scene, string fullPath, string directory, string projectPath, ModelImportSettings settings)
    {
        private readonly Dictionary<string, ArrayMesh> _meshes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (ArrayMesh Mesh, Skin Skin)> _skinnedMeshes = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, StandardMaterial3D> _materials = [];
        private readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.Ordinal);

        // ADR 0208: the skeleton. Bone nodes leave the node tree and become bones of one Skeleton3D, built at the bones'
        // armature node (the parent of their common ancestor); skinned meshes move under it.
        private readonly HashSet<nint> _boneNodes = [];
        private AiNode* _skeletonNode;
        private Skeleton3D? _skeleton;
        private readonly Dictionary<string, int> _bones = new(StringComparer.Ordinal);
        private readonly List<string> _boneNames = [];
        private readonly List<int> _boneParents = [];
        private readonly List<Transform3D> _boneRests = [];
        private readonly List<nint> _skinnedNodes = [];
        private readonly Dictionary<string, Node3D> _nodesByName = new(StringComparer.Ordinal);

        public Node3D Build(string name)
        {
            var root = new Node3D { Name = name };
            if (settings.Scale != 1f)
                root.Scale = new Vector3(settings.Scale);

            FindSkeleton();
            var aiRoot = scene->MRootNode;
            // Skip Assimp's synthetic root when it carries nothing.
            if (aiRoot->MNumMeshes == 0 && Matrix4x4.Transpose(aiRoot->MTransformation).IsIdentity && aiRoot != _skeletonNode &&
                !_boneNodes.Contains((nint)aiRoot))
            {
                for (var i = 0u; i < aiRoot->MNumChildren; i++)
                    if (BuildNode(aiRoot->MChildren[i]) is { } child)
                        root.AddChild(child);
            }
            else if (_boneNodes.Contains((nint)aiRoot))
            {
                // The file's root is a bone: the skeleton is a new node at the model's root.
                _skeleton = new Skeleton3D { Name = "Skeleton3D" };
                root.AddChild(_skeleton);
                AddBone(aiRoot, -1);
                FinishSkeleton();
            }
            else if (BuildNode(aiRoot) is { } child)
            {
                root.AddChild(child);
            }

            AttachSkinnedMeshes();
            if (settings.ImportAnimations && scene->MNumAnimations > 0)
                BuildAnimations(root);
            return root;
        }

        private Node3D? BuildNode(AiNode* source)
        {
            if (source == _skeletonNode)
                return BuildSkeleton(source);

            var skinned = _boneNodes.Count > 0 && IsSkinned(source);
            Node3D node = source->MNumMeshes > 0 && !skinned ? new MeshInstance3D { Mesh = MeshFor(source) } : new Node3D();
            var name = source->MName.AsString;
            node.Name = string.IsNullOrWhiteSpace(name) ? (node is MeshInstance3D ? "Mesh" : "Node") : name;
            ApplyTransform(node, source);
            if (skinned)
                _skinnedNodes.Add((nint)source); // built under the skeleton (AttachSkinnedMeshes)

            for (var i = 0u; i < source->MNumChildren; i++)
                if (BuildNode(source->MChildren[i]) is { } child)
                    node.AddChild(child);
            if (skinned && node.ChildCount == 0)
                return null; // nothing but the skinned mesh, which moved under the skeleton
            _nodesByName.TryAdd(name, node);
            return node;
        }

        private void ApplyTransform(Node3D node, AiNode* source)
        {
            // Assimp matrices are row-major with column vectors; System.Numerics uses row vectors: transpose.
            var local = Matrix4x4.Transpose(source->MTransformation);
            if (Matrix4x4.Decompose(local, out var scale, out var rotation, out var translation))
            {
                node.Position = translation;
                node.Rotation = Quaternion.Normalize(rotation);
                node.Scale = scale;
            }
            else
            {
                Log.Warning($"[Import] '{projectPath}': node '{node.Name}' has a transform that is not translation/rotation/scale; it is ignored.");
            }
        }

        // ── Skeleton (ADR 0208) ───────────────────────────────────────────────

        // Bones: every node a mesh's bones name, the nodes between them and their common ancestor, and mesh-less subtrees
        // under them (end bones). The skeleton is the common ancestor when it is not a bone itself, else its parent.
        private void FindSkeleton()
        {
            var named = new HashSet<string>(StringComparer.Ordinal);
            for (var m = 0u; m < scene->MNumMeshes; m++)
            {
                var mesh = scene->MMeshes[m];
                for (var b = 0u; b < mesh->MNumBones; b++)
                    named.Add(mesh->MBones[b]->MName.AsString);
            }

            if (named.Count == 0)
                return;

            var byName = new Dictionary<string, nint>(StringComparer.Ordinal);
            Index(scene->MRootNode, byName);
            var boneNodes = new List<nint>();
            foreach (var name in named)
            {
                if (byName.TryGetValue(name, out var node))
                    boneNodes.Add(node);
                else
                    Log.Warning($"[Import] '{projectPath}': bone '{name}' has no node; its weights are ignored.");
            }

            if (boneNodes.Count == 0)
                return;

            var ancestor = (AiNode*)boneNodes[0];
            foreach (var node in boneNodes)
                ancestor = CommonAncestor(ancestor, (AiNode*)node);
            foreach (var node in boneNodes)
                for (var n = (AiNode*)node; n is not null && n != ancestor; n = n->MParent)
                    _boneNodes.Add((nint)n);

            if (named.Contains(ancestor->MName.AsString))
            {
                _boneNodes.Add((nint)ancestor);
                _skeletonNode = ancestor->MParent; // null: the root is a bone (Build makes the skeleton)
            }
            else
            {
                _skeletonNode = ancestor;
            }

            foreach (var node in _boneNodes.ToArray())
                AddEndBones((AiNode*)node);
        }

        private static void Index(AiNode* node, Dictionary<string, nint> byName)
        {
            byName.TryAdd(node->MName.AsString, (nint)node);
            for (var i = 0u; i < node->MNumChildren; i++)
                Index(node->MChildren[i], byName);
        }

        private static AiNode* CommonAncestor(AiNode* a, AiNode* b)
        {
            var ancestors = new HashSet<nint>();
            for (var n = a; n is not null; n = n->MParent)
                ancestors.Add((nint)n);
            for (var n = b; n is not null; n = n->MParent)
                if (ancestors.Contains((nint)n))
                    return n;
            return a;
        }

        private void AddEndBones(AiNode* bone)
        {
            for (var i = 0u; i < bone->MNumChildren; i++)
            {
                var child = bone->MChildren[i];
                if (_boneNodes.Contains((nint)child) || HasMeshes(child))
                    continue;
                _boneNodes.Add((nint)child);
                AddEndBones(child);
            }
        }

        private static bool HasMeshes(AiNode* node)
        {
            if (node->MNumMeshes > 0)
                return true;
            for (var i = 0u; i < node->MNumChildren; i++)
                if (HasMeshes(node->MChildren[i]))
                    return true;
            return false;
        }

        private bool IsSkinned(AiNode* node)
        {
            for (var i = 0u; i < node->MNumMeshes; i++)
                if (scene->MMeshes[node->MMeshes[i]]->MNumBones > 0)
                    return true;
            return false;
        }

        private Skeleton3D BuildSkeleton(AiNode* source)
        {
            var skeleton = _skeleton = new Skeleton3D();
            var name = source->MName.AsString;
            skeleton.Name = string.IsNullOrWhiteSpace(name) ? "Skeleton3D" : name;
            ApplyTransform(skeleton, source);
            if (source->MNumMeshes > 0 && !IsSkinned(source))
                skeleton.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = MeshFor(source) });
            else if (source->MNumMeshes > 0)
                _skinnedNodes.Add((nint)source);

            for (var i = 0u; i < source->MNumChildren; i++)
            {
                var child = source->MChildren[i];
                if (_boneNodes.Contains((nint)child))
                    AddBone(child, -1);
                else if (BuildNode(child) is { } node)
                    skeleton.AddChild(node);
            }

            FinishSkeleton();
            _nodesByName.TryAdd(name, skeleton);
            return skeleton;
        }

        // Adds a bone (parents before children), its child bones, and its non-bone children as BoneAttachment3Ds.
        private void AddBone(AiNode* node, int parent)
        {
            var name = node->MName.AsString;
            if (string.IsNullOrEmpty(name) || _bones.ContainsKey(name))
            {
                var unique = $"{(string.IsNullOrEmpty(name) ? "Bone" : name)}_{_boneNames.Count}";
                Log.Warning($"[Import] '{projectPath}': bone name '{name}' is empty or repeated; imported as '{unique}'.");
                name = unique;
            }

            var index = _boneNames.Count;
            _bones[name] = index;
            _boneNames.Add(name);
            _boneParents.Add(parent);
            _boneRests.Add(Transform3D.FromMatrix4x4(Matrix4x4.Transpose(node->MTransformation)));
            for (var i = 0u; i < node->MNumChildren; i++)
            {
                var child = node->MChildren[i];
                if (_boneNodes.Contains((nint)child))
                {
                    AddBone(child, index);
                }
                else if (BuildNode(child) is { } built)
                {
                    var attachment = new BoneAttachment3D { Name = $"{name}Attachment", BoneName = name };
                    attachment.AddChild(built);
                    _skeleton!.AddChild(attachment);
                }
            }
        }

        private void FinishSkeleton()
        {
            _skeleton!.BoneNames = [.. _boneNames];
            _skeleton.BoneParents = [.. _boneParents];
            _skeleton.BoneRests = [.. _boneRests];
        }

        // Each skinned node becomes a MeshInstance3D under the skeleton (identity transform: glTF ignores a skinned
        // mesh node's own transform, and the joint matrices place it), with a Skin of its bones' inverse bind matrices.
        private void AttachSkinnedMeshes()
        {
            foreach (var handle in _skinnedNodes)
            {
                var node = (AiNode*)handle;
                var name = node->MName.AsString;
                if (_skeleton is null || SkinnedMeshFor(node) is not { } skinned)
                {
                    Log.Warning($"[Import] '{projectPath}': skinned mesh '{name}' has no skeleton; imported static.");
                    continue;
                }

                var instance = new MeshInstance3D
                {
                    Name = string.IsNullOrWhiteSpace(name) ? "Mesh" : name,
                    Mesh = skinned.Mesh,
                    Skin = skinned.Skin,
                    Skeleton = "..",
                };
                _skeleton.AddChild(instance);
                _nodesByName.TryAdd(name, instance);
            }
        }

        private (ArrayMesh Mesh, Skin Skin)? SkinnedMeshFor(AiNode* node)
        {
            var key = MeshKey(node);
            if (_skinnedMeshes.TryGetValue(key, out var existing))
                return existing;

            // The union of the meshes' bones is the skin; joint indices index it.
            var joints = new Dictionary<string, int>(StringComparer.Ordinal);
            var skin = new Skin { ResourceName = node->MName.AsString };
            var names = new List<string>();
            var poses = new List<Transform3D>();
            for (var i = 0u; i < node->MNumMeshes; i++)
            {
                var aiMesh = scene->MMeshes[node->MMeshes[i]];
                for (var b = 0u; b < aiMesh->MNumBones; b++)
                {
                    var bone = aiMesh->MBones[b];
                    var boneName = bone->MName.AsString;
                    if (joints.ContainsKey(boneName))
                        continue;
                    joints[boneName] = names.Count;
                    names.Add(boneName);
                    poses.Add(Transform3D.FromMatrix4x4(Matrix4x4.Transpose(bone->MOffsetMatrix)));
                }
            }

            if (names.Count > Skin.MaxJoints)
            {
                Log.Warning($"[Import] '{projectPath}': mesh '{node->MName.AsString}' has {names.Count} bones (at most {Skin.MaxJoints}).");
                return null;
            }

            skin.BindNames = [.. names];
            skin.BindPoses = [.. poses];
            var mesh = new ArrayMesh { ResourceName = node->MNumMeshes == 1 ? scene->MMeshes[node->MMeshes[0]]->MName.AsString : node->MName.AsString };
            for (var i = 0u; i < node->MNumMeshes; i++)
                if (BuildSurface(scene->MMeshes[node->MMeshes[i]], joints) is { } surface)
                    mesh.AddSurface(surface);
            var result = (mesh, skin);
            _skinnedMeshes[key] = result;
            return result;
        }

        // ── Animations (ADR 0208) ─────────────────────────────────────────────

        private void BuildAnimations(Node3D root)
        {
            var extras = GltfAnimationExtras.Read(fullPath, projectPath);
            var library = new AnimationLibrary();
            var skipped = 0;
            for (var i = 0u; i < scene->MNumAnimations; i++)
            {
                var source = scene->MAnimations[i];
                var raw = source->MName.AsString;
                var name = CleanClipName(raw, (int)i, out var loopSuffix);
                var animation = new Animation { ResourceName = name };
                var ticks = source->MTicksPerSecond > 0 ? source->MTicksPerSecond : 25.0;
                var length = source->MDuration / ticks;
                var loop = loopSuffix ? AnimationLoopMode.Linear : AnimationLoopMode.None;
                IReadOnlyList<ModelClipEvent>? events = null;
                if (extras.Get((int)i, raw) is { } extra)
                {
                    loop = extra.Loop ?? loop;
                    events = extra.Events;
                }

                if ((settings.FindClip(name) ?? settings.FindClip(raw)) is { } clip)
                {
                    loop = clip.Loop ?? loop;
                    events = clip.Events ?? events;
                    if (clip.Rename is { } rename)
                        animation.ResourceName = name = rename;
                }

                for (var c = 0u; c < source->MNumChannels; c++)
                {
                    var channel = source->MChannels[c];
                    var nodeName = channel->MNodeName.AsString;
                    NodePath path;
                    string bone;
                    Transform3D rest;
                    if (_skeleton is not null && _bones.TryGetValue(nodeName, out var boneIndex))
                    {
                        path = root.GetPathTo(_skeleton);
                        bone = nodeName;
                        rest = _boneRests[boneIndex];
                    }
                    else if (_nodesByName.TryGetValue(nodeName, out var target) && (ReferenceEquals(target, root) || root.IsAncestorOf(target)))
                    {
                        path = root.GetPathTo(target);
                        bone = "";
                        rest = target.Transform;
                    }
                    else
                    {
                        skipped++;
                        continue;
                    }

                    rest.Decompose(out var restPosition, out var restRotation, out var restScale);
                    length = Math.Max(length, AddChannel(animation, channel, path, bone, ticks, restPosition, restRotation, restScale));
                }

                animation.Length = (float)Math.Max(length, 1e-3);
                animation.LoopMode = loop;
                if (events is not null)
                    foreach (var e in events)
                        animation.AddEvent(e.Name, Math.Clamp(e.Time, 0f, animation.Length));

                if (library.Has(name))
                {
                    var unique = $"{name}_{i}";
                    Log.Warning($"[Import] '{projectPath}': clip name '{name}' is repeated; imported as '{unique}'.");
                    animation.ResourceName = unique;
                }

                library.Animations.Add(animation);
            }

            if (skipped > 0)
                Log.Warning($"[Import] '{projectPath}': {skipped} animation channel(s) target nodes that were not imported; skipped.");
            if (library.Animations.Count == 0)
                return;
            var player = new AnimationPlayer { Name = "AnimationPlayer" };
            player.Libraries.Add(library);
            root.AddChild(player);
        }

        // "Armature|run-loop" → "run" (looping); an empty name → "Animation{index}".
        internal static string CleanClipName(string raw, int index, out bool loop)
        {
            var name = raw;
            var bar = name.LastIndexOf('|');
            if (bar >= 0)
                name = name[(bar + 1)..];
            loop = false;
            foreach (var suffix in (ReadOnlySpan<string>)["-loop", "_loop", "-cycle", "_cycle"])
            {
                if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^suffix.Length];
                    loop = true;
                    break;
                }
            }

            return string.IsNullOrWhiteSpace(name) ? $"Animation{index}" : name;
        }

        // Adds the channel's position, rotation and scale tracks (unless they never leave the rest); returns its last key time.
        private double AddChannel(Animation animation, NodeAnim* channel, NodePath path, string bone, double ticks,
            Vector3 restPosition, Quaternion restRotation, Vector3 restScale)
        {
            var end = 0.0;
            var optimize = settings.OptimizeAnimations;

            var count = (int)channel->MNumPositionKeys;
            if (count > 0)
            {
                var times = new float[count];
                var values = new Vector3[count];
                for (var k = 0; k < count; k++)
                {
                    times[k] = (float)(channel->MPositionKeys[k].MTime / ticks);
                    values[k] = channel->MPositionKeys[k].MValue;
                }

                end = Math.Max(end, times[^1]);
                if (KeyReduction.Reduce(ref times, ref values, restPosition, optimize, 1e-4f) is { } interpolation)
                    animation.Tracks.Add(new Position3DTrack { Path = path, Bone = bone, Times = times, Values = values, Interpolation = interpolation });
            }

            count = (int)channel->MNumRotationKeys;
            if (count > 0)
            {
                var times = new float[count];
                var values = new Quaternion[count];
                for (var k = 0; k < count; k++)
                {
                    times[k] = (float)(channel->MRotationKeys[k].MTime / ticks);
                    var q = channel->MRotationKeys[k].MValue;
                    var value = Quaternion.Normalize(new Quaternion(q.X, q.Y, q.Z, q.W));
                    // Keep consecutive keys in one hemisphere (slerp takes the short way either way; key reduction compares).
                    if (k > 0 && Quaternion.Dot(values[k - 1], value) < 0f)
                        value = Quaternion.Negate(value);
                    values[k] = value;
                }

                end = Math.Max(end, times[^1]);
                if (KeyReduction.Reduce(ref times, ref values, restRotation, optimize, 1e-5f) is { } interpolation)
                    animation.Tracks.Add(new Rotation3DTrack { Path = path, Bone = bone, Times = times, Values = values, Interpolation = interpolation });
            }

            count = (int)channel->MNumScalingKeys;
            if (count > 0)
            {
                var times = new float[count];
                var values = new Vector3[count];
                for (var k = 0; k < count; k++)
                {
                    times[k] = (float)(channel->MScalingKeys[k].MTime / ticks);
                    values[k] = channel->MScalingKeys[k].MValue;
                }

                end = Math.Max(end, times[^1]);
                if (KeyReduction.Reduce(ref times, ref values, restScale, optimize, 1e-4f) is { } interpolation)
                    animation.Tracks.Add(new Scale3DTrack { Path = path, Bone = bone, Times = times, Values = values, Interpolation = interpolation });
            }

            return end;
        }

        private static string MeshKey(AiNode* node)
        {
            var builder = new System.Text.StringBuilder();
            for (var i = 0u; i < node->MNumMeshes; i++)
                builder.Append(node->MMeshes[i]).Append(',');
            return builder.ToString();
        }

        // Nodes that use the same set of meshes share one ArrayMesh (a surface per Assimp mesh).
        private ArrayMesh MeshFor(AiNode* node)
        {
            var key = MeshKey(node);
            if (_meshes.TryGetValue(key, out var existing))
                return existing;

            var mesh = new ArrayMesh { ResourceName = node->MNumMeshes == 1 ? scene->MMeshes[node->MMeshes[0]]->MName.AsString : node->MName.AsString };
            for (var i = 0u; i < node->MNumMeshes; i++)
            {
                var aiMesh = scene->MMeshes[node->MMeshes[i]];
                if (BuildSurface(aiMesh, null) is { } surface)
                    mesh.AddSurface(surface);
            }

            _meshes[key] = mesh;
            return mesh;
        }

        private MeshSurface? BuildSurface(AiMesh* mesh, Dictionary<string, int>? joints)
        {
            const uint triangles = 4; // aiPrimitiveType_TRIANGLE
            if ((mesh->MPrimitiveTypes & triangles) == 0 || mesh->MNumVertices == 0)
                return null;

            var count = (int)mesh->MNumVertices;
            var positions = new Vector3[count];
            new ReadOnlySpan<Vector3>(mesh->MVertices, count).CopyTo(positions);

            var normals = mesh->MNormals is null ? [] : new Vector3[count];
            if (mesh->MNormals is not null)
            {
                new ReadOnlySpan<Vector3>(mesh->MNormals, count).CopyTo(normals);
                for (var i = 0; i < count; i++)
                    normals[i] = normals[i].LengthSquared() > 1e-20f ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
            }

            var uvSource = mesh->MTextureCoords[0];
            var uvs = uvSource is null ? [] : new Vector2[count];
            for (var i = 0; i < uvs.Length; i++)
                uvs[i] = new Vector2(uvSource[i].X, uvSource[i].Y);

            var indices = new List<int>((int)mesh->MNumFaces * 3);
            for (var f = 0u; f < mesh->MNumFaces; f++)
            {
                var face = mesh->MFaces[f];
                if (face.MNumIndices != 3)
                    continue; // points and lines are not drawn
                indices.Add((int)face.MIndices[0]);
                indices.Add((int)face.MIndices[1]);
                indices.Add((int)face.MIndices[2]);
            }

            if (indices.Count == 0)
                return null;
            var material = settings.ImportMaterials ? MaterialFor(mesh->MMaterialIndex) : null;
            var surface = new MeshSurface(positions, normals, uvs, [.. indices], material);
            if (joints is not null && mesh->MNumBones > 0)
                ReadWeights(mesh, joints, surface);
            return surface;
        }

        // The four heaviest influences per vertex (Assimp's LimitBoneWeights keeps four already), normalised.
        private static void ReadWeights(AiMesh* mesh, Dictionary<string, int> joints, MeshSurface surface)
        {
            var count = (int)mesh->MNumVertices;
            var jointIndices = new int[count * 4];
            var weights = new float[count * 4];
            for (var b = 0u; b < mesh->MNumBones; b++)
            {
                var bone = mesh->MBones[b];
                var joint = joints[bone->MName.AsString];
                for (var w = 0u; w < bone->MNumWeights; w++)
                {
                    var influence = bone->MWeights[w];
                    var vertex = (int)influence.MVertexId;
                    if ((uint)vertex >= (uint)count || !(influence.MWeight > 0f))
                        continue;
                    var lightest = 0;
                    for (var k = 1; k < 4; k++)
                        if (weights[vertex * 4 + k] < weights[vertex * 4 + lightest])
                            lightest = k;
                    if (influence.MWeight > weights[vertex * 4 + lightest])
                    {
                        weights[vertex * 4 + lightest] = influence.MWeight;
                        jointIndices[vertex * 4 + lightest] = joint;
                    }
                }
            }

            var vectors = new Vector4[count];
            for (var v = 0; v < count; v++)
            {
                var sum = weights[v * 4] + weights[v * 4 + 1] + weights[v * 4 + 2] + weights[v * 4 + 3];
                vectors[v] = sum > 0f
                    ? new Vector4(weights[v * 4], weights[v * 4 + 1], weights[v * 4 + 2], weights[v * 4 + 3]) / sum
                    : Vector4.Zero;
            }

            surface.Joints = jointIndices;
            surface.Weights = vectors;
        }

        private StandardMaterial3D MaterialFor(uint index)
        {
            if (_materials.TryGetValue(index, out var existing))
                return existing;

            var source = index < scene->MNumMaterials ? scene->MMaterials[index] : null;
            var material = new StandardMaterial3D();
            if (source is not null)
                Convert(source, material);
            _materials[index] = material;
            return material;
        }

        private void Convert(AiMaterial* source, StandardMaterial3D material)
        {
            var api = Api;
            AssimpString name;
            if (api.GetMaterialString(source, "?mat.name", 0, 0, &name) == Return.Success)
                material.ResourceName = name.AsString;

            Vector4 color;
            if (api.GetMaterialColor(source, "$clr.base", 0, 0, &color) == Return.Success ||
                api.GetMaterialColor(source, "$clr.diffuse", 0, 0, &color) == Return.Success)
                material.AlbedoColor = ToSrgbColor(color);

            float value;
            uint max = 1;
            if (api.GetMaterialFloatArray(source, "$mat.opacity", 0, 0, &value, &max) == Return.Success && value < 1f)
                material.AlbedoColor = System.Drawing.Color.FromArgb(ToByte(value), material.AlbedoColor);

            if (api.GetMaterialColor(source, "$clr.emissive", 0, 0, &color) == Return.Success)
                material.EmissionColor = ToSrgbColor(color with { W = 1f });
            max = 1;
            if (api.GetMaterialFloatArray(source, "$mat.emissiveIntensity", 0, 0, &value, &max) == Return.Success)
                material.EmissionEnergy = value;
            max = 1;
            if (api.GetMaterialFloatArray(source, "$mat.shininess", 0, 0, &value, &max) == Return.Success && value > 0f)
                material.Shininess = Math.Clamp(value, 1f, 1024f);

            int twoSided;
            max = 1;
            if (api.GetMaterialIntegerArray(source, "$mat.twosided", 0, 0, &twoSided, &max) == Return.Success)
                material.DoubleSided = twoSided != 0;

            AssimpString alphaMode;
            if (api.GetMaterialString(source, "$mat.gltf.alphaMode", 0, 0, &alphaMode) == Return.Success)
            {
                material.Transparency = alphaMode.AsString switch
                {
                    "MASK" => AlphaMode.Cutout,
                    "BLEND" => AlphaMode.Blend,
                    _ => AlphaMode.Opaque,
                };
            }
            else if (material.AlbedoColor.A < 255)
            {
                material.Transparency = AlphaMode.Blend;
            }

            max = 1;
            if (api.GetMaterialFloatArray(source, "$mat.gltf.alphaCutoff", 0, 0, &value, &max) == Return.Success)
                material.AlphaCutoff = value;

            material.AlbedoTexture = TextureFor(source, AiTextureType.BaseColor) ?? TextureFor(source, AiTextureType.Diffuse);
            material.NormalTexture = TextureFor(source, AiTextureType.Normals);
            material.EmissionTexture = TextureFor(source, AiTextureType.Emissive);
        }

        private Texture2D? TextureFor(AiMaterial* material, AiTextureType type)
        {
            AssimpString path;
            if (Api.GetMaterialTexture(material, type, 0, &path, null, null, null, null, null, null) != Return.Success)
                return null;
            var reference = path.AsString;
            if (string.IsNullOrEmpty(reference))
                return null;
            if (_textures.TryGetValue(reference, out var cached))
                return cached;

            Texture2D? texture = null;
            try
            {
                texture = reference.StartsWith('*') ? Embedded(reference) : EmbeddedByName(reference) ?? External(reference);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                Log.Warning($"[Import] '{projectPath}': texture '{reference}' could not be loaded: {e.Message}");
            }

            _textures[reference] = texture;
            return texture;
        }

        private Texture2D? Embedded(string reference)
        {
            if (!int.TryParse(reference.AsSpan(1), out var index) || (uint)index >= scene->MNumTextures)
                return null;
            return Decode(scene->MTextures[index]);
        }

        // FBX (and some glTF exporters) reference embedded images by file name (aiScene::GetEmbeddedTexture).
        private Texture2D? EmbeddedByName(string reference)
        {
            var name = Path.GetFileName(reference.Replace('\\', '/'));
            for (var i = 0u; i < scene->MNumTextures; i++)
            {
                var embedded = scene->MTextures[i];
                var file = embedded->MFilename.AsString;
                if (file.Length > 0 && string.Equals(Path.GetFileName(file.Replace('\\', '/')), name, StringComparison.OrdinalIgnoreCase))
                    return Decode(embedded);
            }

            return null;
        }

        private static Texture2D Decode(AiTexture* texture)
        {
            if (texture->MHeight == 0)
            {
                // Compressed (PNG/JPEG): MWidth is the byte count.
                var bytes = new ReadOnlySpan<byte>(texture->PcData, (int)texture->MWidth).ToArray();
                return Texture2D.FromEncoded(bytes);
            }

            var width = (int)texture->MWidth;
            var height = (int)texture->MHeight;
            var rgba = new byte[width * height * 4];
            for (var i = 0; i < width * height; i++)
            {
                var texel = texture->PcData[i]; // BGRA
                rgba[i * 4] = texel.R;
                rgba[i * 4 + 1] = texel.G;
                rgba[i * 4 + 2] = texel.B;
                rgba[i * 4 + 3] = texel.A;
            }

            return Texture2D.FromPixels(width, height, rgba);
        }

        // Files next to the model load through the resource loader (cached, with their own .meta settings).
        private Texture2D External(string reference)
        {
            var fullPath = Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(reference.Replace('\\', '/'))));
            if (!System.IO.File.Exists(fullPath))
                throw new FileNotFoundException($"'{fullPath}' does not exist.");
            return ResourceLoader.Load<Texture2D>(AssetDatabase.Current.ToProjectPath(fullPath));
        }

        private static System.Drawing.Color ToSrgbColor(Vector4 linear)
        {
            var srgb = ColorSpace.LinearToSrgb(new Vector3(linear.X, linear.Y, linear.Z));
            return System.Drawing.Color.FromArgb(ToByte(linear.W), ToByte(srgb.X), ToByte(srgb.Y), ToByte(srgb.Z));
        }

        private static int ToByte(float v) => (int)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
    }
}
