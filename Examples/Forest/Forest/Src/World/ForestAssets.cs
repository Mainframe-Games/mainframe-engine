using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>One ambientCG terrain texture set under <c>Content/Art/Terrain/&lt;Name&gt;/</c> (see <see cref="ForestAssets"/>).</summary>
/// <param name="Name">Display name and folder (<c>Grass</c>).</param>
/// <param name="Tag">The layer's <see cref="TerrainLayer.Tag"/>: the footstep surface (<c>grass</c>).</param>
/// <param name="AssetId">The ambientCG asset id (<c>Ground037</c>).</param>
/// <param name="TilingMeters">World metres per repeat (about the photographed patch's size).</param>
/// <param name="HeightBlendContrast">See <see cref="TerrainLayer.HeightBlendContrast"/>.</param>
/// <param name="Tint">Multiplies the albedo (sRGB); null = white.</param>
public sealed record ForestLayerAsset(string Name, string Tag, string AssetId, float TilingMeters, float HeightBlendContrast, System.Drawing.Color? Tint = null)
{
    /// <summary>The set's folder, project-relative.</summary>
    public string Folder => $"{ForestAssets.TerrainFolder}/{Name}";

    /// <summary>Colour, sRGB JPG, 1024².</summary>
    public string AlbedoPath => $"{Folder}/{AssetId}_Color.jpg";

    /// <summary>OpenGL (+Y up) normal map, JPG, 1024².</summary>
    public string NormalPath => $"{Folder}/{AssetId}_NormalGL.jpg";

    /// <summary>Packed occlusion (R), roughness (G), metallic 0 (B), PNG, 1024².</summary>
    public string OrmPath => $"{Folder}/{AssetId}_ORM.png";

    /// <summary>Height for height blending (8-bit grey PNG from ambientCG's displacement), 1024².</summary>
    public string HeightPath => $"{Folder}/{AssetId}_Height.png";

    /// <summary>Every file of the set.</summary>
    public string[] Files => [AlbedoPath, NormalPath, OrmPath, HeightPath];
}

/// <summary>What a prop is, for scattering and footsteps.</summary>
public enum ForestPropKind
{
    Rock,
    Log,
    Stump,
    Debris,
    Plant,
}

/// <summary>One Poly Haven glTF model under <c>Content/Art/Props/&lt;AssetId&gt;/</c> (see <see cref="ForestAssets"/>).</summary>
/// <param name="AssetId">The Poly Haven asset id and folder (<c>boulder_01</c>).</param>
/// <param name="Resolution">The texture set imported (<c>1k</c>, <c>2k</c>).</param>
/// <param name="Kind">Rock, log, stump, debris or plant.</param>
/// <param name="Size">The model's bounds in metres at scale 1 (x, y = up, z), as authored.</param>
/// <param name="Cutout">Alpha-tested foliage (the albedo is a PNG with alpha; drawn double-sided).</param>
public sealed record ForestPropAsset(string AssetId, string Resolution, ForestPropKind Kind, Vector3 Size, bool Cutout = false)
{
    /// <summary>The model's folder, project-relative.</summary>
    public string Folder => $"{ForestAssets.PropsFolder}/{AssetId}";

    /// <summary>The glTF (its <c>.bin</c> and <c>textures/</c> next to it).</summary>
    public string ModelPath => $"{Folder}/{AssetId}_{Resolution}.gltf";

    /// <summary>Colour, sRGB (a PNG with alpha for cut-out foliage).</summary>
    public string AlbedoPath => $"{Folder}/textures/{AssetId}_diff_{Resolution}.{(Cutout ? "png" : "jpg")}";

    /// <summary>OpenGL (+Y up) normal map.</summary>
    public string NormalPath => $"{Folder}/textures/{AssetId}_nor_gl_{Resolution}.jpg";

    /// <summary>Poly Haven's ARM map: occlusion (R), roughness (G), metallic (B), i.e. glTF/Godot ORM packing.</summary>
    public string OrmPath => $"{Folder}/textures/{AssetId}_arm_{Resolution}.jpg";

    /// <summary>The glTF's buffer.</summary>
    public string BufferPath => $"{Folder}/{AssetId}.bin";

    /// <summary>Every file of the model.</summary>
    public string[] Files => [ModelPath, BufferPath, AlbedoPath, NormalPath, OrmPath];
}

/// <summary>
/// The Forest's third-party art (all CC0; <c>Examples/Forest/NOTICE.md</c>), imported by
/// <c>Examples/Forest/Tools/fetch_assets.py</c> into <c>Content/Art</c>: eight ambientCG terrain layers, eleven Poly Haven
/// props and a Poly Haven sky panorama. Paths are project-relative (<c>Content/…</c>, what <see cref="ResourceLoader"/>
/// and scenes use); <see cref="Resolve"/> gives the file on disk. The factories create new resources on every call (the
/// textures themselves are shared through the resource cache), so a caller that builds many instances keeps the result.
/// </summary>
public static class ForestAssets
{
    public const string ArtFolder = "Content/Art";
    public const string TerrainFolder = ArtFolder + "/Terrain";
    public const string PropsFolder = ArtFolder + "/Props";

