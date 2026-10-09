using System.Numerics;
using MainframeEngine.Trees;

namespace MainframeEngine;

/// <summary>How <see cref="TreeImpostorBaker"/> bakes a <see cref="TreeImpostor"/>.</summary>
public sealed record TreeImpostorOptions
{
    /// <summary>Views per side of the octahedral grid (8: 64 views).</summary>
    public int Frames { get; init; } = 8;

    /// <summary>Pixels per view (128: a 1024² atlas at 8 frames).</summary>
    public int CellSize { get; init; } = 128;

    /// <summary>Hemi-octahedral (the upper hemisphere; trees are rarely seen from below) or full octahedral.</summary>
    public bool Hemi { get; init; } = true;

    /// <summary>Samples per pixel along each axis.</summary>
    public int Supersample { get; init; } = 2;
}

/// <summary>
/// An octahedral impostor of a tree (ADR 0172; <see href="../../docs/design/procedural-trees.md">procedural-trees.md</see>):
/// the tree's finest level seen from <see cref="Frames"/>² directions of a (hemi-)octahedral grid
/// (<see cref="ImpostorOctahedron"/>), each view a cell of three atlases — albedo with coverage alpha (sRGB,
/// coverage-preserving mips), object-space normal with the depth from the bounding sphere's centre in alpha, and detail
/// at half resolution (R thickness, G ambient occlusion: the vertex AO × depth-based self-occlusion, B 1 on leaves). Drawn as one camera-facing quad per tree
/// (<see cref="ImpostorMaterial3D"/>, <see cref="Mesh"/>) that blends the three nearest views and is lit live like the
/// mesh, so it follows the sun; its shadow caster faces the light.
/// </summary>
public sealed class TreeImpostor
{
    internal TreeImpostor(int frames, int cellSize, bool hemi, Vector3 center, float radius, byte[] albedo, byte[] normal, byte[] detail,
        float cutoff)
    {
        Frames = frames;
        CellSize = cellSize;
        Hemi = hemi;
        Center = center;
        Radius = radius;
        AlbedoPixels = albedo;
        NormalPixels = normal;
        DetailPixels = detail;
        Cutoff = cutoff;
        var size = frames * cellSize;
        Albedo = Texture2D.FromPixels(size, size, albedo, new TextureImportSettings
        {
            ColorSpace = TextureImportColorSpace.Srgb,
            Wrap = TextureWrap.Clamp,
            Anisotropy = 1,
            PreserveAlphaCoverage = true,
            AlphaCoverageCutoff = Math.Clamp(cutoff, 0.01f, 0.99f),
        });
        Normal = Texture2D.FromPixels(size, size, normal, new TextureImportSettings { ColorSpace = TextureImportColorSpace.Linear, Wrap = TextureWrap.Clamp, Anisotropy = 1 });
        Detail = Texture2D.FromPixels(size / 2, size / 2, detail, new TextureImportSettings { ColorSpace = TextureImportColorSpace.Linear, Wrap = TextureWrap.Clamp, Anisotropy = 1 });
        Albedo.ResourceName = "Impostor albedo";
        Normal.ResourceName = "Impostor normal";
        Detail.ResourceName = "Impostor detail";
        Mesh = CreateQuad(center, radius);
    }

    public int Frames { get; }

    public int CellSize { get; }

    public bool Hemi { get; }

    /// <summary>The bounding sphere of the baked mesh (object space): the views' centre and half-size.</summary>
    public Vector3 Center { get; }

    public float Radius { get; }

    /// <summary>The leaves' alpha cutoff the atlas was baked with.</summary>
    public float Cutoff { get; }

    public Texture2D Albedo { get; }

    public Texture2D Normal { get; }

    public Texture2D Detail { get; }

    public byte[] AlbedoPixels { get; }

    public byte[] NormalPixels { get; }

    /// <summary>The detail map's RGBA8 pixels, at half the atlas size.</summary>
    public byte[] DetailPixels { get; }

    /// <summary>
    /// The quad the impostor draws with: four vertices whose UVs name the corners (the vertex shader turns them to the
    /// camera) and whose positions span the bounding sphere's box, so culling and bounds work like the mesh's.
    /// </summary>
    public ArrayMesh Mesh { get; }

    /// <summary>A material drawing this impostor (one per use: its visibility range is per level).</summary>
    public ImpostorMaterial3D CreateMaterial() => new()
    {
        ResourceName = "Tree impostor",
        AlbedoTexture = Albedo,
        NormalTexture = Normal,
        DetailTexture = Detail,
        Frames = Frames,
        Hemi = Hemi,
        Center = Center,
        Radius = Radius,
        AlphaCutoff = Cutoff,
    };

