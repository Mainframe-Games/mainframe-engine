using System.Buffers.Binary;
using System.Globalization;

namespace MainframeEngine;

/// <summary>
/// The baked state of a <see cref="NavigationGrid2D"/> (inline in the scene or a <c>.mres</c>): the region it was baked
/// for, the solid cells and the weight scales as compact strings, so a large grid stays a few lines of JSON. Solid cells
/// are <c>rle1:</c> + base64 of LEB128 run lengths alternating walkable and solid runs (walkable first), in row-major order;
/// weights are <c>rle1:</c> + base64 of (skip, run, float32 little-endian) triples for the cells whose scale is not 1.
/// The codec is plain code (no reflection): trimming and AOT are fine.
/// </summary>
[EditorIcon("grid-3x3", Family = EditorIconFamily.Resource)]
public sealed class NavigationGridData : Resource
{
    private const string Prefix = "rle1:";

    /// <summary>The cell region this data was baked for (the grid's <see cref="NavigationGrid2D.Region"/>).</summary>
    [Export] public Rect2I Region { get; set; }

    /// <summary>The solid cells (RLE, see the type's summary); empty: every cell walkable.</summary>
    [Export] public string Solid { get; set; } = "";

    /// <summary>The cells whose weight scale is not 1 (RLE, see the type's summary); empty: all 1.</summary>
    [Export] public string Weights { get; set; } = "";

    /// <summary>Where the cells came from.</summary>
    [Export] public NavigationGridSource BakedFrom { get; set; }

    /// <summary>A hash of the static bodies the physics bake read (hex), to tell when the bake is out of date; empty otherwise.</summary>
    [Export] public string SourceHash { get; set; } = "";

    /// <summary>The number of solid cells (counted at encode time, for status lines).</summary>
    [Export] public int SolidCount { get; set; }

    /// <summary>The number of weighted cells (counted at encode time).</summary>
    [Export] public int WeightedCount { get; set; }

    /// <summary>Encodes <paramref name="grid"/>'s cells (allocates; edit time or level load).</summary>
    public static NavigationGridData FromGrid(AStarGrid2D grid, NavigationGridSource bakedFrom = NavigationGridSource.Manual)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (grid.IsDirty)
            throw new InvalidOperationException("The grid's region changed: call Update() first.");
        var data = new NavigationGridData { Region = grid.Region, BakedFrom = bakedFrom };
        data.Encode(grid);
        return data;
    }

    /// <summary>Writes <paramref name="grid"/>'s cells into <see cref="Solid"/> and <see cref="Weights"/> (the region must match).</summary>
    public void Encode(AStarGrid2D grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var cells = grid.Width * grid.Height;
        var bytes = new List<byte>(64);
        var current = false;
        var run = 0;
        for (var i = 0; i < cells; i++)
        {
            var solid = grid.IsSolid(i);
            if (solid != current)
            {
                WriteVarint(bytes, run);
                current = solid;
                run = 0;
            }

            run++;
        }

        if (run > 0 && (current || bytes.Count > 0))
            WriteVarint(bytes, run);
        Solid = grid.SolidCount == 0 ? "" : Prefix + Convert.ToBase64String([.. bytes]);
        SolidCount = grid.SolidCount;

        bytes.Clear();
        Span<byte> f = stackalloc byte[4];
        if (grid.HasWeights && grid.WeightedCount > 0)
        {
            var skip = 0;
            var i = 0;
            while (i < cells)
            {
                var w = grid.WeightAt(i);
                if (w == 1f)
                {
                    skip++;
                    i++;
                    continue;
                }

                var length = 1;
                while (i + length < cells && grid.WeightAt(i + length) == w)
                    length++;
                WriteVarint(bytes, skip);
                WriteVarint(bytes, length);
                BinaryPrimitives.WriteSingleLittleEndian(f, w);
                bytes.AddRange(f);
                skip = 0;
                i += length;
            }
        }

        Weights = bytes.Count == 0 ? "" : Prefix + Convert.ToBase64String([.. bytes]);
        WeightedCount = grid.WeightedCount;
        Region = grid.Region;
    }

    /// <summary>
    /// Writes the cells into <paramref name="grid"/> (already updated to <see cref="Region"/>). On a region mismatch or a
    /// decode error the grid is left all walkable and <paramref name="error"/> says why.
    /// </summary>
    public bool TryApply(AStarGrid2D grid, out string? error)
    {
        ArgumentNullException.ThrowIfNull(grid);
        error = null;
        grid.ClearCells();
        if (grid.Region != Region)
        {
            error = $"it was baked for region {Format(Region)}, the grid is {Format(grid.Region)}";
            return false;
        }

        var cells = grid.Width * grid.Height;
        if (!TryDecodeSolid(grid, cells, out error) || !TryDecodeWeights(grid, cells, out error))
        {
            grid.ClearCells();
            return false;
        }

        grid.BumpVersion();
        return true;
    }

    private bool TryDecodeSolid(AStarGrid2D grid, int cells, out string? error)
    {
        error = null;
        if (Solid.Length == 0)
            return true;
        if (!TryPayload(Solid, out var bytes))
        {
            error = "the solid cells are not valid rle1 data";
            return false;
        }

        var position = 0;
        var cell = 0;
        var solid = false;
        while (position < bytes.Length)
        {
            if (!TryReadVarint(bytes, ref position, out var run) || run > cells - cell)
            {
                error = "the solid cells run past the region";
                return false;
            }

            if (solid)
                for (var i = 0; i < run; i++)
                    grid.SetSolid(cell + i, true);
            cell += run;
            solid = !solid;
        }

        return true;
    }

    private bool TryDecodeWeights(AStarGrid2D grid, int cells, out string? error)
    {
        error = null;
        if (Weights.Length == 0)
            return true;
        if (!TryPayload(Weights, out var bytes))
        {
            error = "the weights are not valid rle1 data";
            return false;
        }

        var position = 0;
        var cell = 0;
        while (position < bytes.Length)
        {
            if (!TryReadVarint(bytes, ref position, out var skip) || !TryReadVarint(bytes, ref position, out var run) ||
                position + 4 > bytes.Length || skip > cells - cell || run > cells - cell - skip)
            {
                error = "the weights run past the region";
                return false;
            }

            var weight = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(position, 4));
            position += 4;
            if (!(weight >= 0f) || !float.IsFinite(weight))
            {
                error = "a weight is negative or not finite";
                return false;
            }

            cell += skip;
            for (var i = 0; i < run; i++)
                grid.SetWeight(cell + i, weight);
            cell += run;
        }

        return true;
    }

    private static bool TryPayload(string text, out byte[] bytes)
    {
        bytes = [];
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        try
        {
            bytes = Convert.FromBase64String(text[Prefix.Length..]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void WriteVarint(List<byte> bytes, int value)
    {
        var v = (uint)value;
        while (v >= 0x80)
        {
            bytes.Add((byte)(v | 0x80));
            v >>= 7;
        }

        bytes.Add((byte)v);
    }

    private static bool TryReadVarint(byte[] bytes, ref int position, out int value)
    {
        value = 0;
        var shift = 0;
        while (position < bytes.Length && shift < 32)
        {
            var b = bytes[position++];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value >= 0;
            shift += 7;
        }

        return false;
    }

    private static string Format(Rect2I r) => string.Create(CultureInfo.InvariantCulture,
        $"({r.Position.X}, {r.Position.Y}, {r.Size.X}×{r.Size.Y})");
}
