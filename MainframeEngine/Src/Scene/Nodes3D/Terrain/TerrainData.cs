using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A terrain's settings and layers (G8a): a square height grid with origin at the map corner, and per-cell or
/// per-vertex image layers. The resource (<c>terrain.mres</c>) holds only the knobs; the pixels are images in its
/// folder, written on scene save (<see cref="SaveLayers"/>) and read on first use (<see cref="EnsureLoaded"/>):
/// <list type="table">
/// <item><term><c>heightmap.png</c></term><description>16-bit grey, (N+1)²: <c>h = HeightMin + v / 65535 · (HeightMax − HeightMin)</c></description></item>
/// <item><term><c>splat-0.png</c>, <c>splat-1.png</c></term><description>Realistic: RGBA8 per cell, weights of layers 0–3 and 4–7 (sum 255)</description></item>
/// <item><term><c>surface.png</c></term><description>Faceted: RGBA8 per cell, R = surface id</description></item>
/// <item><term><c>user.png</c></term><description>RGBA8 per cell: four game-defined channels</description></item>
/// <item><term><c>water.png</c></term><description>RGBA8 per vertex, R = water depth as a fraction of <see cref="MaxWaterDepth"/></description></item>
/// </list>
/// A missing file loads as its default, so code-made data needs no files at all (<see cref="Create"/>).
/// </summary>
/// <remarks>
/// <see cref="Profile"/>, <see cref="SizeMeters"/>, <see cref="VertexSpacing"/>, <see cref="ChunkMeters"/>, the height
/// range and the diagonal are fixed once the layers are loaded: import a resampled heightmap into new data to change
/// them. Heights are quantised to the 16-bit step as they are written, so saving and loading gives back the same floats.
/// Edit through a <see cref="Terrain3D"/> (or these methods: terrains using the data rebuild the touched chunks).
/// </remarks>
[EditorIcon("mountain")]
public sealed class TerrainData : Resource
{
    /// <summary>The layer folder's resource file name.</summary>
    public const string ResourceFileName = "terrain.mres";

    public const string HeightmapFile = "heightmap.png";
    public const string SurfaceFile = "surface.png";
    public const string UserFile = "user.png";
    public const string WaterFile = "water.png";

    /// <summary>Realistic splat layers (two RGBA8 weight maps).</summary>
    public const int MaxLayers = 8;

    /// <summary>Largest vertex count per side (2049 × 2049 vertices).</summary>
    public const int MaxVerticesPerSide = 2049;

    private float[]? _heights;   // quantised heights (the water surface where there is water)
    private float[]? _bed;       // heights − water depth: what is drawn, collided and queried
    private byte[]? _water;      // RGBA8 per vertex
    private byte[]? _splat0;     // Realistic, RGBA8 per cell
    private byte[]? _splat1;
    private byte[]? _surface;    // Faceted, RGBA8 per cell
    private byte[]? _user;       // RGBA8 per cell
    private float[] _chunkMin = [];
    private float[] _chunkMax = [];
    private readonly Texture2D?[] _splatTextures = new Texture2D?[2];
    private LayerFiles _dirty;
    private string? _savedFolder;
    private int _version;
    private TerrainGrid _grid;

    [Flags]
    private enum LayerFiles : byte
    {
        None = 0,
        Heightmap = 1,
        Splat0 = 2,
        Splat1 = 4,
        Surface = 8,
        User = 16,
        Water = 32,
        All = 63,
    }

    /// <summary>
    /// A new, empty terrain of <paramref name="sizeMeters"/> at <paramref name="vertexSpacing"/> with the profile's
    /// defaults (Realistic: 64-quad chunks, heights −64..192 m; Faceted: 16-quad chunks, −16..48 m).
    /// <paramref name="chunkMeters"/> 0 picks the default. Usable at once: no folder, no files.
    /// </summary>
    public static TerrainData Create(TerrainProfile profile, int sizeMeters, float vertexSpacing, int chunkMeters = 0)
    {
        var data = new TerrainData
        {
            Profile = profile,
            SizeMeters = sizeMeters,
            VertexSpacing = vertexSpacing,
            ChunkMeters = chunkMeters > 0 ? chunkMeters : (int)MathF.Round((profile == TerrainProfile.Realistic ? 64 : 16) * vertexSpacing),
            HeightMin = profile == TerrainProfile.Realistic ? -64f : -16f,
            HeightMax = profile == TerrainProfile.Realistic ? 192f : 48f,
        };
        data.Validate();
        data.EnsureLoaded();
        return data;
    }

    // ── Knobs ──────────────────────────────────────────────────────────────────

    /// <summary>Faceted or Realistic (fixed once created).</summary>
    [Export]
    public TerrainProfile Profile { get; set { ThrowIfLoaded(); field = value; } } = TerrainProfile.Realistic;

    /// <summary>Side length in metres (Realistic ≤ 2048, Faceted ≤ 1024).</summary>
    [Export(Range = "16,2048,1")]
    public int SizeMeters { get; set { ThrowIfLoaded(); field = value; } } = 1024;

    /// <summary>Metres between vertices (Realistic 0.5–1, Faceted 2).</summary>
    [Export(Range = "0.25,4,0.25")]
    public float VertexSpacing { get; set { ThrowIfLoaded(); field = value; } } = 1f;

    /// <summary>Chunk side in metres: <see cref="ChunkQuads"/> = ChunkMeters / VertexSpacing must be even and divide the map.</summary>
    [Export(Range = "4,256,1")]
    public int ChunkMeters { get; set { ThrowIfLoaded(); field = value; } } = 64;

