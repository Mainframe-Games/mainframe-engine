using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>How a <see cref="LightProbeVolume"/> places its probes.</summary>
public enum ProbeLayout : byte
{
    /// <summary>A regular grid over the volume's box (interiors, prop scenes).</summary>
    Box,

    /// <summary>
    /// An XZ grid over the terrain with <see cref="LightProbeVolume.LayerHeights"/> above the ground under each column,
    /// denser near the ground where the receivers are (the Forest).
    /// </summary>
    TerrainFollowing,
}

/// <summary>
/// Where the probes of a volume are: the grid of a <see cref="LightProbeData"/> (and of a bake). Probe (x, y, z) stands at
/// <c>Origin + (x · Spacing.X, y · Spacing.Y, z · Spacing.Z)</c> in a box layout; terrain-following, its height is the
/// ground under its column (<c>ground</c>) + <c>LayerHeights[y]</c>. The grid is axis-aligned in world space.
/// </summary>
public readonly record struct ProbeGrid(ProbeLayout Layout, Vector3 Origin, Vector3 Spacing, int CountX, int CountY, int CountZ, float[] LayerHeights)
{
    /// <summary>Layers a terrain-following grid may have (the shader's table holds eight).</summary>
    public const int MaxLayers = 8;

    /// <summary>The 3D texture's slabs along Y: sky visibility, bounce red, green, blue, ground height.</summary>
    public const int Slabs = 5;

    /// <summary>Probes in the grid.</summary>
    public int ProbeCount => CountX * CountY * CountZ;

    /// <summary>Columns (XZ) in the grid.</summary>
    public int ColumnCount => CountX * CountZ;

    /// <summary>The flat index of probe (x, y, z): x fastest, then y, then z.</summary>
    public int Index(int x, int y, int z) => (z * CountY + y) * CountX + x;

    /// <summary>The flat index of column (x, z).</summary>
    public int Column(int x, int z) => z * CountX + x;

    /// <summary>Probe (x, y, z)'s world position over ground height <paramref name="ground"/> (terrain-following; ignored for a box).</summary>
    public Vector3 Position(int x, int y, int z, float ground) => Layout == ProbeLayout.TerrainFollowing
        ? new Vector3(Origin.X + x * Spacing.X, ground + LayerHeights[y], Origin.Z + z * Spacing.Z)
        : Origin + new Vector3(x * Spacing.X, y * Spacing.Y, z * Spacing.Z);

    /// <summary>
    /// The continuous layer coordinate (0 … CountY − 1) of a point <paramref name="height"/> metres above the ground
    /// (terrain-following: piecewise linear between the layer heights, clamped) or above the origin (box). What
    /// <c>probeLayerCoordinate</c> in <c>include/probes.slang</c> computes.
    /// </summary>
    public float LayerCoordinate(float height)
    {
        if (Layout == ProbeLayout.Box)
            return Math.Clamp(height / Spacing.Y, 0f, CountY - 1);
        var layers = LayerHeights;
        if (height <= layers[0])
            return 0f;
        for (var i = 0; i + 1 < CountY; i++)
            if (height < layers[i + 1])
                return i + (height - layers[i]) / MathF.Max(layers[i + 1] - layers[i], 1e-4f);
        return CountY - 1;
    }

    /// <summary>A grid with nothing in it (no volume).</summary>
    public bool IsEmpty => CountX <= 0 || CountY <= 0 || CountZ <= 0;
}