    /// <summary>
    /// Poly Haven's "Lilienstein" (a sunlit meadow and forest edge, low sun, partly cloudy), its tonemapped JPG at
    /// 4096 × 2048: an optional LDR <see cref="SkyEnvironmentType.Panoramic"/> sky (the physical sky stays the default).
    /// </summary>
    public const string SkyPanorama = ArtFolder + "/Sky/lilienstein_4k.jpg";

    // Terrain layers, in splat-channel order (index = channel: 0–3 in splat-0.png, 4–7 in splat-1.png).
    // Ground037's photo is a bright, yellow-green meadow: tinted towards a shaded forest-edge green.
    public static readonly ForestLayerAsset Grass = new("Grass", "grass", "Ground037", 2.5f, 0.25f, System.Drawing.Color.FromArgb(196, 212, 170));
    public static readonly ForestLayerAsset Leaves = new("Leaves", "leaves", "Ground023", 2.5f, 0.25f);
    public static readonly ForestLayerAsset Moss = new("Moss", "moss", "Moss002", 2f, 0.3f);
    public static readonly ForestLayerAsset Rock = new("Rock", "rock", "Rock063", 6f, 0.2f);
    public static readonly ForestLayerAsset Dirt = new("Dirt", "dirt", "Ground067", 2.5f, 0.15f);
    public static readonly ForestLayerAsset Gravel = new("Gravel", "gravel", "Ground108", 1.5f, 0.1f);
    public static readonly ForestLayerAsset Mud = new("Mud", "mud", "Ground051", 2f, 0.15f);
    public static readonly ForestLayerAsset Needles = new("Needles", "needles", "Ground082S", 2f, 0.25f);

    /// <summary>The eight layers in splat order (<see cref="TerrainSplatMaterial3D.MaxLayers"/>).</summary>
    public static IReadOnlyList<ForestLayerAsset> TerrainLayers { get; } = [Grass, Leaves, Moss, Rock, Dirt, Gravel, Mud, Needles];

    // Props (sizes in metres, y up).
    public static readonly ForestPropAsset MossyRocks01 = new("rock_moss_set_01", "2k", ForestPropKind.Rock, new(8.0f, 1.77f, 6.95f));
    public static readonly ForestPropAsset MossyRocks02 = new("rock_moss_set_02", "2k", ForestPropKind.Rock, new(8.28f, 1.37f, 3.41f));
    public static readonly ForestPropAsset Boulder = new("boulder_01", "2k", ForestPropKind.Rock, new(1.27f, 1.0f, 1.83f));
    public static readonly ForestPropAsset Rock07 = new("rock_07", "1k", ForestPropKind.Rock, new(0.17f, 0.14f, 0.32f));
    public static readonly ForestPropAsset Rock09 = new("rock_09", "1k", ForestPropKind.Rock, new(0.07f, 0.03f, 0.14f));
    public static readonly ForestPropAsset DeadTrunk = new("dead_tree_trunk", "2k", ForestPropKind.Log, new(3.05f, 0.29f, 0.28f));
    public static readonly ForestPropAsset DeadTrunk02 = new("dead_tree_trunk_02", "2k", ForestPropKind.Log, new(4.05f, 1.06f, 1.06f));
    public static readonly ForestPropAsset Stump01 = new("tree_stump_01", "2k", ForestPropKind.Stump, new(1.43f, 0.57f, 1.59f));
    public static readonly ForestPropAsset Stump02 = new("tree_stump_02", "1k", ForestPropKind.Stump, new(1.52f, 0.52f, 1.39f));
    public static readonly ForestPropAsset DryBranches = new("dry_branches_medium_01", "1k", ForestPropKind.Debris, new(1.06f, 0.34f, 1.3f));
    public static readonly ForestPropAsset Fern = new("fern_02", "2k", ForestPropKind.Plant, new(1.97f, 0.43f, 1.72f), Cutout: true);

    /// <summary>Every prop.</summary>
    public static IReadOnlyList<ForestPropAsset> Props { get; } =
        [MossyRocks01, MossyRocks02, Boulder, Rock07, Rock09, DeadTrunk, DeadTrunk02, Stump01, Stump02, DryBranches, Fern];

    /// <summary>The file on disk for a project-relative path (<see cref="ContentPaths.Resolve(string)"/>).</summary>
    public static string Resolve(string path) => ContentPaths.Resolve(path);

    /// <summary>Every committed art file, project-relative (terrain, props, sky).</summary>
    public static IEnumerable<string> AllFiles =>
        TerrainLayers.SelectMany(l => l.Files).Concat(Props.SelectMany(p => p.Files)).Append(SkyPanorama);

