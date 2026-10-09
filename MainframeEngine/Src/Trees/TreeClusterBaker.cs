using System.Numerics;
using MainframeEngine.Trees;

namespace MainframeEngine;

/// <summary>
/// A baked leaf-cluster atlas (ADR 0172): <see cref="Card"/> cells of twig variants as three textures — albedo with
/// coverage alpha (sRGB, coverage-preserving mips at the leaves' cutoff), the card-space normal (the twig's real leaf
/// normals, so a flat card shades like a twig) and thickness (R: <c>1 − 0.5^(layers − 1)</c> of the leaf layers a pixel
/// passes through) with ambient occlusion (G: depth-based self-occlusion). Uncovered texels are dilated.
/// </summary>
public sealed class TreeClusterAtlas
{
    internal TreeClusterAtlas(TreeClusterCard card, int width, int height, byte[] albedo, byte[] normal, byte[] thickness, float cutoff)
    {
        Card = card;
        Width = width;
        Height = height;
        AlbedoPixels = albedo;
        NormalPixels = normal;
        ThicknessPixels = thickness;
        Cutoff = cutoff;
        Albedo = Texture2D.FromPixels(width, height, albedo, new TextureImportSettings
        {
            ColorSpace = TextureImportColorSpace.Srgb,
            Wrap = TextureWrap.Clamp,
            PreserveAlphaCoverage = true,
            AlphaCoverageCutoff = Math.Clamp(cutoff, 0.01f, 0.99f),
        });
        Normal = Texture2D.FromPixels(width, height, normal, new TextureImportSettings { ColorSpace = TextureImportColorSpace.Linear, Wrap = TextureWrap.Clamp });
        Thickness = Texture2D.FromPixels(width, height, thickness, new TextureImportSettings { ColorSpace = TextureImportColorSpace.Linear, Wrap = TextureWrap.Clamp });
        Albedo.ResourceName = "Cluster albedo";
        Normal.ResourceName = "Cluster normal";
        Thickness.ResourceName = "Cluster thickness";
    }

    /// <summary>The cell layout and card size the generator meshes against.</summary>
    public TreeClusterCard Card { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The leaves' alpha cutoff (the albedo's coverage-preserving mips keep its coverage).</summary>
    public float Cutoff { get; }

    public Texture2D Albedo { get; }

    public Texture2D Normal { get; }

    public Texture2D Thickness { get; }

    /// <summary>RGBA8 albedo (sRGB, alpha = coverage).</summary>
    public byte[] AlbedoPixels { get; }

    /// <summary>RGBA8 card-space normal (× 0.5 + 0.5).</summary>
    public byte[] NormalPixels { get; }

    /// <summary>RGBA8 thickness (R) and ambient occlusion (G).</summary>
    public byte[] ThicknessPixels { get; }

    /// <summary>Writes the three maps as <c>&lt;name&gt;_albedo.png</c>, <c>_normal.png</c>, <c>_thick.png</c> in <paramref name="folder"/>.</summary>
    public void SavePngs(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, $"{name}_albedo.png"), Png.EncodeRgba8(Width, Height, AlbedoPixels));
        File.WriteAllBytes(Path.Combine(folder, $"{name}_normal.png"), Png.EncodeRgba8(Width, Height, NormalPixels));
        File.WriteAllBytes(Path.Combine(folder, $"{name}_thick.png"), Png.EncodeRgba8(Width, Height, ThicknessPixels));
    }
}