    /// <summary>Height of 16-bit value 0.</summary>
    [Export]
    public float HeightMin { get; set { ThrowIfLoaded(); field = value; } } = -64f;

    /// <summary>Height of 16-bit value 65535.</summary>
    [Export]
    public float HeightMax { get; set { ThrowIfLoaded(); field = value; } } = 192f;

    /// <summary>How quads split into triangles.</summary>
    [Export]
    public TerrainDiagonal Diagonal { get; set { ThrowIfLoaded(); field = value; } } = TerrainDiagonal.Checkerboard;

    /// <summary>Water depth of a full water layer (R = 255), in metres: the bed is the height minus the depth.</summary>
    [Export(Range = "0.1,16,0.1")]
    public float MaxWaterDepth { get; set { ThrowIfLoaded(); field = value; } } = 1.2f;

    /// <summary>Collision layer of the terrain's chunk bodies.</summary>
    [Export]
    public uint CollisionLayer { get; set; } = 1;

    /// <summary>Collision mask of the terrain's chunk bodies (static: 0 is usual).</summary>
    [Export]
    public uint CollisionMask { get; set; }

    [Export]
    public TerrainCollisionMode CollisionMode { get; set; } = TerrainCollisionMode.All;

    /// <summary>Whether the chunks cast shadows.</summary>
    [Export]
    public bool CastShadows { get; set; } = true;

    /// <summary>
    /// The foliage scattered over the terrain (grass, ferns, pebbles; ADR 0157), drawn by the terrain's
    /// <see cref="TerrainFoliage3D"/>. Indices are part of each type's placement hash: append new types at the end.
    /// </summary>
    [Export]
    public FoliageType[] FoliageTypes { get; set => field = value ?? []; } = [];

    // ── Derived sizes ──────────────────────────────────────────────────────────

    /// <summary>Quads per side (N).</summary>
    public int Quads => (int)MathF.Round(SizeMeters / VertexSpacing);

    /// <summary>Vertices per side (N + 1): the heightmap and water images' size.</summary>
    public int VerticesPerSide => Quads + 1;

    /// <summary>Paint cell size: the vertex spacing (Realistic) or half of it (Faceted).</summary>
    public float CellSize => Profile == TerrainProfile.Realistic ? VertexSpacing : VertexSpacing * 0.5f;

    /// <summary>Cells per side: the cell images' size.</summary>
    public int CellsPerSide => Profile == TerrainProfile.Realistic ? Quads : Quads * 2;

    /// <summary>Cells per quad side (1 Realistic, 2 Faceted).</summary>
    public int CellsPerQuad => Profile == TerrainProfile.Realistic ? 1 : 2;

    /// <summary>Quads per chunk side.</summary>
    public int ChunkQuads => (int)MathF.Round(ChunkMeters / VertexSpacing);

    /// <summary>Chunks per side.</summary>
    public int ChunksPerSide => Quads / ChunkQuads;

    /// <summary>The height grid's maths.</summary>
    public TerrainGrid Grid => _heights is not null ? _grid : new(Quads, VertexSpacing, Diagonal);

    /// <summary>Bumped by every edit.</summary>
    public int Version => _version;

    /// <summary>True once the layers are in memory (<see cref="EnsureLoaded"/>).</summary>
    public bool IsLoaded => _heights is not null;

    /// <summary>
    /// The folder the layer images are read from and saved to: set explicitly, else the folder of
    /// <see cref="Resource.ResourcePath"/>; null for code-made data that was never saved.
    /// </summary>
    public string? LayerFolder
    {
        get
        {
            if (field is not null)
                return field;
            return ResourcePath is { } path ? Path.GetDirectoryName(AssetDatabase.Current.ToAbsolutePath(path)) : null;
        }
        set;
    }

    /// <summary>Checks the knobs; throws <see cref="InvalidDataException"/> describing the first problem.</summary>
    public void Validate()
    {
        var maxSize = Profile == TerrainProfile.Realistic ? 2048 : 1024;
        if (SizeMeters <= 0 || SizeMeters > maxSize)
            throw new InvalidDataException($"Terrain size {SizeMeters} m is outside 1..{maxSize} m for the {Profile} profile.");
        if (!(VertexSpacing > 0) || !float.IsFinite(VertexSpacing))
            throw new InvalidDataException($"Vertex spacing {VertexSpacing} must be positive.");
        var quads = SizeMeters / VertexSpacing;
        if (MathF.Abs(quads - MathF.Round(quads)) > 1e-4f)
            throw new InvalidDataException($"Size {SizeMeters} m is not a whole number of {VertexSpacing} m quads.");
        if (Quads + 1 > MaxVerticesPerSide)
            throw new InvalidDataException($"{Quads + 1} vertices per side is more than {MaxVerticesPerSide}.");
        var chunkQuads = ChunkMeters / VertexSpacing;
        if (ChunkMeters <= 0 || MathF.Abs(chunkQuads - MathF.Round(chunkQuads)) > 1e-4f)
            throw new InvalidDataException($"Chunk size {ChunkMeters} m is not a whole number of {VertexSpacing} m quads.");
        if (ChunkQuads < 2 || ChunkQuads % 2 != 0 || Quads % ChunkQuads != 0)
            throw new InvalidDataException($"Chunks of {ChunkQuads} quads must be even and divide the map's {Quads} quads.");
        if (!(HeightMax > HeightMin) || !float.IsFinite(HeightMin) || !float.IsFinite(HeightMax))
            throw new InvalidDataException($"Height range {HeightMin}..{HeightMax} m is empty.");
        if (!(MaxWaterDepth > 0) || !float.IsFinite(MaxWaterDepth))
            throw new InvalidDataException($"Max water depth {MaxWaterDepth} m must be positive.");
    }