    /// <summary>Writes the atlases as <c>&lt;name&gt;_albedo.png</c>, <c>_normal.png</c>, <c>_detail.png</c> in <paramref name="folder"/>.</summary>
    public void SavePngs(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var size = Frames * CellSize;
        File.WriteAllBytes(Path.Combine(folder, $"{name}_albedo.png"), Png.EncodeRgba8(size, size, AlbedoPixels));
        File.WriteAllBytes(Path.Combine(folder, $"{name}_normal.png"), Png.EncodeRgba8(size, size, NormalPixels));
        File.WriteAllBytes(Path.Combine(folder, $"{name}_detail.png"), Png.EncodeRgba8(size / 2, size / 2, DetailPixels));
    }

    private static ArrayMesh CreateQuad(Vector3 c, float r)
    {
        Vector3[] positions = [c + new Vector3(-r, -r, -r), c + new Vector3(r, -r, r), c + new Vector3(r, r, -r), c + new Vector3(-r, r, r)];
        Vector3[] normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
        Vector2[] uvs = [new(0f, 1f), new(1f, 1f), new(1f, 0f), new(0f, 0f)]; // bottom-left, bottom-right, top-right, top-left
        var mesh = new ArrayMesh { ResourceName = "Tree impostor quad" };
        mesh.AddSurface(positions, normals, uvs, [0, 1, 2, 0, 2, 3]);
        return mesh;
    }
}

/// <summary>
/// Bakes <see cref="TreeImpostor"/>s (ADR 0172) on the CPU (the bake rasterizer of the cluster atlases: orthographic,
/// supersampled, deterministic, headless; the views bake in parallel): the mesh's surfaces with their materials' albedo
/// textures, tints and alpha tests, unlit, without wind.
/// </summary>
public static class TreeImpostorBaker
{
    /// <summary>
    /// Bakes <paramref name="mesh"/> (a tree's finest level: surface 0 bark, 1 leaves, or any surfaces), each surface drawn
    /// with its own material, else <paramref name="bark"/> (surface 0) or <paramref name="leaves"/> (the others).
    /// </summary>
    public static TreeImpostor Bake(Mesh mesh, Material? bark, Material? leaves, TreeImpostorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        options ??= new TreeImpostorOptions();
        var frames = Math.Clamp(options.Frames, 2, 32);
        var cell = Math.Clamp(options.CellSize, 8, 1024);
        var supersample = Math.Clamp(options.Supersample, 1, 4);
        var hemi = options.Hemi;

        // Surfaces and their bake materials (textures decoded once).
        var textures = new Dictionary<Texture2D, BakeTexture>(ReferenceEqualityComparer.Instance);
        var surfaces = new List<(MeshSurface Surface, BakeMaterial Material, float[] Occlusion)>();
        var cutoff = 0.5f;
        var bounds = Aabb.Empty;
        for (var s = 0; s < mesh.SurfaceCount; s++)
        {
            var surface = mesh.GetSurface(s);
            if (surface.IndexCount == 0)
                continue;
            var material = mesh.GetSurfaceMaterial(s) ?? (s == 0 ? bark : leaves);
            var bake = BakeMaterialOf(material, textures, s > 0);
            if (bake.Cutoff > 0f)
                cutoff = bake.Cutoff;
            var occlusion = new float[surface.VertexCount];
            for (var i = 0; i < occlusion.Length; i++)
                occlusion[i] = surface.Custom0.Length == occlusion.Length && surface.Custom0[i].W > 0f ? Math.Clamp(surface.Custom0[i].W, 0f, 1f) : 1f;
            surfaces.Add((surface, bake, occlusion));
            bounds = bounds.Merge(surface.Bounds);
        }

        var center = bounds.IsEmpty ? Vector3.Zero : bounds.Center;
        var radius = 1e-3f;
        foreach (var (surface, _, _) in surfaces)
            foreach (var p in surface.Positions)
                radius = MathF.Max(radius, Vector3.Distance(p, center));
        radius *= 1.01f;

        var size = frames * cell;
        var albedo = new byte[size * size * 4];
        var normal = new byte[size * size * 4];
        var detail = new byte[size * size * 4];
        Parallel.For(0, frames * frames, () => new BakeRasterizer(cell, cell, supersample), (f, _, raster) =>
        {
            int column = f % frames, row = f / frames;
            var direction = ImpostorOctahedron.FrameDirection(column, row, frames, hemi);
            var (right, up) = ImpostorOctahedron.Basis(direction);
            var k = cell * supersample / (2f * radius);
            var half = cell * supersample * 0.5f;
            var toSamples = new Matrix4x4(
                right.X * k, -up.X * k, -direction.X * k, 0f,
                right.Y * k, -up.Y * k, -direction.Y * k, 0f,
                right.Z * k, -up.Z * k, -direction.Z * k, 0f,
                -Vector3.Dot(center, right) * k + half, Vector3.Dot(center, up) * k + half, Vector3.Dot(center, direction) * k, 1f);
            raster.Clear();
            foreach (var (surface, material, occlusion) in surfaces)
                raster.Draw(surface.Positions, surface.Normals, surface.UVs, surface.Indices, toSamples, material, occlusion);
            var pixels = raster.Resolve();

            // View-space normals to object space; depth along the view from the centre, ±radius → 0..1.
            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                if (p.Coverage <= 0f)
                    continue;
                var n = Vector3.Normalize(right * p.Normal.X + up * p.Normal.Y + direction * p.Normal.Z);
                pixels[i] = p with { Normal = n };
            }

            var x0 = column * cell;
            var y0 = row * cell;
            BakeImages.WriteCell(pixels, null, cell, cell, albedo, normal, null, size, x0, y0,
                depth => BakeImages.ToByte(0.5f - 0.5f * depth / (k * radius)));
            // Ambient occlusion: the mesh's vertex AO × the canopy's depth-based self-occlusion (what screen-space AO finds
            // in the mesh's crevices and cannot find on the flat quad).
            var depthAo = BakeImages.DepthOcclusion(pixels, cell, cell, radius: 0.06f * cell, bias: 0.04f * radius * k,
                strength: 0.5f);
            for (var y = 0; y < cell; y++)
            {
                for (var x = 0; x < cell; x++)
                {
                    var p = pixels[y * cell + x];
                    var o = ((y0 + y) * size + x0 + x) * 4;
                    detail[o] = BakeImages.ToByte(p.Coverage > 0f ? p.Thickness : 0f);
                    detail[o + 1] = BakeImages.ToByte(p.Coverage > 0f ? p.Occlusion * depthAo[y * cell + x] : 1f);
                    detail[o + 2] = BakeImages.ToByte(p.Mask);
                    detail[o + 3] = 255;
                }
            }

            BakeImages.Dilate(albedo, [albedo, normal, detail], size, x0, y0, cell, cell);
            return raster;
        }, _ => { });

