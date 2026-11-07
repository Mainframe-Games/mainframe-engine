using System.Globalization;
using System.Numerics;
using MainframeEngine.Trees;

namespace MainframeEngine.Editor;

/// <summary>What <see cref="TreeVariants.Bake"/> wrote (project paths).</summary>
/// <param name="Folder">The variants' folder (<c>Assets/Trees/&lt;stem&gt;_&lt;style&gt;</c>).</param>
/// <param name="TypePath">The <see cref="ScatterObjectType"/> listing the scenes.</param>
/// <param name="Meshes">One baked <see cref="TreeMesh"/> per variant.</param>
/// <param name="Scenes">One tree scene per variant.</param>
/// <param name="Seeds">The variants' seeds.</param>
internal sealed record TreeVariantBake(string Folder, string TypePath, IReadOnlyList<string> Meshes, IReadOnlyList<string> Scenes, IReadOnlyList<int> Seeds);

/// <summary>
/// G8b.7 (ADR 0274): bakes N seeds of a <see cref="Tree3D"/>'s options into meshes, wraps each in a tree scene and lists
/// the scenes in a <see cref="ScatterObjectType"/>, so the Terrain dock's Objects mode scatters varied trees that load
/// without the generator. With a collider the scene's root is a <see cref="StaticBody3D"/> holding the baked tree and its
/// trunk capsule; without one it is the baked tree alone, which does not collide.
/// </summary>
/// <remarks>
/// Files go to <c>Assets/Trees/&lt;stem&gt;_&lt;style&gt;/</c>: <c>&lt;stem&gt;_&lt;style&gt;_v&lt;i&gt;.mfres</c> (mesh),
/// <c>…_v&lt;i&gt;.mfscene</c> (scene) and <c>&lt;stem&gt;_&lt;style&gt;.mfres</c> (the type). A second run overwrites the
/// meshes and scenes (their UIDs stay) and keeps the type's placement settings, replacing only its scenes. Variant
/// <c>i</c> grows from the tree's seed + <c>i</c>, so variant 0 is the tree in the inspector.
/// </remarks>
internal static class TreeVariants
{
    public const int MaxCount = 16;

    /// <summary>The variants' folder: the tree's bake stem and style (<c>Assets/Trees/oak_medium_realistic</c>).</summary>
    public static string Folder(Tree3D tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var bake = Tree3DInspector.BakePath(tree); // Assets/Trees/<stem>_<seed>_<style>.mfres, or the tree's own bake file
        var name = Path.GetFileNameWithoutExtension(bake);
        var style = tree.Style == TreeStyle.LowPoly ? "lowpoly" : "realistic";
        var stem = StemOf(name, style);
        return $"{Tree3DInspector.BakeFolder}/{stem}_{style}";
    }

    /// <summary>Bakes <paramref name="count"/> variants (1–<see cref="MaxCount"/>) of <paramref name="tree"/>. Throws on a tree without options or an unwritable folder.</summary>
    public static TreeVariantBake Bake(Tree3D tree, int count, bool collider)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var options = tree.Options ?? tree.ResolveOptions()
            ?? throw new InvalidOperationException($"Tree '{tree.Name}' has no options or preset to bake.");
        count = Math.Clamp(count, 1, MaxCount);
        var folder = Folder(tree);
        var name = Path.GetFileName(folder);
        var assets = AssetDatabase.Current;
        Directory.CreateDirectory(assets.ToAbsolutePath(folder));

        var baseSeed = tree.ResolveSeed(options);
        var copy = options.Clone();
        var generator = new TreeGenerator();
        var meshes = new string[count];
        var scenes = new string[count];
        var seeds = new int[count];
        var packed = new PackedScene[count];
        var canopy = 0f;
        var trunkRadius = 0f;
        for (var i = 0; i < count; i++)
        {
            var seed = unchecked(baseSeed + i) & int.MaxValue;
            var mesh = TreeMesh.Generate(options, seed, tree.Style, generator);
            mesh.Options = copy;
            var variant = string.Create(CultureInfo.InvariantCulture, $"{name}_v{i}");
            meshes[i] = $"{folder}/{variant}.mfres";
            ResourceSaver.Save(mesh, meshes[i]);

            scenes[i] = $"{folder}/{variant}.mfscene";
            var root = BuildScene(tree, mesh, variant, collider);
            SceneSaver.Save(root, scenes[i]);
            root.Free();
            packed[i] = ResourceLoader.Load<PackedScene>(scenes[i]);
            seeds[i] = seed;

            if (mesh.Lods.Length > 0)
            {
                var bounds = mesh.Lods[0].Bounds;
                canopy = MathF.Max(canopy, MathF.Max(MathF.Max(-bounds.Min.X, bounds.Max.X), MathF.Max(-bounds.Min.Z, bounds.Max.Z)));
            }

            trunkRadius = MathF.Max(trunkRadius, mesh.TrunkRadius);
        }

