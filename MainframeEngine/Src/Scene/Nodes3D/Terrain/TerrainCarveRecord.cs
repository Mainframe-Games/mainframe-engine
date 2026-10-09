using System.Buffers.Binary;
using System.Text.Json;

namespace MainframeEngine;

/// <summary>
/// What a <see cref="River3D"/> carve replaced: the original heights of the vertex rectangle it wrote, so a re-carve
/// starts from the uncarved ground and an uncarve restores it bit for bit. Kept by the <see cref="TerrainData"/> (in
/// memory, and as <c>carve_&lt;id&gt;.json</c> in its layer folder when the terrain saves).
/// </summary>
/// <param name="Rect">The vertex rectangle.</param>
/// <param name="Heights">Its stored heights before the carve, row by row (quantised, so writing them back is exact).</param>
internal sealed record TerrainCarveRecord(Rect2I Rect, float[] Heights)
{
    /// <summary>The record's file name for carve <paramref name="id"/>.</summary>
    public static string FileName(string id) => $"carve_{id}.json";

    /// <summary>True when <paramref name="id"/> is a usable carve id (letters, digits, '-' and '_', 1–64 characters).</summary>
    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64)
            return false;
        foreach (var c in id)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                return false;
        return true;
    }

    /// <summary>Writes the record as JSON: the rectangle and the heights as the terrain's 16-bit values (base64, little-endian).</summary>
    public void Write(Stream stream, TerrainData data)
    {
        var encoded = new byte[Heights.Length * 2];
        for (var i = 0; i < Heights.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(i * 2), data.EncodeHeight(Heights[i]));
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("x", Rect.Position.X);
        writer.WriteNumber("y", Rect.Position.Y);
        writer.WriteNumber("width", Rect.Size.X);
        writer.WriteNumber("height", Rect.Size.Y);
        writer.WriteString("heights", Convert.ToBase64String(encoded));
        writer.WriteEndObject();
    }

    /// <summary>Reads a record written by <see cref="Write"/>; throws <see cref="InvalidDataException"/> when it does not fit the terrain.</summary>
    public static TerrainCarveRecord Read(Stream stream, TerrainData data)
    {
        try
        {
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var rect = new Rect2I(root.GetProperty("x").GetInt32(), root.GetProperty("y").GetInt32(),
                root.GetProperty("width").GetInt32(), root.GetProperty("height").GetInt32());
            var encoded = Convert.FromBase64String(root.GetProperty("heights").GetString() ?? "");
            var size = data.VerticesPerSide;
            if (rect.Position.X < 0 || rect.Position.Y < 0 || rect.Size.X <= 0 || rect.Size.Y <= 0 ||
                rect.End.X > size || rect.End.Y > size || encoded.Length != rect.Area * 2)
                throw new InvalidDataException($"Carve record rectangle {rect} does not fit the {size}×{size} height grid.");
            var heights = new float[rect.Area];
            for (var i = 0; i < heights.Length; i++)
                heights[i] = data.DecodeHeight(BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(i * 2)));
            return new TerrainCarveRecord(rect, heights);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException($"Unreadable carve record: {e.Message}", e);
        }
    }
}