    /// <summary>The splat channel of the layer with <paramref name="tag"/>, or -1.</summary>
    public static int LayerIndex(string tag)
    {
        for (var i = 0; i < TerrainLayers.Count; i++)
            if (TerrainLayers[i].Tag == tag)
                return i;
        return -1;
    }

    /// <summary>A <see cref="TerrainLayer"/> with the set's albedo, normal, ORM and height.</summary>
    public static TerrainLayer CreateTerrainLayer(ForestLayerAsset layer) => new()
    {
        Name = layer.Name,
        Tag = layer.Tag,
        Albedo = ResourceLoader.Load<Texture2D>(layer.AlbedoPath),
        Normal = ResourceLoader.Load<Texture2D>(layer.NormalPath),
        Orm = ResourceLoader.Load<Texture2D>(layer.OrmPath),
        Height = ResourceLoader.Load<Texture2D>(layer.HeightPath),
        TilingMeters = layer.TilingMeters,
        HeightBlendContrast = layer.HeightBlendContrast,
        Tint = layer.Tint ?? System.Drawing.Color.White,
    };

    /// <summary>All eight layers in splat order.</summary>
    public static TerrainLayer[] CreateTerrainLayers() => [.. TerrainLayers.Select(CreateTerrainLayer)];

    /// <summary>A <see cref="TerrainSplatMaterial3D"/> with all eight layers (1024² packing, the default).</summary>
    public static TerrainSplatMaterial3D CreateTerrainMaterial() => new() { Layers = CreateTerrainLayers() };

    /// <summary>
    /// The prop's PBR material: albedo, normal map and the ARM map as <see cref="StandardMaterial3D.OrmTexture"/>
    /// (metallic 0, roughness and occlusion from the texture). Rocks, logs and stumps are closed meshes and cull back
    /// faces (the glTFs ask for double-sided); cut-out foliage is alpha-tested and double-sided.
    /// </summary>
    public static StandardMaterial3D CreatePropMaterial(ForestPropAsset prop) => new()
    {
        ResourceName = prop.AssetId,
        ShadingMode = ShadingMode.Pbr,
        AlbedoTexture = ResourceLoader.Load<Texture2D>(prop.AlbedoPath),
        NormalTexture = ResourceLoader.Load<Texture2D>(prop.NormalPath),
        OrmTexture = ResourceLoader.Load<Texture2D>(prop.OrmPath),
        Metallic = 0f,
        Roughness = 1f,
        AmbientOcclusion = 1f,
        Transparency = prop.Cutout ? AlphaMode.Cutout : AlphaMode.Opaque,
        AlphaCutoff = 0.5f,
        DoubleSided = prop.Cutout,
    };

    /// <summary>
    /// The prop's glTF instantiated (its nodes owned by the instance; its root keeps the glTF as
    /// <see cref="Node.SceneFilePath"/>, so a saved scene stores a nested instance) with <paramref name="material"/> (or
    /// a new <see cref="CreatePropMaterial"/>) as every mesh's <see cref="GeometryInstance3D.MaterialOverride"/>: the
    /// importer reads Blinn-Phong colour and normal maps only.
    /// </summary>
    public static Node3D InstantiateProp(ForestPropAsset prop, StandardMaterial3D? material = null)
    {
        material ??= CreatePropMaterial(prop);
        var scene = ResourceLoader.Load<PackedScene>(prop.ModelPath);
        try
        {
            var root = scene.Instantiate<Node3D>();
            root.Name = prop.AssetId;
            ApplyMaterial(root, material);
            return root;
        }
        finally
        {
            scene.Release();
        }
    }

    /// <summary>The meshes of a prop (for <see cref="MultiMesh"/> scattering): each with its transform relative to the model's root.</summary>
    public static List<(ArrayMesh Mesh, Transform3D Transform)> LoadPropMeshes(ForestPropAsset prop)
    {
        var meshes = new List<(ArrayMesh, Transform3D)>();
        var root = InstantiateProp(prop);
        try
        {
            Collect(root, Transform3D.Identity, meshes);
        }
        finally
        {
            root.Free();
        }

        return meshes;
    }

    /// <summary>A panoramic <see cref="Sky"/> of <see cref="SkyPanorama"/>.</summary>
    public static Sky CreatePanoramaSky() => new() { Mode = SkyEnvironmentType.Panoramic, Panorama = SkyPanorama };

    private static void ApplyMaterial(Node node, Material material)
    {
        if (node is MeshInstance3D mesh)
            mesh.MaterialOverride = material;
        foreach (var child in node.Children)
            ApplyMaterial(child, material);
    }

    private static void Collect(Node node, Transform3D parent, List<(ArrayMesh, Transform3D)> meshes)
    {
        var transform = parent;
        if (node is Node3D node3D)
            transform = parent * node3D.Transform;
        if (node is MeshInstance3D { Mesh: ArrayMesh mesh })
            meshes.Add((mesh, transform));
        foreach (var child in node.Children)
            Collect(child, transform, meshes);
    }
}
