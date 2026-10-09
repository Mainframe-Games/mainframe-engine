using MainframeEngine.Trees;
using DrawingColor = System.Drawing.Color;

namespace MainframeEngine;

/// <summary>
/// The built-in materials of generated trees (ADR 0158), shared by every <see cref="Tree3D"/> and
/// <see cref="TreeScatter"/> with the same look, so equal trees batch into instanced draws:
/// <list type="bullet">
/// <item><b>Realistic bark:</b> a <see cref="FoliageMaterial3D"/> in <see cref="ShadingMode.Pbr"/>, opaque, back faces
/// culled, with the ambientCG set named by <see cref="TreeOptions.BarkTexture"/> (<c>_Color</c>, <c>_NormalGL</c>, and
/// <c>_Roughness</c> packed into an ORM map by <see cref="OrmPacker"/>), tinted by <see cref="TreeOptions.BarkTint"/>.
/// It sways with the same branch bend as the leaves, so leaves stay on their twigs; the trunk barely moves (the bend
/// grows with the squared wind weight, 0 at the base).</item>
/// <item><b>Realistic leaves:</b> a <see cref="FoliageMaterial3D"/> in PBR, cut out at
/// <see cref="TreeOptions.LeafAlphaCutoff"/>, back faces lit with the flipped normal, translucency 0.5, with Ez Tree's
/// leaf texture (<see cref="TreeOptions.LeafTexture"/>, imported with <c>FixAlphaBorder</c>) tinted by
/// <see cref="TreeOptions.LeafTint"/>.</item>
/// <item><b>LowPoly:</b> flat <see cref="StandardMaterial3D"/>s in PBR, <see cref="TreeOptions.BarkPaletteColor"/> and
/// <see cref="TreeOptions.LeafPaletteColor"/>; the blobs' vertex colours shade the leaves per blob. No wind.</item>
/// </list>
/// Textures load from the engine's <c>Content/Trees/</c> (next to the application); a missing set logs a warning and
/// leaves the material untextured. Main thread.
/// </summary>
public static class TreeMaterials
{
    /// <summary>The engine folder of the bark sets (<c>&lt;name&gt;_1K-JPG/&lt;name&gt;_1K-JPG_Color.jpg</c>, ...).</summary>
    public const string BarkFolder = "Content/Trees/Bark";

    /// <summary>The engine folder of the leaf textures (<c>&lt;name&gt;.png</c>).</summary>
    public const string LeafFolder = "Content/Trees/Leaves";

    /// <summary>Translucency of the Realistic leaves.</summary>
    public const float LeafTranslucency = 0.5f;

    /// <summary>Roughness of the Realistic leaves (dielectric, waxy).</summary>
    public const float LeafRoughness = 0.65f;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<BarkKey, Material> BarkCache = [];
    private static readonly Dictionary<LeafKey, Material> LeafCache = [];
    private static readonly Dictionary<string, Texture2D?> Textures = new(StringComparer.Ordinal);

    /// <summary>The shared bark material for <paramref name="options"/> drawn in <paramref name="style"/>.</summary>
    public static Material Bark(TreeOptions options, TreeStyle style)
    {
        ArgumentNullException.ThrowIfNull(options);
        var key = style == TreeStyle.LowPoly
            ? new BarkKey(style, "", false, 0, options.BarkPaletteColor.ToArgb())
            : new BarkKey(style, options.BarkTexture, options.BarkTextured, options.BarkTint.ToArgb(), 0);
        lock (Gate)
        {
            if (!BarkCache.TryGetValue(key, out var material))
            {
                material = style == TreeStyle.LowPoly ? Palette("Tree bark (low poly)", options.BarkPaletteColor, 0.9f) : RealisticBark(options);
                BarkCache.Add(key, material);
            }

            return material;
        }
    }

    /// <summary>The shared leaf material for <paramref name="options"/> drawn in <paramref name="style"/>.</summary>
    public static Material Leaves(TreeOptions options, TreeStyle style)
    {
        ArgumentNullException.ThrowIfNull(options);
        var key = style == TreeStyle.LowPoly
            ? new LeafKey(style, "", 0, 0f, options.LeafPaletteColor.ToArgb())
            : new LeafKey(style, options.LeafTexture, options.LeafTint.ToArgb(), options.LeafAlphaCutoff, 0);
        lock (Gate)
        {
            if (!LeafCache.TryGetValue(key, out var material))
            {
                material = style == TreeStyle.LowPoly ? Palette("Tree leaves (low poly)", options.LeafPaletteColor, 0.8f) : RealisticLeaves(options);
                LeafCache.Add(key, material);
            }

            return material;
        }
    }

