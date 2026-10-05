using System.Buffers;
using System.Text.Json;

namespace MainframeEngine.Serialization;

/// <summary>
/// The text layout of scene and resource files: objects and arrays indented by two spaces, one member per line, except
/// short arrays of scalars (vectors, colours, transforms, group names) and resource references, which stay on one line
/// (<c>[0, 1.5, 0]</c>, <c>{ "res": "BoxMesh_twalp" }</c>). Tokens are copied byte for byte; only whitespace changes.
/// </summary>
internal static class SceneJsonLayout
{
    /// <summary>Arrays of at most this many scalars are written on one line (a 4×4 matrix fits).</summary>
    public const int MaxInlineArrayLength = 16;

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Lays out <paramref name="json"/> (one JSON value, any whitespace) in the scene file style.</summary>
    public static byte[] Format(ReadOnlySpan<byte> json)
    {
        var output = new ArrayBufferWriter<byte>(json.Length + json.Length / 2);
        var reader = new Utf8JsonReader(json, ReaderOptions);
        var depth = 0;
        var first = true;     // no member written yet in the current container
        var afterName = false; // a property name was just written: the value follows on the same line

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    BeginValue(output, ref first, depth, afterName: false);
                    WriteString(output, reader.ValueSpan);
                    output.Write(": "u8);
                    afterName = true;
                    continue;

                case JsonTokenType.StartObject or JsonTokenType.StartArray:
                    BeginValue(output, ref first, depth, afterName);
                    afterName = false;
                    if (reader.TokenType == JsonTokenType.StartArray ? TryWriteInlineArray(output, ref reader) : TryWriteReference(output, ref reader))
                        continue;
                    output.Write(reader.TokenType == JsonTokenType.StartObject ? "{"u8 : "["u8);
                    if (IsEmptyContainer(reader))
                    {
                        reader.Read();
                        output.Write(reader.TokenType == JsonTokenType.EndObject ? "}"u8 : "]"u8);
                        continue;
                    }

                    depth++;
                    first = true;
                    continue;

                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    depth--;
                    NewLine(output, depth);
                    output.Write(reader.TokenType == JsonTokenType.EndObject ? "}"u8 : "]"u8);
                    first = false;
                    continue;

                default:
                    BeginValue(output, ref first, depth, afterName);
                    afterName = false;
                    WriteScalar(output, ref reader);
                    continue;
            }
        }

        return output.WrittenSpan.ToArray();
    }

    // Separates a value from the previous member and starts its line (unless it follows its property name).
    private static void BeginValue(ArrayBufferWriter<byte> output, ref bool first, int depth, bool afterName)
    {
        if (afterName)
            return;
        if (depth > 0)
        {
            if (!first)
                output.Write(","u8);
            NewLine(output, depth);
        }

        first = false;
    }

    private static void NewLine(ArrayBufferWriter<byte> output, int depth)
    {
        var span = output.GetSpan(1 + depth * 2);
        span[0] = (byte)'\n';
        span.Slice(1, depth * 2).Fill((byte)' ');
        output.Advance(1 + depth * 2);
    }

    // The reader is a struct: a copy looks ahead without moving the original.
    private static bool IsEmptyContainer(Utf8JsonReader reader) =>
        reader.Read() && reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray;

    private static bool TryWriteInlineArray(ArrayBufferWriter<byte> output, ref Utf8JsonReader reader)
    {
        var scan = reader;
        var count = 0;
        while (scan.Read() && scan.TokenType != JsonTokenType.EndArray)
        {
            if (scan.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray || ++count > MaxInlineArrayLength)
                return false;
        }

        output.Write("["u8);
        for (var i = 0; i < count; i++)
        {
            reader.Read();
            if (i > 0)
                output.Write(", "u8);
            WriteScalar(output, ref reader);
        }

        reader.Read(); // EndArray
        output.Write("]"u8);
        return true;
    }

    // A resource reference: an object whose only member is "res" with a string key.
    private static bool TryWriteReference(ArrayBufferWriter<byte> output, ref Utf8JsonReader reader)
    {
        var scan = reader;
        if (!scan.Read() || scan.TokenType != JsonTokenType.PropertyName || !scan.ValueTextEquals("res"u8)
            || !scan.Read() || scan.TokenType != JsonTokenType.String
            || !scan.Read() || scan.TokenType != JsonTokenType.EndObject)
            return false;

        reader.Read();
        output.Write("{ "u8);
        WriteString(output, reader.ValueSpan);
        output.Write(": "u8);
        reader.Read();
        WriteString(output, reader.ValueSpan);
        reader.Read(); // EndObject
        output.Write(" }"u8);
        return true;
    }

    private static void WriteScalar(ArrayBufferWriter<byte> output, ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                WriteString(output, reader.ValueSpan);
                break;
            case JsonTokenType.Number:
                output.Write(reader.ValueSpan);
                break;
            case JsonTokenType.True:
                output.Write("true"u8);
                break;
            case JsonTokenType.False:
                output.Write("false"u8);
                break;
            default:
                output.Write("null"u8);
                break;
        }
    }

    // ValueSpan of a string or property name is its raw (still escaped) content between the quotes.
    private static void WriteString(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> raw)
    {
        output.Write("\""u8);
        output.Write(raw);
        output.Write("\""u8);
    }
}
