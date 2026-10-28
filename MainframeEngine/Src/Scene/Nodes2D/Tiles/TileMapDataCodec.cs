using System.Buffers.Binary;

namespace MainframeEngine;

/// <summary>
/// The saved form of a <see cref="TileMapLayer"/>'s cells: one string per non-empty chunk, sorted by chunk (y, then x),
/// each the base64 of a little-endian <c>u16</c> format number followed by 12-byte cell records in cell order —
/// <c>i16 x, i16 y, u16 sourceId, u16 atlasX, u16 atlasY, u16 alternative</c>, the record of Godot 4.3's
/// <c>tile_map_data</c>, so a Godot importer only has to split by chunk. Same cells, same bytes.
/// </summary>
internal static class TileMapDataCodec
{
    /// <summary>The format this engine writes.</summary>
    public const ushort Format = 1;

    public const int RecordSize = 12;

    public static string[] Encode(TileMapLayer layer)
    {
        var chunks = layer.Chunks;
        if (chunks.Count == 0)
            return [];
        // Saved order is (y, x) whatever the draw order.
        var sorted = new List<TileChunk>(chunks);
        sorted.Sort(static (a, b) => a.Coords.Y != b.Coords.Y ? a.Coords.Y.CompareTo(b.Coords.Y) : a.Coords.X.CompareTo(b.Coords.X));
        var result = new string[sorted.Count];
        for (var c = 0; c < sorted.Count; c++)
        {
            var chunk = sorted[c];
            var bytes = new byte[2 + chunk.Used * RecordSize];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, Format);
            var at = 2;
            var origin = chunk.Origin;
            for (var i = 0; i < chunk.Cells.Length; i++)
            {
                var cell = chunk.Cells[i];
                if (cell.IsEmpty)
                    continue;
                WriteRecord(bytes.AsSpan(at, RecordSize), origin.X + i % chunk.Size, origin.Y + i / chunk.Size, cell);
                at += RecordSize;
            }

            result[c] = Convert.ToBase64String(bytes);
        }

        return result;
    }

    public static void Decode(TileMapLayer layer, string[]? chunks)
    {
        layer.Clear();
        if (chunks is null)
            return;
        foreach (var text in chunks)
        {
            if (string.IsNullOrEmpty(text))
                continue;
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
                Log.Error($"[TileMapLayer] '{layer.Name}': a TileMapData entry is not base64; skipped.");
                continue;
            }

            DecodeRecords(layer, bytes);
        }
    }

    /// <summary>
    /// Adds the cells of one blob (a chunk line, or a whole Godot <c>tile_map_data</c> array: format 0 and 1 share the
    /// record layout) to <paramref name="layer"/>.
    /// </summary>
    public static void DecodeRecords(TileMapLayer layer, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2)
            return;
        var format = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        if (format > Format)
        {
            Log.Error($"[TileMapLayer] '{layer.Name}': tile map data format {format} is newer than this engine's ({Format}); skipped.");
            return;
        }

        var records = bytes[2..];
        if (records.Length % RecordSize != 0)
            Log.Warning($"[TileMapLayer] '{layer.Name}': tile map data has {records.Length % RecordSize} trailing bytes; ignored.");
        for (var at = 0; at + RecordSize <= records.Length; at += RecordSize)
        {
            var r = records.Slice(at, RecordSize);
            var x = BinaryPrimitives.ReadInt16LittleEndian(r);
            var y = BinaryPrimitives.ReadInt16LittleEndian(r[2..]);
            var source = BinaryPrimitives.ReadUInt16LittleEndian(r[4..]);
            var cell = new TileCell(source == 0xFFFF ? (short)-1 : (short)source, BinaryPrimitives.ReadUInt16LittleEndian(r[6..]),
                BinaryPrimitives.ReadUInt16LittleEndian(r[8..]), BinaryPrimitives.ReadUInt16LittleEndian(r[10..]));
            if (!cell.IsEmpty)
                layer.SetCellData(new Vector2I(x, y), cell);
        }
    }

    public static void WriteRecord(Span<byte> r, int x, int y, TileCell cell)
    {
        BinaryPrimitives.WriteInt16LittleEndian(r, (short)x);
        BinaryPrimitives.WriteInt16LittleEndian(r[2..], (short)y);
        BinaryPrimitives.WriteUInt16LittleEndian(r[4..], (ushort)cell.SourceId);
        BinaryPrimitives.WriteUInt16LittleEndian(r[6..], cell.AtlasX);
        BinaryPrimitives.WriteUInt16LittleEndian(r[8..], cell.AtlasY);
        BinaryPrimitives.WriteUInt16LittleEndian(r[10..], cell.Alternative);
    }
}