    /// <summary>The engine path (relative to the application) of one map of a bark set: <c>Color</c>, <c>NormalGL</c>, <c>Roughness</c>.</summary>
    public static string BarkMapPath(string set, string map) => $"{BarkFolder}/{set}_1K-JPG/{set}_1K-JPG_{map}.jpg";

    /// <summary>The engine path of a leaf texture.</summary>
    public static string LeafPath(string name) => $"{LeafFolder}/{name}.png";

    private static FoliageMaterial3D RealisticBark(TreeOptions options)
    {
        var material = new FoliageMaterial3D
        {
            ResourceName = $"Tree bark ({options.BarkTexture})",
            ShadingMode = ShadingMode.Pbr,
            AlbedoColor = Opaque(options.BarkTint),
            AlphaCutout = false,
            BackFace = FoliageBackFace.Cull,
            Translucency = 0f,
            Roughness = 1f,
        };
        if (!options.BarkTextured || string.IsNullOrEmpty(options.BarkTexture))
        {
            material.Roughness = 0.9f;
            return material;
        }

        var set = options.BarkTexture;
        material.AlbedoTexture = Load(BarkMapPath(set, "Color"), TextureImportSettings.Default);
        material.NormalTexture = Load(BarkMapPath(set, "NormalGL"), TextureImportSettings.Default);
        material.OrmTexture = PackedOrm(set);
        if (material.OrmTexture is null)
            material.Roughness = 0.9f;
        return material;
    }

    private static FoliageMaterial3D RealisticLeaves(TreeOptions options) => new()
    {
        ResourceName = $"Tree leaves ({options.LeafTexture})",
        ShadingMode = ShadingMode.Pbr,
        AlbedoColor = Opaque(options.LeafTint),
        AlbedoTexture = string.IsNullOrEmpty(options.LeafTexture)
            ? null
            : Load(LeafPath(options.LeafTexture), new TextureImportSettings { FixAlphaBorder = true, Wrap = TextureWrap.Clamp }),
        AlphaCutout = true,
        AlphaCutoff = options.LeafAlphaCutoff,
        BackFace = FoliageBackFace.Flip,
        Translucency = LeafTranslucency,
        Roughness = LeafRoughness,
    };

    private static StandardMaterial3D Palette(string name, DrawingColor color, float roughness) => new()
    {
        ResourceName = name,
        ShadingMode = ShadingMode.Pbr,
        AlbedoColor = Opaque(color),
        Roughness = roughness,
    };

    private static Texture2D? PackedOrm(string set)
    {
        var key = "orm:" + set;
        if (Textures.TryGetValue(key, out var cached))
            return cached;
        Texture2D? orm = null;
        if (Load(BarkMapPath(set, "Roughness"), TextureImportSettings.Default) is { } roughness)
        {
            try
            {
                orm = OrmPacker.Pack(null, roughness, null);
                orm.ResourceName = $"{set} ORM";
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                Log.Warning($"[Trees] Could not pack the ORM map of bark set '{set}': {e.Message}");
            }
        }

        Textures[key] = orm;
        return orm;
    }

    private static Texture2D? Load(string enginePath, TextureImportSettings settings)
    {
        if (Textures.TryGetValue(enginePath, out var cached))
            return cached;
        var path = ContentPaths.Resolve(enginePath, ContentPaths.BaseDirectory);
        Texture2D? texture = null;
        if (File.Exists(path))
            texture = Texture2D.FromFile(path, settings);
        else
            Log.Warning($"[Trees] Missing tree texture '{enginePath}'; the material stays untextured.");
        Textures[enginePath] = texture;
        return texture;
    }

    private static DrawingColor Opaque(DrawingColor c) => DrawingColor.FromArgb(255, c.R, c.G, c.B);

    private readonly record struct BarkKey(TreeStyle Style, string Set, bool Textured, int Tint, int Palette);

    private readonly record struct LeafKey(TreeStyle Style, string Texture, int Tint, float Cutoff, int Palette);
}