/// <summary>
/// A baked <see cref="LightProbeVolume"/> (ADR 0170): per probe, the sky's visibility (SH L1: 4 numbers) and the bounce
/// light reaching it (SH L1 RGB: 12 numbers), plus the ground height under each column of a terrain-following grid. The
/// <c>.mres</c> holds the grid and the bake's inputs hash (<see cref="BakeHash"/>); the coefficients live in a binary
/// <c>.probes</c> file next to it (<see cref="DataFile"/>, half floats, in LFS), read on first use. A bake made in code
/// (tests, a bake at load) keeps them in memory.
/// </summary>
/// <remarks>
/// On the GPU (<see cref="ToTexture"/>) the data is one <see cref="Texture3D"/> of
/// <c>CountX × (5 · CountY) × CountZ</c> RGBA16F texels: five slabs stacked along Y (sky visibility, bounce R, G, B, then
/// the ground height in r), each an SH L1 set (c0, c1.x, c1.y, c1.z) per probe (see <see cref="SphericalHarmonics"/>).
/// </remarks>
[EditorIcon("bulb")]
public sealed class LightProbeData : Resource
{
    /// <summary>Numbers per probe: sky visibility (4), bounce red, green, blue (4 each).</summary>
    public const int CoefficientsPerProbe = 16;

    private const ulong FileMagic = 0x3130425250464D; // "MFPRB01" little-endian
    private float[]? _coefficients;
    private float[]? _ground;
    private Texture3D? _texture;
    private bool _loadFailed;

    [Export] public ProbeLayout Layout { get; set; }

    /// <summary>World position of probe (0, 0, 0) (terrain-following: Y unused).</summary>
    [Export] public Vector3 Origin { get; set; }

    /// <summary>Metres between probes (terrain-following: Y unused).</summary>
    [Export] public Vector3 Spacing { get; set; } = new(2f, 2f, 2f);

    [Export] public int CountX { get; set; }

    /// <summary>Probes along Y: layers of a terrain-following grid.</summary>
    [Export] public int CountY { get; set; }

    [Export] public int CountZ { get; set; }

    /// <summary>Terrain-following: metres above the ground of each layer.</summary>
    [Export] public float[] LayerHeights { get; set; } = [];

    /// <summary>A hash of everything the bake read (the scene's proxies, the sun, the sky, the settings): a bake whose hash differs from the scene's is stale.</summary>
    [Export] public string BakeHash { get; set; } = string.Empty;

    /// <summary>Rays traced per probe.</summary>
    [Export] public int RaysPerProbe { get; set; }

    /// <summary>Bounces baked.</summary>
    [Export] public int Bounces { get; set; }

    /// <summary>The coefficients' file name next to the <c>.mres</c> (empty: in memory only).</summary>
    [Export] public string DataFile { get; set; } = string.Empty;

    /// <summary>The probes' grid.</summary>
    public ProbeGrid Grid => new(Layout, Origin, Spacing, CountX, CountY, CountZ, LayerHeights);

    /// <summary>
    /// The coefficients, <see cref="CoefficientsPerProbe"/> per probe in <see cref="ProbeGrid.Index"/> order (read from
    /// <see cref="DataFile"/> on first use; empty when there are none).
    /// </summary>
    public ReadOnlySpan<float> Coefficients
    {
        get
        {
            EnsureLoaded();
            return _coefficients;
        }
    }

    /// <summary>Ground height under each column (terrain-following; zeros for a box).</summary>
    public ReadOnlySpan<float> Ground
    {
        get
        {
            EnsureLoaded();
            return _ground;
        }
    }

    /// <summary>True when the coefficients are there (in memory or readable from the file).</summary>
    public bool HasData
    {
        get
        {
            EnsureLoaded();
            return _coefficients is { Length: > 0 } c && c.Length == Grid.ProbeCount * CoefficientsPerProbe && !Grid.IsEmpty;
        }
    }

    /// <summary>Stores a bake's results (copies them) and drops the GPU texture.</summary>
    public void SetData(ProbeGrid grid, ReadOnlySpan<float> coefficients, ReadOnlySpan<float> ground)
    {
        if (coefficients.Length != grid.ProbeCount * CoefficientsPerProbe)
            throw new ArgumentException($"Expected {grid.ProbeCount * CoefficientsPerProbe} coefficients, got {coefficients.Length}.", nameof(coefficients));
        if (ground.Length != grid.ColumnCount)
            throw new ArgumentException($"Expected {grid.ColumnCount} ground heights, got {ground.Length}.", nameof(ground));
        Layout = grid.Layout;
        Origin = grid.Origin;
        Spacing = grid.Spacing;
        CountX = grid.CountX;
        CountY = grid.CountY;
        CountZ = grid.CountZ;
        LayerHeights = (float[])grid.LayerHeights.Clone();
        _coefficients = coefficients.ToArray();
        _ground = ground.ToArray();
        _loadFailed = false;
        _texture = null;
        EmitChanged();
    }