        return new TreeImpostor(frames, cell, hemi, center, radius, albedo, normal, Half(detail, size), cutoff);
    }

    /// <summary>
    /// <see cref="Bake"/> through the on-disk bake cache (keyed by every input: the surfaces' arrays, the materials and
    /// their textures, the options), so a forest bakes its impostors once per machine.
    /// </summary>
    public static TreeImpostor BakeCached(Mesh mesh, Material? bark, Material? leaves, TreeImpostorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        options ??= new TreeImpostorOptions();
        using var hash = TreeBakeCache.Hasher("impostor");
        TreeBakeCache.Add(hash, $"{options.Frames}|{options.CellSize}|{options.Hemi}|{options.Supersample}|{mesh.SurfaceCount}|");
        for (var s = 0; s < mesh.SurfaceCount; s++)
        {
            var surface = mesh.GetSurface(s);
            TreeBakeCache.Add<System.Numerics.Vector3>(hash, surface.Positions);
            TreeBakeCache.Add<System.Numerics.Vector3>(hash, surface.Normals);
            TreeBakeCache.Add<System.Numerics.Vector2>(hash, surface.UVs);
            TreeBakeCache.Add<System.Numerics.Vector4>(hash, surface.Custom0);
            TreeBakeCache.Add<int>(hash, surface.Indices);
            switch (mesh.GetSurfaceMaterial(s) ?? (s == 0 ? bark : leaves))
            {
                case FoliageMaterial3D f:
                    TreeBakeCache.Add(hash, $"foliage|{f.AlbedoColor.ToArgb()}|{f.AlphaCutout}|{f.AlphaCutoff}|{f.BackFace}|");
                    TreeBakeCache.Add(hash, f.AlbedoTexture);
                    TreeBakeCache.Add(hash, f.ThicknessTexture);
                    break;
                case StandardMaterial3D m:
                    TreeBakeCache.Add(hash, $"standard|{m.AlbedoColor.ToArgb()}|{m.Transparency}|{m.AlphaCutoff}|{m.DoubleSided}|");
                    TreeBakeCache.Add(hash, m.AlbedoTexture);
                    break;
                case { } other:
                    TreeBakeCache.Add(hash, $"other|{other.GetType().Name}|");
                    break;
                default:
                    TreeBakeCache.Add(hash, "none|");
                    break;
            }
        }

        var entry = TreeBakeCache.Name(hash, "impostor");
        if (Load(entry) is { } cached)
            return cached;
        var impostor = Bake(mesh, bark, leaves, options);
        TreeBakeCache.Write(entry, writer =>
        {
            writer.Write(impostor.Frames);
            writer.Write(impostor.CellSize);
            writer.Write(impostor.Hemi);
            writer.Write(impostor.Center.X);
            writer.Write(impostor.Center.Y);
            writer.Write(impostor.Center.Z);
            writer.Write(impostor.Radius);
            writer.Write(impostor.Cutoff);
            TreeBakeCache.WriteBytes(writer, impostor.AlbedoPixels);
            TreeBakeCache.WriteBytes(writer, impostor.NormalPixels);
            TreeBakeCache.WriteBytes(writer, impostor.DetailPixels);
        });
        return impostor;
    }

    private static TreeImpostor? Load(string entry)
    {
        using var reader = TreeBakeCache.TryRead(entry);
        if (reader is null)
            return null;
        try
        {
            int frames = reader.ReadInt32(), cell = reader.ReadInt32();
            var hemi = reader.ReadBoolean();
            var center = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            float radius = reader.ReadSingle(), cutoff = reader.ReadSingle();
            var albedo = TreeBakeCache.ReadBytes(reader);
            var normal = TreeBakeCache.ReadBytes(reader);
            var detail = TreeBakeCache.ReadBytes(reader);
            var size = frames * cell;
            return frames is >= 2 and <= 32 && cell is >= 8 and <= 1024 && albedo.Length == size * size * 4 && normal.Length == albedo.Length &&
                   detail.Length == size / 2 * (size / 2) * 4
                ? new TreeImpostor(frames, cell, hemi, center, radius, albedo, normal, detail, cutoff)
                : null;
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    // The detail map at half resolution (2 × 2 box): it varies slowly, and it is a third of the impostor's memory.
    private static byte[] Half(byte[] rgba, int size)
    {
        var half = size / 2;
        var result = new byte[half * half * 4];
        for (var y = 0; y < half; y++)
        {
            for (var x = 0; x < half; x++)
            {
                for (var c = 0; c < 4; c++)
                {
                    var sum = rgba[((2 * y) * size + 2 * x) * 4 + c] + rgba[((2 * y) * size + 2 * x + 1) * 4 + c] +
                              rgba[((2 * y + 1) * size + 2 * x) * 4 + c] + rgba[((2 * y + 1) * size + 2 * x + 1) * 4 + c];
                    result[(y * half + x) * 4 + c] = (byte)((sum + 2) >> 2);
                }
            }
        }

        return result;
    }

    // A material's look in the bake: albedo texture × colour, its alpha test, two-sided cards, a foliage thickness map; leaves
    // mask 1. Hidden fragments are skipped (no layer count: the thickness comes from the material's map).
    private static BakeMaterial BakeMaterialOf(Material? material, Dictionary<Texture2D, BakeTexture> textures, bool leaves)
    {
        Texture2D? texture = null;
        Texture2D? thickness = null;
        var tint = Vector3.One;
        var cutoff = 0f;
        var twoSided = false;
        switch (material)
        {
            case FoliageMaterial3D f:
                texture = f.AlbedoTexture;
                thickness = f.ThicknessTexture;
                tint = Linear(f.AlbedoColor);
                cutoff = f.AlphaCutout ? f.AlphaCutoff : 0f;
                twoSided = f.BackFace != FoliageBackFace.Cull;
                break;
            case StandardMaterial3D s:
                texture = s.AlbedoTexture;
                tint = Linear(s.AlbedoColor);
                cutoff = s.Transparency == AlphaMode.Cutout ? s.AlphaCutoff : 0f;
                twoSided = s.DoubleSided;
                break;
        }

        return new BakeMaterial(Decode(texture, textures, colour: true), tint, cutoff, twoSided, TrianglesPerGroup: 2,
            Mask: leaves || cutoff > 0f ? 1f : 0f, CountLayers: false, Detail: Decode(thickness, textures, colour: false));
    }

    private static BakeTexture? Decode(Texture2D? texture, Dictionary<Texture2D, BakeTexture> textures, bool colour)
    {
        if (texture is null)
            return null;
        if (textures.TryGetValue(texture, out var bake))
            return bake;
        try
        {
            var srgb = texture.ImportSettings.ResolveColorSpace(colorUsage: colour) == TextureColorSpace.Srgb;
            bake = BakeTexture.From(texture, srgb, texture.ImportSettings.Wrap == TextureWrap.Clamp);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException)
        {
            Log.Warning($"[Trees] The impostor bake cannot read {texture}: {e.Message}");
        }

        textures[texture] = bake!;
        return bake;
    }

    private static Vector3 Linear(System.Drawing.Color c) => ColorSpace.SrgbToLinear(new Vector3(c.R, c.G, c.B) / 255f);
}