/// <summary>
/// Bakes leaf-cluster atlases (ADR 0172) from Ez Tree's own generator: <see cref="TwigOptions.Variants"/> twigs
/// (<see cref="TreeGenerator.TwigParams"/>: the tree's last level carrying its leaves) rendered orthographically, face
/// on, into the cells of a <see cref="TreeClusterAtlas"/>, on the CPU (<c>BakeRasterizer</c>: supersampled, deterministic,
/// headless; the variants bake in parallel). Atlases are cached per tree look; the first bake of a species costs about
/// 50–150 ms.
/// </summary>
public static class TreeClusterBaker
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, TreeClusterAtlas> Cache = new(StringComparer.Ordinal);

    /// <summary>The atlas for <paramref name="options"/>' leaves and twig (baked once, then shared).</summary>
    public static TreeClusterAtlas GetOrBake(TreeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var key = CacheKey(options);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
                return cached;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var leaves = TreeMaterials.LeafTexture(options);
        using var hash = TreeBakeCache.Hasher("cluster");
        TreeBakeCache.Add(hash, key);
        TreeBakeCache.Add(hash, leaves);
        var entry = TreeBakeCache.Name(hash, "cluster");
        var atlas = Load(entry);
        if (atlas is null)
        {
            atlas = Bake(options.ToParams(), options.Twig ?? new TwigOptions(), leaves is null ? null : BakeTexture.From(leaves, srgb: true, clamp: true),
                options.LeafAlphaCutoff);
            Save(entry, atlas);
            Log.Debug($"[Trees] Baked the leaf clusters of '{options.ResourceName}' ({atlas.Card.Columns} x {atlas.Card.Rows}) in {watch.Elapsed.TotalMilliseconds:0} ms.");
        }
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var raced))
                return raced;
            Cache.Add(key, atlas);
            return atlas;
        }
    }

    /// <summary>
    /// Bakes the atlas of <paramref name="tree"/>'s twig. <paramref name="leafRgba"/> is the leaf image (sRGB RGBA8,
    /// <paramref name="leafWidth"/> × <paramref name="leafHeight"/>); null draws untextured white cards.
    /// </summary>
    public static TreeClusterAtlas Bake(TreeParams tree, TwigOptions twig, byte[]? leafRgba, int leafWidth, int leafHeight, float cutoff) =>
        Bake(tree, twig, leafRgba is null ? null : new BakeTexture(leafRgba, leafWidth, leafHeight, srgb: true, clamp: true), cutoff);

    internal static TreeClusterAtlas Bake(TreeParams tree, TwigOptions twig, BakeTexture? leafTexture, float cutoff)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(twig);
        var variants = twig.AutoLayout ? Math.Clamp(twig.Variants, 1, 9) : Math.Clamp(twig.Variants, 1, Math.Max(1, twig.Columns) * Math.Max(1, twig.Rows));
        var supersample = Math.Clamp(twig.Supersample, 1, 4);

        // The twigs (leaf cards only: the tree keeps its own branch), and the frame every cell shares.
        var twigs = new TreeSurfaceData[variants];
        Parallel.For(0, variants, v =>
        {
            var parameters = TreeGenerator.TwigParams(tree, twig.LeafSlots, twig.LeafDensity, twig.Seed, v);
            var generator = new TreeGenerator();
            var skeleton = generator.GrowSkeleton(parameters, parameters.Seed);
            twigs[v] = generator.Mesh(skeleton, parameters, new TreeMeshDetail()).Leaves;
        });

        var material = new BakeMaterial(leafTexture, Vector3.One, Math.Clamp(cutoff, 0f, 0.99f), TwoSided: true);

        // The frame: the leaves' quads first, then (a coarse pass) what they actually cover, so the cells hold the twig
        // tightly (leaf images have wide transparent margins). Centred on the stem; y = 0 is its base.
        var (halfWidth, top, bottom) = (1e-3f, 1e-3f, 0f);
        foreach (var leaves in twigs)
        {
            foreach (var p in leaves.Positions)
            {
                halfWidth = MathF.Max(halfWidth, MathF.Abs(p.X));
                top = MathF.Max(top, p.Y);
                bottom = MathF.Min(bottom, p.Y);
            }
        }

        (halfWidth, top, bottom) = CoveredBounds(twigs, material, halfWidth, top, bottom, 128, 128);
        var (columns, rows) = twig.AutoLayout
            ? Layout(variants, twig.AtlasWidth, twig.AtlasHeight, (top - bottom) / (2 * halfWidth))
            : (Math.Max(1, twig.Columns), Math.Max(1, twig.Rows));
        var cellWidth = Math.Max(8, twig.AtlasWidth / columns);
        var cellHeight = Math.Max(8, twig.AtlasHeight / rows);
        var width = cellWidth * columns;
        var height = cellHeight * rows;
        const float margin = 2f; // pixels kept clear around a cell
        var scale = MathF.Min((cellWidth - 2 * margin) / (2 * halfWidth), (cellHeight - 2 * margin) / (top - bottom)); // pixels per unit
        var cardWidth = cellWidth / scale;
        var cardHeight = cellHeight / scale;
        var topUnits = top + (cellHeight / scale - (top - bottom)) * 0.5f; // the twig-space y of the cell's top edge
        var baseV = topUnits / cardHeight; // where y = 0 (the stem's base) lands, 0 = top
        var card = new TreeClusterCard(columns, rows, variants, cardWidth, cardHeight, baseV, Math.Max(1, twig.LeafSlots));

        var albedo = new byte[width * height * 4];
        var normal = new byte[width * height * 4];
        var thickness = new byte[width * height * 4];
        Parallel.For(0, variants, v =>
        {
            var leaves = twigs[v];
            var raster = new BakeRasterizer(cellWidth, cellHeight, supersample);
            var s = scale * supersample;
            raster.Draw(leaves.Positions, leaves.Normals, leaves.UVs, leaves.Indices, TwigToSamples(s, cellWidth * 0.5f * supersample, topUnits), material);
            var pixels = raster.Resolve();
            var ao = BakeImages.DepthOcclusion(pixels, cellWidth, cellHeight, radius: 0.12f * MathF.Min(cellWidth, cellHeight),
                bias: 0.02f * cardHeight * s, strength: 0.6f);
            var x0 = v % columns * cellWidth;
            var y0 = v / columns * cellHeight;
            BakeImages.WriteCell(pixels, ao, cellWidth, cellHeight, albedo, normal, thickness, width, x0, y0);
            BakeImages.Dilate(albedo, [albedo, normal, thickness], width, x0, y0, cellWidth, cellHeight);
        });

        return new TreeClusterAtlas(card, width, height, albedo, normal, thickness, cutoff);
    }

    // The bake cache (TreeBakeCache): the card layout and the three atlases.
    private static TreeClusterAtlas? Load(string entry)
    {
        using var reader = TreeBakeCache.TryRead(entry);
        if (reader is null)
            return null;
        try
        {
            var card = new TreeClusterCard(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadDouble(), reader.ReadDouble(),
                reader.ReadDouble(), reader.ReadInt32());
            int width = reader.ReadInt32(), height = reader.ReadInt32();
            var cutoff = reader.ReadSingle();
            var albedo = TreeBakeCache.ReadBytes(reader);
            var normal = TreeBakeCache.ReadBytes(reader);
            var thickness = TreeBakeCache.ReadBytes(reader);
            var size = width * height * 4;
            return card.IsValid && albedo.Length == size && normal.Length == size && thickness.Length == size
                ? new TreeClusterAtlas(card, width, height, albedo, normal, thickness, cutoff)
                : null;
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    private static void Save(string entry, TreeClusterAtlas atlas) =>
        TreeBakeCache.Write(entry, writer =>
        {
            var c = atlas.Card;
            writer.Write(c.Columns);
            writer.Write(c.Rows);
            writer.Write(c.Variants);
            writer.Write(c.Width);
            writer.Write(c.Height);
            writer.Write(c.BaseV);
            writer.Write(c.LeafSlots);
            writer.Write(atlas.Width);
            writer.Write(atlas.Height);
            writer.Write(atlas.Cutoff);
            TreeBakeCache.WriteBytes(writer, atlas.AlbedoPixels);
            TreeBakeCache.WriteBytes(writer, atlas.NormalPixels);
            TreeBakeCache.WriteBytes(writer, atlas.ThicknessPixels);
        });

    // The grid whose cells best match the twigs' aspect (height / width), among those holding every variant.
    internal static (int Columns, int Rows) Layout(int variants, int atlasWidth, int atlasHeight, float aspect)
    {
        ReadOnlySpan<(int Columns, int Rows)> candidates = [(4, 2), (2, 4), (3, 3), (8, 1), (1, 8), (2, 2), (2, 1), (1, 2), (1, 1)];
        var best = (Columns: 4, Rows: 2);
        var bestScore = float.MaxValue;
        foreach (var (c, r) in candidates)
        {
            if (c * r < variants)
                continue;
            var cellAspect = (float)(atlasHeight / r) / (atlasWidth / c);
            // Fit error, plus a small cost for empty cells (lost resolution).
            var score = MathF.Abs(MathF.Log(cellAspect / MathF.Max(aspect, 1e-3f))) + 0.15f * (c * r - variants);
            if (score < bestScore)
            {
                bestScore = score;
                best = (c, r);
            }
        }

        return best;
    }

    // Twig space → samples: x right from the cell's centre, y down from its top, depth = −z (the camera at +z).
    private static Matrix4x4 TwigToSamples(float s, float centerX, float topUnits) => new(
        s, 0, 0, 0,
        0, -s, 0, 0,
        0, 0, -s, 0,
        centerX, topUnits * s, 0, 1);

    // The twigs' covered extent (half width about the stem, top, bottom; twig units), from a coarse render of each in the
    // frame of their quads' bounds.
    private static (float HalfWidth, float Top, float Bottom) CoveredBounds(TreeSurfaceData[] twigs, BakeMaterial material,
        float halfWidth, float top, float bottom, int width, int height)
    {
        var scale = MathF.Min(width / (2 * halfWidth), height / (top - bottom));
        var topUnits = top;
        float coveredHalf = 0f, coveredTop = float.MinValue, coveredBottom = float.MaxValue;
        var raster = new BakeRasterizer(width, height, 1);
        foreach (var leaves in twigs)
        {
            raster.Clear();
            raster.Draw(leaves.Positions, leaves.Normals, leaves.UVs, leaves.Indices, TwigToSamples(scale, width * 0.5f, topUnits), material);
            var pixels = raster.Resolve();
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (pixels[y * width + x].Coverage <= 0f)
                        continue;
                    coveredHalf = MathF.Max(coveredHalf, (MathF.Abs(x + 0.5f - width * 0.5f) + 1f) / scale);
                    coveredTop = MathF.Max(coveredTop, topUnits - y / scale);
                    coveredBottom = MathF.Min(coveredBottom, topUnits - (y + 1) / scale);
                }
            }
        }

        if (coveredTop <= coveredBottom)
            return (halfWidth, top, bottom); // nothing covered: keep the quads' frame
        return (MathF.Max(coveredHalf, 1e-3f), coveredTop, MathF.Min(coveredBottom, 0f));
    }

    private static string CacheKey(TreeOptions o) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{o.LeafTexture}|{o.Type}|{o.Levels}|{LastLevelKey(o)}|{o.LeafCount}|{o.LeafStart:R}|{o.LeafSize:R}|{o.LeafSizeVariance:R}|{o.LeafAngle:R}|" +
            $"{o.LeafBillboard}|{o.LeafAlphaCutoff:R}|{(o.Twig ?? new TwigOptions()).BakeKey}");

    private static string LastLevelKey(TreeOptions o)
    {
        var p = o.ToParams();
        var l = p.Levels;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.Length[l]:R}|{p.Gnarliness[l]:R}|{p.Twist[l]:R}");
    }
}