    /// <summary>
    /// The coefficients at <paramref name="position"/> as the shaders sample them (<c>sampleProbes</c>): trilinear between
    /// the eight probes around it, clamped to the grid, with a terrain-following layer found from the bilinear ground
    /// height. Fills <paramref name="coefficients"/> (<see cref="CoefficientsPerProbe"/>); zeros without data.
    /// </summary>
    public void Sample(Vector3 position, Span<float> coefficients)
    {
        coefficients[..CoefficientsPerProbe].Clear();
        if (!HasData)
            return;
        var grid = Grid;
        var c = _coefficients!;
        var gx = Math.Clamp((position.X - grid.Origin.X) / grid.Spacing.X, 0f, grid.CountX - 1);
        var gz = Math.Clamp((position.Z - grid.Origin.Z) / grid.Spacing.Z, 0f, grid.CountZ - 1);
        int x0 = (int)gx, z0 = (int)gz;
        int x1 = Math.Min(x0 + 1, grid.CountX - 1), z1 = Math.Min(z0 + 1, grid.CountZ - 1);
        float fx = gx - x0, fz = gz - z0;
        float layer;
        if (grid.Layout == ProbeLayout.TerrainFollowing)
        {
            var g = _ground!;
            var ground = (g[grid.Column(x0, z0)] * (1 - fx) + g[grid.Column(x1, z0)] * fx) * (1 - fz) +
                         (g[grid.Column(x0, z1)] * (1 - fx) + g[grid.Column(x1, z1)] * fx) * fz;
            layer = grid.LayerCoordinate(position.Y - ground);
        }
        else
        {
            layer = Math.Clamp((position.Y - grid.Origin.Y) / grid.Spacing.Y, 0f, grid.CountY - 1);
        }

        var y0 = (int)layer;
        var y1 = Math.Min(y0 + 1, grid.CountY - 1);
        var fy = layer - y0;
        for (var corner = 0; corner < 8; corner++)
        {
            var w = ((corner & 1) != 0 ? fx : 1 - fx) * ((corner & 2) != 0 ? fy : 1 - fy) * ((corner & 4) != 0 ? fz : 1 - fz);
            if (w <= 0f)
                continue;
            var p = grid.Index((corner & 1) != 0 ? x1 : x0, (corner & 2) != 0 ? y1 : y0, (corner & 4) != 0 ? z1 : z0) * CoefficientsPerProbe;
            for (var k = 0; k < CoefficientsPerProbe; k++)
                coefficients[k] += c[p + k] * w;
        }
    }

    /// <summary>
    /// The probes as a <see cref="Texture3D"/> (built once and kept; null without data). See the class remarks for the
    /// layout. Half floats, trilinear, clamp to edge.
    /// </summary>
    public Texture3D? ToTexture()
    {
        if (_texture is not null)
            return _texture;
        if (!HasData)
            return null;
        var grid = Grid;
        int w = grid.CountX, layers = grid.CountY, h = layers * ProbeGrid.Slabs, d = grid.CountZ;
        var texels = new Half[(long)w * h * d * 4];
        var c = _coefficients!;
        for (var z = 0; z < d; z++)
        {
            for (var y = 0; y < layers; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var probe = grid.Index(x, y, z) * CoefficientsPerProbe;
                    for (var slab = 0; slab < 4; slab++)
                    {
                        var t = (((long)z * h + slab * layers + y) * w + x) * 4;
                        for (var k = 0; k < 4; k++)
                            texels[t + k] = (Half)c[probe + slab * 4 + k];
                    }

                    var g = (((long)z * h + 4 * layers + y) * w + x) * 4;
                    texels[g] = (Half)_ground![grid.Column(x, z)];
                }
            }
        }