    private void ThrowIfLoaded()
    {
        if (_heights is not null)
            throw new InvalidOperationException("Terrain sizes, profile and height range are fixed once its layers are loaded.");
    }

    // ── Quantising ─────────────────────────────────────────────────────────────

    /// <summary>The height of one 16-bit step.</summary>
    public float HeightStep => (HeightMax - HeightMin) / 65535f;

    /// <summary>The 16-bit value of <paramref name="height"/> (clamped to the range).</summary>
    public ushort EncodeHeight(float height)
    {
        var v = MathF.Round((height - HeightMin) / HeightStep);
        return float.IsNaN(v) ? (ushort)0 : (ushort)Math.Clamp(v, 0f, 65535f);
    }

    /// <summary>The height of 16-bit value <paramref name="value"/>.</summary>
    public float DecodeHeight(ushort value) => HeightMin + value * HeightStep;

    /// <summary><paramref name="height"/> rounded to the 16-bit step and clamped to the range: what a write stores.</summary>
    public float QuantizeHeight(float height) => DecodeHeight(EncodeHeight(height));

    // ── Loading ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the layers from <see cref="LayerFolder"/> (missing files, or no folder, give the defaults: flat at height
    /// 0, layer 0 everywhere, no water). Called by <see cref="Terrain3D"/> when it builds; idempotent.
    /// </summary>
    public void EnsureLoaded()
    {
        if (_heights is not null)
            return;
        Validate();
        var folder = LayerFolder;
        var vertices = VerticesPerSide;
        var cells = CellsPerSide;

        var heights = new float[vertices * vertices];
        var heightPath = folder is null ? null : Path.Combine(folder, HeightmapFile);
        if (heightPath is not null && File.Exists(heightPath))
        {
            var image = Png.ReadGray16(heightPath);
            RequireSize(image.Width, image.Height, vertices, HeightmapFile);
            for (var i = 0; i < heights.Length; i++)
                heights[i] = DecodeHeight(image.Pixels[i]);
        }
        else
        {
            Array.Fill(heights, QuantizeHeight(0f));
        }

        var water = LoadRgba(folder, WaterFile, vertices, default);
        byte[]? splat0 = null, splat1 = null, surface = null;
        if (Profile == TerrainProfile.Realistic)
        {
            splat0 = LoadRgba(folder, SplatFile(0), cells, 0x000000FFu);
            splat1 = LoadRgba(folder, SplatFile(1), cells, 0u);
        }
        else
        {
            surface = LoadRgba(folder, SurfaceFile, cells, 0u);
        }

        var user = LoadRgba(folder, UserFile, cells, 0u);

        _grid = new TerrainGrid(Quads, VertexSpacing, Diagonal);
        _heights = heights;
        _water = water;
        _splat0 = splat0;
        _splat1 = splat1;
        _surface = surface;
        _user = user;
        _bed = new float[heights.Length];
        var chunks = ChunksPerSide;
        _chunkMin = new float[chunks * chunks];
        _chunkMax = new float[chunks * chunks];
        UpdateBed(new Rect2I(0, 0, vertices, vertices));
        _dirty = LayerFiles.None;
        _savedFolder = folder;
    }

    /// <summary>The splat image file of <paramref name="image"/> (0: layers 0–3, 1: layers 4–7).</summary>
    public static string SplatFile(int image) => image == 0 ? "splat-0.png" : "splat-1.png";

    private static byte[] LoadRgba(string? folder, string file, int size, uint fill)
    {
        var pixels = new byte[size * size * 4];
        var path = folder is null ? null : Path.Combine(folder, file);
        if (path is not null && File.Exists(path))
        {
            var image = Png.ReadRgba8(path);
            RequireSize(image.Width, image.Height, size, file);
            image.Pixels.CopyTo(pixels, 0);
        }
        else if (fill != 0)
        {
            for (var i = 0; i < pixels.Length; i += 4)
                WritePacked(pixels, i, fill);
        }

        return pixels;
    }

    private static void RequireSize(int width, int height, int size, string file)
    {
        if (width != size || height != size)
            throw new InvalidDataException($"Terrain layer '{file}' is {width}×{height}; expected {size}×{size}.");
    }

    // ── Saving ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the changed layers into <paramref name="folder"/> (default: <see cref="LayerFolder"/>), each to a
    /// temporary file then renamed. Layers at their defaults are not written (an existing file is deleted), so a
    /// folder may hold only <c>terrain.mres</c>. Saving to a new folder writes every non-default layer.
    /// </summary>
    public void SaveLayers(string? folder = null)
    {
        folder ??= LayerFolder ?? throw new InvalidOperationException("The terrain data has no folder: pass one or save it as a resource.");
        if (_heights is null)
            return; // never loaded: nothing changed
        folder = Path.GetFullPath(folder);
        var write = string.Equals(folder, _savedFolder is null ? null : Path.GetFullPath(_savedFolder), StringComparison.Ordinal)
            ? _dirty
            : LayerFiles.All;
        Directory.CreateDirectory(folder);
        var vertices = VerticesPerSide;
        var cells = CellsPerSide;

        if ((write & LayerFiles.Heightmap) != 0)
        {
            var path = Path.Combine(folder, HeightmapFile);
            var flat = EncodeHeight(0f);
            var encoded = new ushort[_heights.Length];
            var isDefault = true;
            for (var i = 0; i < encoded.Length; i++)
            {
                encoded[i] = EncodeHeight(_heights[i]);
                isDefault &= encoded[i] == flat;
            }

            if (isDefault)
                DeleteIfExists(path);
            else
                Atomically(path, temp => Png.WriteGray16(temp, vertices, vertices, encoded));
        }

        if ((write & LayerFiles.Water) != 0)
            SaveRgba(folder, WaterFile, _water!, vertices, 0u);
        if ((write & LayerFiles.Splat0) != 0 && _splat0 is not null)
            SaveRgba(folder, SplatFile(0), _splat0, cells, 0x000000FFu);
        if ((write & LayerFiles.Splat1) != 0 && _splat1 is not null)
            SaveRgba(folder, SplatFile(1), _splat1, cells, 0u);
        if ((write & LayerFiles.Surface) != 0 && _surface is not null)
            SaveRgba(folder, SurfaceFile, _surface, cells, 0u);
        if ((write & LayerFiles.User) != 0)
            SaveRgba(folder, UserFile, _user!, cells, 0u);
        SaveCarveRecords(folder, everything: write == LayerFiles.All);

        _dirty = LayerFiles.None;
        _savedFolder = folder;
        if (LayerFolder is null || !string.Equals(Path.GetFullPath(LayerFolder), folder, StringComparison.Ordinal))
            LayerFolder = folder;
    }

