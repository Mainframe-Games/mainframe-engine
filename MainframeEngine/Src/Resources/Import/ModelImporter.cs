using System.Numerics;
using System.Text.Json;
using Silk.NET.Assimp;
using AiMaterial = Silk.NET.Assimp.Material;
using AiMesh = Silk.NET.Assimp.Mesh;
using AiNode = Silk.NET.Assimp.Node;
using AiScene = Silk.NET.Assimp.Scene;
using AiTexture = Silk.NET.Assimp.Texture;
using AiTextureType = Silk.NET.Assimp.TextureType;

namespace MainframeEngine;

/// <summary>
/// Import settings of a model file, stored in its <c>.meta</c> sidecar (<c>"importer": "model"</c>):
/// <code>{ "scale": 1, "generateNormals": true, "importMaterials": true, "optimizeMeshes": false }</code>
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
    };

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
/// colours. Not imported yet: skinning, animation, cameras, lights, PBR metallic/roughness (Blinn-Phong for now).</para>
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
                return _assimp = Assimp.GetApi();
            }
            catch (Exception e) when (e is DllNotFoundException or FileNotFoundException or InvalidOperationException or PlatformNotSupportedException)
            {
                throw new InvalidOperationException($"The Assimp native library could not be loaded ({e.Message}); models cannot be imported.", e);
            }
        }
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

        var api = Api;
        AiScene* scene;
        lock (Gate)
            scene = api.ImportFile(fullPath, (uint)flags);
        if (scene is null || scene->MRootNode is null || (scene->MFlags & (uint)SceneFlags.Incomplete) != 0)
        {
            var error = api.GetErrorStringS();
            if (scene is not null)
                api.ReleaseImport(scene);
            throw new InvalidDataException($"Assimp could not import '{projectPath}': {error}");
        }

        try
        {
            return new Builder(scene, Path.GetDirectoryName(fullPath)!, projectPath, settings).Build(Path.GetFileNameWithoutExtension(fullPath));
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

    private sealed class Builder(AiScene* scene, string directory, string projectPath, ModelImportSettings settings)
    {
        private readonly Dictionary<string, ArrayMesh> _meshes = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, StandardMaterial3D> _materials = [];
        private readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.Ordinal);

        public Node3D Build(string name)
        {
            var root = new Node3D { Name = name };
            if (settings.Scale != 1f)
                root.Scale = new Vector3(settings.Scale);

            var aiRoot = scene->MRootNode;
            // Skip Assimp's synthetic root when it carries nothing.
            if (aiRoot->MNumMeshes == 0 && Matrix4x4.Transpose(aiRoot->MTransformation).IsIdentity)
            {
                for (var i = 0u; i < aiRoot->MNumChildren; i++)
                    root.AddChild(BuildNode(aiRoot->MChildren[i]));
            }
            else
            {
                root.AddChild(BuildNode(aiRoot));
            }

            return root;
        }

        private Node3D BuildNode(AiNode* source)
        {
            Node3D node = source->MNumMeshes > 0 ? new MeshInstance3D { Mesh = MeshFor(source) } : new Node3D();
            var name = source->MName.AsString;
            node.Name = string.IsNullOrWhiteSpace(name) ? (node is MeshInstance3D ? "Mesh" : "Node") : name;

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

            for (var i = 0u; i < source->MNumChildren; i++)
                node.AddChild(BuildNode(source->MChildren[i]));
            return node;
        }

        // Nodes that use the same set of meshes share one ArrayMesh (a surface per Assimp mesh).
        private ArrayMesh MeshFor(AiNode* node)
        {
            var builder = new System.Text.StringBuilder();
            for (var i = 0u; i < node->MNumMeshes; i++)
                builder.Append(node->MMeshes[i]).Append(',');
            var key = builder.ToString();
            if (_meshes.TryGetValue(key, out var existing))
                return existing;

            var mesh = new ArrayMesh { ResourceName = node->MNumMeshes == 1 ? scene->MMeshes[node->MMeshes[0]]->MName.AsString : node->MName.AsString };
            for (var i = 0u; i < node->MNumMeshes; i++)
            {
                var aiMesh = scene->MMeshes[node->MMeshes[i]];
                if (BuildSurface(aiMesh) is { } surface)
                    mesh.AddSurface(surface);
            }

            _meshes[key] = mesh;
            return mesh;
        }

        private MeshSurface? BuildSurface(AiMesh* mesh)
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
            return new MeshSurface(positions, normals, uvs, [.. indices], material);
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
                texture = reference.StartsWith('*') ? Embedded(reference) : External(reference);
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
            AiTexture* texture = scene->MTextures[index];
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