        return _texture = new Texture3D(w, h, d, Texture3DFormat.Rgba16F, MemoryMarshal.AsBytes(texels.AsSpan()));
    }

    /// <summary>
    /// Writes the coefficients to <paramref name="mresPath"/>'s <c>.probes</c> file (same name, LFS) and the resource to
    /// <paramref name="mresPath"/>; returns the resource's UID.
    /// </summary>
    public string Save(string mresPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(mresPath);
        if (!HasData)
            throw new InvalidOperationException("Nothing to save: the probe data has no coefficients.");
        var full = AssetDatabase.Current.ToAbsolutePath(mresPath);
        DataFile = Path.GetFileNameWithoutExtension(full) + ".probes";
        WriteDataFile(Path.Combine(Path.GetDirectoryName(full)!, DataFile));
        return ResourceSaver.Save(this, mresPath);
    }

    /// <summary>Writes the binary coefficients file (magic, counts, ground float32, coefficients float16).</summary>
    public void WriteDataFile(string path)
    {
        EnsureLoaded();
        var grid = Grid;
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(header, FileMagic);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], grid.CountX);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], grid.CountY);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], grid.CountZ);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], CoefficientsPerProbe);
        stream.Write(header);
        Span<byte> four = stackalloc byte[4];
        foreach (var g in _ground!)
        {
            BinaryPrimitives.WriteSingleLittleEndian(four, g);
            stream.Write(four);
        }

        Span<byte> two = stackalloc byte[2];
        foreach (var c in _coefficients!)
        {
            BinaryPrimitives.WriteHalfLittleEndian(two, (Half)c);
            stream.Write(two);
        }

        ResourceSaver.WriteAtomically(path, stream.ToArray());
    }

    /// <summary>Reads the coefficients file (throws <see cref="InvalidDataException"/> when it does not match the grid).</summary>
    public void ReadDataFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var grid = Grid;
        if (bytes.Length < 24 || BinaryPrimitives.ReadUInt64LittleEndian(bytes) != FileMagic)
            throw new InvalidDataException($"'{path}' is not a light probe file.");
        int x = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)), y = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        int z = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16)), per = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20));
        if (x != grid.CountX || y != grid.CountY || z != grid.CountZ || per != CoefficientsPerProbe)
            throw new InvalidDataException($"'{path}' holds {x}×{y}×{z} probes, the resource {grid.CountX}×{grid.CountY}×{grid.CountZ}.");
        var columns = grid.ColumnCount;
        var count = grid.ProbeCount * CoefficientsPerProbe;
        if (bytes.Length != 24 + columns * 4 + count * 2)
            throw new InvalidDataException($"'{path}' has {bytes.Length} bytes, expected {24 + columns * 4 + count * 2}.");
        var ground = new float[columns];
        for (var i = 0; i < columns; i++)
            ground[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(24 + i * 4));
        var coefficients = new float[count];
        var offset = 24 + columns * 4;
        for (var i = 0; i < count; i++)
            coefficients[i] = (float)BinaryPrimitives.ReadHalfLittleEndian(bytes.AsSpan(offset + i * 2));
        _ground = ground;
        _coefficients = coefficients;
        _texture = null;
    }

    // Reads DataFile next to the resource the first time the data is needed (once: a missing file is logged once).
    private void EnsureLoaded()
    {
        if (_coefficients is not null || _loadFailed)
            return;
        _loadFailed = true;
        if (string.IsNullOrEmpty(DataFile) || ResourcePath is not { } resourcePath)
            return;
        var folder = Path.GetDirectoryName(AssetDatabase.Current.ToAbsolutePath(resourcePath));
        var path = folder is null ? DataFile : Path.Combine(folder, DataFile);
        try
        {
            ReadDataFile(path);
            _loadFailed = false;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Warning($"[Probes] '{resourcePath}': cannot read '{DataFile}' ({e.Message}); the volume renders without probes.");
        }
    }
}