    private static void SaveRgba(string folder, string file, byte[] pixels, int size, uint fill)
    {
        var path = Path.Combine(folder, file);
        var isDefault = true;
        for (var i = 0; i < pixels.Length && isDefault; i += 4)
            isDefault = ReadPacked(pixels, i) == fill;
        if (isDefault)
            DeleteIfExists(path);
        else
            Atomically(path, temp => Png.WriteRgba8(temp, size, size, pixels));
    }

    private static void Atomically(string path, Action<string> write)
    {
        var temp = path + ".tmp";
        write(temp);
        File.Move(temp, path, overwrite: true);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>The folder a new terrain of scene <paramref name="scenePath"/> saves into: <c>&lt;scene&gt;_terrain/</c> beside it.</summary>
    public static string DefaultFolderFor(string scenePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(scenePath);
        var directory = Path.GetDirectoryName(scenePath) ?? "";
        var folder = Path.GetFileNameWithoutExtension(scenePath) + "_terrain";
        return string.IsNullOrEmpty(directory) ? folder : Path.Combine(directory, folder).Replace('\\', '/');
    }

    // ── Reading ────────────────────────────────────────────────────────────────

    /// <summary>The stored heights, row by row ((N+1)²): the water surface where there is water.</summary>
    public ReadOnlySpan<float> Heights
    {
        get
        {
            EnsureLoaded();
            return _heights;
        }
    }

    /// <summary>
    /// The ground: <see cref="Heights"/> minus the water depth, row by row. Meshes, collision and the height queries use
    /// it.
    /// </summary>
    public ReadOnlySpan<float> BedHeights
    {
        get
        {
            EnsureLoaded();
            return _bed;
        }
    }

    internal float[] BedArray
    {
        get
        {
            EnsureLoaded();
            return _bed!;
        }
    }

    internal ReadOnlySpan<float> ChunkMin => _chunkMin;
    internal ReadOnlySpan<float> ChunkMax => _chunkMax;

    /// <summary>Lowest and highest ground height (the bed) of the whole map.</summary>
    public (float Min, float Max) BedRange
    {
        get
        {
            EnsureLoaded();
            float min = float.MaxValue, max = float.MinValue;
            for (var i = 0; i < _chunkMin.Length; i++)
            {
                min = MathF.Min(min, _chunkMin[i]);
                max = MathF.Max(max, _chunkMax[i]);
            }

            return (min, max);
        }
    }

    /// <summary>Lowest and highest bed height of chunk (<paramref name="cx"/>, <paramref name="cz"/>), edges included.</summary>
    public (float Min, float Max) ChunkHeightRange(int cx, int cz)
    {
        EnsureLoaded();
        var c = cz * ChunksPerSide + cx;
        return (_chunkMin[c], _chunkMax[c]);
    }

    /// <summary>Copies the stored heights of vertex rectangle <paramref name="vertices"/> (row by row) into <paramref name="destination"/>.</summary>
    public void GetHeights(Rect2I vertices, Span<float> destination)
    {
        EnsureLoaded();
        CheckRect(vertices, VerticesPerSide, destination.Length);
        var stride = VerticesPerSide;
        for (var y = 0; y < vertices.Size.Y; y++)
            _heights.AsSpan((vertices.Position.Y + y) * stride + vertices.Position.X, vertices.Size.X)
                .CopyTo(destination.Slice(y * vertices.Size.X, vertices.Size.X));
    }

    /// <summary>
    /// Copies packed RGBA texels (R in the low byte) of <paramref name="layer"/> image <paramref name="image"/>:
    /// <see cref="TerrainLayers.Surface"/> (Realistic splat 0/1, Faceted surface), <see cref="TerrainLayers.User"/>
    /// (cells) or <see cref="TerrainLayers.Water"/> (vertices).
    /// </summary>
    public void GetCells(TerrainLayers layer, int image, Rect2I rect, Span<uint> destination)
    {
        EnsureLoaded();
        var (pixels, size, _) = LayerImage(layer, image);
        CheckRect(rect, size, destination.Length);
        for (var y = 0; y < rect.Size.Y; y++)
            for (var x = 0; x < rect.Size.X; x++)
                destination[y * rect.Size.X + x] = ReadPacked(pixels, ((rect.Position.Y + y) * size + rect.Position.X + x) * 4);
    }

    /// <summary>The weight (0..1) of splat layer <paramref name="layer"/> in cell (<paramref name="u"/>, <paramref name="v"/>), Realistic.</summary>
    public float GetLayerWeight(int layer, int u, int v)
    {
        EnsureLoaded();
        if (_splat0 is null || (uint)layer >= MaxLayers)
            return layer == 0 && _splat0 is null ? 1f : 0f;
        var size = CellsPerSide;
        u = Math.Clamp(u, 0, size - 1);
        v = Math.Clamp(v, 0, size - 1);
        var pixels = layer < 4 ? _splat0 : _splat1!;
        return pixels[(v * size + u) * 4 + (layer & 3)] / 255f;
    }

    /// <summary>
    /// The strongest splat layer of cell (<paramref name="u"/>, <paramref name="v"/>) (Realistic; ties go to the lower
    /// index), or its surface id (Faceted).
    /// </summary>
    public int GetSurface(int u, int v)
    {
        EnsureLoaded();
        var size = CellsPerSide;
        u = Math.Clamp(u, 0, size - 1);
        v = Math.Clamp(v, 0, size - 1);
        var o = (v * size + u) * 4;
        if (_surface is not null)
            return _surface[o];
        var best = 0;
        var bestWeight = -1;
        for (var layer = 0; layer < MaxLayers; layer++)
        {
            int w = layer < 4 ? _splat0![o + layer] : _splat1![o + layer - 4];
            if (w > bestWeight)
            {
                bestWeight = w;
                best = layer;
            }
        }

        return best;
    }

    /// <summary>User channel <paramref name="channel"/> (0..3) of cell (<paramref name="u"/>, <paramref name="v"/>).</summary>
    public byte GetUserChannel(int channel, int u, int v)
    {
        EnsureLoaded();
        var size = CellsPerSide;
        u = Math.Clamp(u, 0, size - 1);
        v = Math.Clamp(v, 0, size - 1);
        return _user![(v * size + u) * 4 + (channel & 3)];
    }

    /// <summary>The water depth fraction (0..1) of vertex (<paramref name="i"/>, <paramref name="j"/>).</summary>
    public float GetWaterFraction(int i, int j)
    {
        EnsureLoaded();
        return _water![(j * VerticesPerSide + i) * 4] / 255f;
    }

    internal byte[] WaterPixels
    {
        get
        {
            EnsureLoaded();
            return _water!;
        }
    }

    /// <summary>
    /// Splat weight map <paramref name="image"/> (0: layers 0–3 in RGBA, 1: layers 4–7) as a linear, unmipmapped,
    /// bilinear, clamped texture (one texel per cell, texel centres at cell centres: UV = terrain-local XZ ÷ size). Kept
    /// in sync with edits (each edit re-uploads the whole map for now). Realistic only.
    /// </summary>
    public Texture2D GetSplatTexture(int image)
    {
        if ((uint)image > 1)
            throw new ArgumentOutOfRangeException(nameof(image), image, "Splat images are 0 and 1.");
        EnsureLoaded();
        if (Profile != TerrainProfile.Realistic)
            throw new InvalidOperationException("Splat maps belong to the Realistic profile.");
        return _splatTextures[image] ??= Texture2D.FromPixels(CellsPerSide, CellsPerSide, image == 0 ? _splat0! : _splat1!, SplatTextureSettings);
    }

    private static readonly TextureImportSettings SplatTextureSettings = new()
    {
        ColorSpace = TextureImportColorSpace.Linear,
        Mipmaps = false,
        Filter = TextureFilter.Linear,
        Wrap = TextureWrap.Clamp,
    };

    // ── Writing ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised after every write, with the layers and the rectangle written (vertices for height and water, cells for
    /// surface and user). <see cref="Terrain3D"/> rebuilds the touched chunks.
    /// </summary>
    internal event Action<TerrainLayers, Rect2I, bool>? Edited;

    /// <summary>The edit recording the next writes (one at a time).</summary>
    internal TerrainEdit? Recorder { get; set; }

    /// <summary>
    /// Writes heights (row by row) into vertex rectangle <paramref name="vertices"/>, quantised to the 16-bit step and
    /// clamped to the range.
    /// </summary>
    public void SetHeights(Rect2I vertices, ReadOnlySpan<float> heights)
    {
        EnsureLoaded();
        CheckRect(vertices, VerticesPerSide, heights.Length);
        Recorder?.Capture(TerrainLayers.Height, vertices);
        var stride = VerticesPerSide;
        for (var y = 0; y < vertices.Size.Y; y++)
        {
            var row = (vertices.Position.Y + y) * stride + vertices.Position.X;
            for (var x = 0; x < vertices.Size.X; x++)
                _heights![row + x] = QuantizeHeight(heights[y * vertices.Size.X + x]);
        }

        Commit(TerrainLayers.Height, vertices, LayerFiles.Heightmap);
    }

    /// <summary>Sets every height from <paramref name="height"/>(x, z) at the vertices (terrain-local metres).</summary>
    public void SetHeightsFrom(Func<float, float, float> height)
    {
        ArgumentNullException.ThrowIfNull(height);
        EnsureLoaded();
        var all = new Rect2I(0, 0, VerticesPerSide, VerticesPerSide);
        Recorder?.Capture(TerrainLayers.Height, all);
        var stride = VerticesPerSide;
        for (var j = 0; j < stride; j++)
            for (var i = 0; i < stride; i++)
                _heights![j * stride + i] = QuantizeHeight(height(i * VertexSpacing, j * VertexSpacing));
        Commit(TerrainLayers.Height, all, LayerFiles.Heightmap);
    }

    /// <summary>
    /// Writes splat weights into cell rectangle <paramref name="cells"/>: eight per cell (layers 0–7), any
    /// non-negative scale; each cell is normalised to sum 255 (all zero: layer 0). Realistic only.
    /// </summary>
    public void SetWeights(Rect2I cells, ReadOnlySpan<float> weights)
    {
        EnsureLoaded();
        RequireRealistic();
        CheckRect(cells, CellsPerSide, weights.Length / MaxLayers);
        if (weights.Length != cells.Area * MaxLayers)
            throw new ArgumentException($"Expected {cells.Area * MaxLayers} weights (8 per cell), got {weights.Length}.", nameof(weights));
        Recorder?.Capture(TerrainLayers.Surface, cells);
        Span<byte> normalised = stackalloc byte[MaxLayers];
        var size = CellsPerSide;
        for (var y = 0; y < cells.Size.Y; y++)
            for (var x = 0; x < cells.Size.X; x++)
            {
                NormalizeWeights(weights.Slice((y * cells.Size.X + x) * MaxLayers, MaxLayers), normalised);
                StoreWeights(((cells.Position.Y + y) * size + cells.Position.X + x) * 4, normalised);
            }

        Commit(TerrainLayers.Surface, cells, LayerFiles.Splat0 | LayerFiles.Splat1);
    }

    /// <summary>Sets every cell's splat weights from <paramref name="weights"/> at the cell centres (terrain-local metres). Realistic only.</summary>
    public void SetWeightsFrom(TerrainWeightGenerator weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        EnsureLoaded();
        RequireRealistic();
        var size = CellsPerSide;
        var all = new Rect2I(0, 0, size, size);
        Recorder?.Capture(TerrainLayers.Surface, all);
        Span<float> raw = stackalloc float[MaxLayers];
        Span<byte> normalised = stackalloc byte[MaxLayers];
        for (var v = 0; v < size; v++)
            for (var u = 0; u < size; u++)
            {
                raw.Clear();
                weights((u + 0.5f) * CellSize, (v + 0.5f) * CellSize, raw);
                NormalizeWeights(raw, normalised);
                StoreWeights((v * size + u) * 4, normalised);
            }

        Commit(TerrainLayers.Surface, all, LayerFiles.Splat0 | LayerFiles.Splat1);
    }

    /// <summary>
    /// Paints splat layer <paramref name="layer"/> (Realistic) or surface id <paramref name="layer"/> (Faceted) over
    /// the cells whose centre is within <paramref name="radius"/> of <paramref name="centerXZ"/> (terrain-local metres).
    /// Realistic: the layer's weight moves toward 1 by <paramref name="strength"/> and the others scale to keep the sum
    /// at 255. Faceted: cells take the id (strength ignored).
    /// </summary>
    public void PaintSurface(Vector2 centerXZ, float radius, int layer, float strength = 1f)
    {
        EnsureLoaded();
        var size = CellsPerSide;
        var cell = CellSize;
        var u0 = Math.Clamp((int)MathF.Floor((centerXZ.X - radius) / cell), 0, size - 1);
        var v0 = Math.Clamp((int)MathF.Floor((centerXZ.Y - radius) / cell), 0, size - 1);
        var u1 = Math.Clamp((int)MathF.Floor((centerXZ.X + radius) / cell), 0, size - 1);
        var v1 = Math.Clamp((int)MathF.Floor((centerXZ.Y + radius) / cell), 0, size - 1);
        var rect = new Rect2I(u0, v0, u1 - u0 + 1, v1 - v0 + 1);
        Recorder?.Capture(TerrainLayers.Surface, rect);
        strength = Math.Clamp(strength, 0f, 1f);
        Span<float> weights = stackalloc float[MaxLayers];
        Span<byte> normalised = stackalloc byte[MaxLayers];
        var r2 = radius * radius;
        for (var v = v0; v <= v1; v++)
            for (var u = u0; u <= u1; u++)
            {
                var dx = (u + 0.5f) * cell - centerXZ.X;
                var dz = (v + 0.5f) * cell - centerXZ.Y;
                if (dx * dx + dz * dz > r2)
                    continue;
                var o = (v * size + u) * 4;
                if (_surface is not null)
                {
                    _surface[o] = (byte)Math.Clamp(layer, 0, 255);
                    continue;
                }

                if ((uint)layer >= MaxLayers)
                    throw new ArgumentOutOfRangeException(nameof(layer), layer, "Realistic terrains have layers 0–7.");
                for (var l = 0; l < MaxLayers; l++)
                {
                    var w = (l < 4 ? _splat0![o + l] : _splat1![o + l - 4]) / 255f;
                    weights[l] = l == layer ? w + (1f - w) * strength : w * (1f - strength);
                }

                NormalizeWeights(weights, normalised);
                StoreWeights(o, normalised);
            }

        Commit(TerrainLayers.Surface, rect, _surface is not null ? LayerFiles.Surface : LayerFiles.Splat0 | LayerFiles.Splat1);
    }

    /// <summary>
    /// Writes packed RGBA texels (R in the low byte) into <paramref name="layer"/> image <paramref name="image"/> (see
    /// <see cref="GetCells"/>). Splat texels are written as given (keep each cell's weights summing to 255).
    /// </summary>
    public void SetCells(TerrainLayers layer, int image, Rect2I rect, ReadOnlySpan<uint> source)
    {
        EnsureLoaded();
        var (pixels, size, file) = LayerImage(layer, image);
        CheckRect(rect, size, source.Length);
        Recorder?.Capture(layer, rect);
        for (var y = 0; y < rect.Size.Y; y++)
            for (var x = 0; x < rect.Size.X; x++)
                WritePacked(pixels, ((rect.Position.Y + y) * size + rect.Position.X + x) * 4, source[y * rect.Size.X + x]);
        Commit(layer, rect, file);
    }

    /// <summary>Writes water depths in metres (row by row, 0..<see cref="MaxWaterDepth"/>) into vertex rectangle <paramref name="vertices"/>.</summary>
    public void SetWaterDepth(Rect2I vertices, ReadOnlySpan<float> depthMeters)
    {
        EnsureLoaded();
        CheckRect(vertices, VerticesPerSide, depthMeters.Length);
        Recorder?.Capture(TerrainLayers.Water, vertices);
        var stride = VerticesPerSide;
        for (var y = 0; y < vertices.Size.Y; y++)
            for (var x = 0; x < vertices.Size.X; x++)
                _water![((vertices.Position.Y + y) * stride + vertices.Position.X + x) * 4] = EncodeWater(depthMeters[y * vertices.Size.X + x]);
        Commit(TerrainLayers.Water, vertices, LayerFiles.Water);
    }

    /// <summary>Sets every vertex's water depth from <paramref name="depth"/>(x, z) in metres (terrain-local).</summary>
    public void SetWaterDepthFrom(Func<float, float, float> depth)
    {
        ArgumentNullException.ThrowIfNull(depth);
        EnsureLoaded();
        var stride = VerticesPerSide;
        var all = new Rect2I(0, 0, stride, stride);
        Recorder?.Capture(TerrainLayers.Water, all);
        for (var j = 0; j < stride; j++)
            for (var i = 0; i < stride; i++)
                _water![(j * stride + i) * 4] = EncodeWater(depth(i * VertexSpacing, j * VertexSpacing));
        Commit(TerrainLayers.Water, all, LayerFiles.Water);
    }

    private byte EncodeWater(float depth)
    {
        var f = MathF.Round(depth / MaxWaterDepth * 255f);
        return float.IsNaN(f) ? (byte)0 : (byte)Math.Clamp(f, 0f, 255f);
    }

    /// <summary>Writes a recorded tile back (undo/redo): no recording, raised as <c>fromUndo</c>.</summary>
    internal void RestoreTile(TerrainLayers layer, int image, Rect2I rect, float[]? heights, uint[]? texels)
    {
        EnsureLoaded();
        var recorder = Recorder;
        Recorder = null;
        try
        {
            if (layer == TerrainLayers.Height)
            {
                var stride = VerticesPerSide;
                for (var y = 0; y < rect.Size.Y; y++)
                    heights.AsSpan(y * rect.Size.X, rect.Size.X).CopyTo(_heights.AsSpan((rect.Position.Y + y) * stride + rect.Position.X));
                Commit(TerrainLayers.Height, rect, LayerFiles.Heightmap, fromUndo: true);
                return;
            }

            var (pixels, size, file) = LayerImage(layer, image);
            for (var y = 0; y < rect.Size.Y; y++)
                for (var x = 0; x < rect.Size.X; x++)
                    WritePacked(pixels, ((rect.Position.Y + y) * size + rect.Position.X + x) * 4, texels![y * rect.Size.X + x]);
            Commit(layer, rect, file, fromUndo: true);
        }
        finally
        {
            Recorder = recorder;
        }
    }

    private void Commit(TerrainLayers layer, Rect2I rect, LayerFiles files, bool fromUndo = false)
    {
        if (layer is TerrainLayers.Height or TerrainLayers.Water)
            UpdateBed(rect);
        if (layer == TerrainLayers.Surface)
            SyncSplatTextures(files);
        _dirty |= files;
        _version++;
        Edited?.Invoke(layer, rect, fromUndo);
        EmitChanged();
    }

    private void SyncSplatTextures(LayerFiles files)
    {
        if ((files & LayerFiles.Splat0) != 0 && _splatTextures[0] is { } t0)
            t0.SetPixels(_splat0);
        if ((files & LayerFiles.Splat1) != 0 && _splatTextures[1] is { } t1)
            t1.SetPixels(_splat1);
    }

    private (byte[] Pixels, int Size, LayerFiles File) LayerImage(TerrainLayers layer, int image) => layer switch
    {
        TerrainLayers.Surface when _surface is not null && image == 0 => (_surface, CellsPerSide, LayerFiles.Surface),
        TerrainLayers.Surface when _surface is null && image is 0 or 1 =>
            (image == 0 ? _splat0! : _splat1!, CellsPerSide, image == 0 ? LayerFiles.Splat0 : LayerFiles.Splat1),
        TerrainLayers.User when image == 0 => (_user!, CellsPerSide, LayerFiles.User),
        TerrainLayers.Water when image == 0 => (_water!, VerticesPerSide, LayerFiles.Water),
        _ => throw new ArgumentException($"Terrain layer {layer} has no image {image}.", nameof(layer)),
    };

    private void StoreWeights(int offset, ReadOnlySpan<byte> weights)
    {
        for (var l = 0; l < 4; l++)
        {
            _splat0![offset + l] = weights[l];
            _splat1![offset + l] = weights[l + 4];
        }
    }

    private void RequireRealistic()
    {
        if (Profile != TerrainProfile.Realistic)
            throw new InvalidOperationException("Splat weights belong to the Realistic profile.");
    }

    /// <summary>
    /// Scales <paramref name="weights"/> to bytes summing to exactly 255 (largest remainders get the rounding); all zero
    /// (or invalid) gives layer 0.
    /// </summary>
    public static void NormalizeWeights(ReadOnlySpan<float> weights, Span<byte> result)
    {
        var total = 0f;
        for (var l = 0; l < weights.Length; l++)
            if (weights[l] > 0f && float.IsFinite(weights[l]))
                total += weights[l];
        result.Clear();
        if (!(total > 0f))
        {
            result[0] = 255;
            return;
        }

        Span<float> remainders = stackalloc float[weights.Length];
        var sum = 0;
        for (var l = 0; l < weights.Length; l++)
        {
            var w = weights[l] > 0f && float.IsFinite(weights[l]) ? weights[l] / total * 255f : 0f;
            var whole = (int)MathF.Floor(w);
            result[l] = (byte)whole;
            remainders[l] = w - whole;
            sum += whole;
        }

        for (; sum < 255; sum++)
        {
            var best = 0;
            for (var l = 1; l < weights.Length; l++)
                if (remainders[l] > remainders[best])
                    best = l;
            result[best]++;
            remainders[best] = -1f;
        }
    }

    private void UpdateBed(Rect2I vertices)
    {
        var stride = VerticesPerSide;
        var depth = MaxWaterDepth / 255f;
        var x1 = Math.Min(vertices.End.X, stride);
        var y1 = Math.Min(vertices.End.Y, stride);
        for (var j = Math.Max(vertices.Position.Y, 0); j < y1; j++)
            for (var i = Math.Max(vertices.Position.X, 0); i < x1; i++)
            {
                var k = j * stride + i;
                _bed![k] = _heights![k] - _water![k * 4] * depth;
            }

        // Chunk ranges (edge vertices belong to both chunks).
        var q = ChunkQuads;
        var chunks = ChunksPerSide;
        var cx0 = Math.Max(0, (vertices.Position.X - 1) / q);
        var cz0 = Math.Max(0, (vertices.Position.Y - 1) / q);
        var cx1 = Math.Min(chunks - 1, (x1 - 1) / q);
        var cz1 = Math.Min(chunks - 1, (y1 - 1) / q);
        for (var cz = cz0; cz <= cz1; cz++)
            for (var cx = cx0; cx <= cx1; cx++)
            {
                float min = float.MaxValue, max = float.MinValue;
                for (var j = cz * q; j <= (cz + 1) * q; j++)
                    for (var i = cx * q; i <= (cx + 1) * q; i++)
                    {
                        var h = _bed![j * stride + i];
                        min = MathF.Min(min, h);
                        max = MathF.Max(max, h);
                    }

                _chunkMin[cz * chunks + cx] = min;
                _chunkMax[cz * chunks + cx] = max;
            }
    }

    // ── Carve records (River3D) ────────────────────────────────────────────────

    private readonly Dictionary<string, TerrainCarveRecord?> _carves = new(StringComparer.Ordinal); // null: removed (or no file)
    private readonly HashSet<string> _dirtyCarves = new(StringComparer.Ordinal);

    /// <summary>The original heights carve <paramref name="id"/> replaced (in memory, else read from the layer folder), or null.</summary>
    internal TerrainCarveRecord? GetCarveRecord(string id)
    {
        if (!TerrainCarveRecord.IsValidId(id))
            return null;
        if (_carves.TryGetValue(id, out var record))
            return record;
        var path = LayerFolder is { } folder ? Path.Combine(folder, TerrainCarveRecord.FileName(id)) : null;
        if (path is not null && File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            record = TerrainCarveRecord.Read(stream, this);
        }

        _carves[id] = record;
        return record;
    }

    /// <summary>Stores (or with null removes) carve <paramref name="id"/>'s record; written or deleted by the next <see cref="SaveLayers"/>.</summary>
    internal void SetCarveRecord(string id, TerrainCarveRecord? record)
    {
        if (!TerrainCarveRecord.IsValidId(id))
            throw new ArgumentException($"'{id}' is not a valid carve id.", nameof(id));
        _carves[id] = record;
        _dirtyCarves.Add(id);
    }

    private void SaveCarveRecords(string folder, bool everything)
    {
        // Moving to a new folder: carry over the records that were never read from the old one.
        if (everything && _savedFolder is { } previous && Directory.Exists(previous) &&
            !string.Equals(Path.GetFullPath(previous), folder, StringComparison.Ordinal))
        {
            foreach (var path in Directory.EnumerateFiles(previous, "carve_*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(path)["carve_".Length..];
                if (TerrainCarveRecord.IsValidId(id) && !_carves.ContainsKey(id))
                    File.Copy(path, Path.Combine(folder, TerrainCarveRecord.FileName(id)), overwrite: true);
            }
        }

        foreach (var (id, record) in _carves)
        {
            if (!everything && !_dirtyCarves.Contains(id))
                continue;
            var path = Path.Combine(folder, TerrainCarveRecord.FileName(id));
            if (record is null)
            {
                DeleteIfExists(path);
                continue;
            }

            Atomically(path, temp =>
            {
                using var stream = File.Create(temp);
                record.Write(stream, this);
            });
        }

        _dirtyCarves.Clear();
    }

    private static void CheckRect(Rect2I rect, int size, int length)
    {
        if (rect.Position.X < 0 || rect.Position.Y < 0 || rect.Size.X < 0 || rect.Size.Y < 0 ||
            rect.End.X > size || rect.End.Y > size)
            throw new ArgumentOutOfRangeException(nameof(rect), rect, $"Rectangle is outside the {size}×{size} layer.");
        if (length != rect.Area)
            throw new ArgumentException($"Expected {rect.Area} values for a {rect.Size.X}×{rect.Size.Y} rectangle, got {length}.");
    }

    internal static uint ReadPacked(byte[] pixels, int offset) =>
        pixels[offset] | (uint)pixels[offset + 1] << 8 | (uint)pixels[offset + 2] << 16 | (uint)pixels[offset + 3] << 24;

    internal static void WritePacked(byte[] pixels, int offset, uint value)
    {
        pixels[offset] = (byte)value;
        pixels[offset + 1] = (byte)(value >> 8);
        pixels[offset + 2] = (byte)(value >> 16);
        pixels[offset + 3] = (byte)(value >> 24);
    }
}