        var typePath = $"{folder}/{name}.mfres";
        var type = ExistingType(typePath) ?? NewType(name, canopy, trunkRadius);
        type.Scenes = packed;
        ResourceSaver.Save(type, typePath);
        Log.Info($"[Editor] Baked {count} variant(s) of '{tree.Name}' into {folder} ({(collider ? "with" : "without")} trunk colliders).");
        return new TreeVariantBake(folder, typePath, meshes, scenes, seeds);
    }

    /// <summary>
    /// The scene of one variant: a <see cref="StaticBody3D"/> root with the baked tree and its trunk capsule
    /// (<paramref name="collider"/>), or the baked tree as the root with its own collision off. The tree keeps the
    /// inspected tree's level distances, shadows and material overrides.
    /// </summary>
    internal static Node3D BuildScene(Tree3D source, TreeMesh mesh, string name, bool collider)
    {
        var tree = new Tree3D
        {
            Name = collider ? "Tree" : name,
            BakedMesh = mesh,
            Collision = false,
            CastShadows = source.CastShadows,
            Lod1Distance = source.Lod1Distance,
            Lod2Distance = source.Lod2Distance,
            MaxDistance = source.MaxDistance,
            BarkMaterial = source.BarkMaterial,
            LeafMaterial = source.LeafMaterial,
        };
        if (!collider || mesh.TrunkRadius <= 0f || mesh.TrunkHeight <= 0f)
            return tree;

        var height = MathF.Max(mesh.TrunkHeight, 2f * mesh.TrunkRadius);
        var body = new StaticBody3D { Name = name };
        body.AddChild(tree);
        tree.Owner = body;
        var shape = new CollisionShape3D
        {
            Name = "Trunk",
            Shape = new CapsuleShape3D { Radius = mesh.TrunkRadius, Height = height },
            Position = new Vector3(0f, height * 0.5f, 0f),
        };
        body.AddChild(shape);
        shape.Owner = body;
        return body;
    }

    // "oak_medium_321_realistic" → "oak_medium"; a renamed bake file keeps its whole name.
    private static string StemOf(string bakeName, string style)
    {
        var name = bakeName.EndsWith("_" + style, StringComparison.Ordinal) ? bakeName[..^(style.Length + 1)] : bakeName;
        var seedAt = name.LastIndexOf('_');
        if (seedAt > 0 && int.TryParse(name.AsSpan(seedAt + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            name = name[..seedAt];
        return name.Length > 0 ? name : "tree";
    }

    private static ScatterObjectType? ExistingType(string path)
    {
        if (!File.Exists(AssetDatabase.Current.ToAbsolutePath(path))) // disk-io: the editor's own output folder
            return null;
        try
        {
            return ResourceLoader.Load<ScatterObjectType>(path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or InvalidCastException)
        {
            Log.Warning($"[Editor] {path} is not a scatter type ({e.Message}); writing a new one.");
            return null;
        }
    }

    // One tree per cell of about the canopy's width; the trunk sinks by its radius so roots never float on slopes.
    private static ScatterObjectType NewType(string name, float canopyRadius, float trunkRadius) => new()
    {
        Name = name,
        SpacingMeters = MathF.Round(MathF.Max(2f, canopyRadius * 1.5f) * 2f) * 0.5f,
        Jitter = 0.4f,
        RandomYaw = true,
        ScaleMin = 0.85f,
        ScaleMax = 1.15f,
        SinkMeters = MathF.Round(MathF.Max(0.1f, trunkRadius) * 100f) / 100f,
        SlopeMaxDegrees = 35f,
        AvoidWater = true,
    };
}
