using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MainframeEngine.Serialization;

namespace MainframeEngine;

/// <summary>The header line of a <c>.msave</c> file (see <see cref="SaveFile"/>).</summary>
internal sealed class SaveHeader
{
    public string? Kind;
    public int Format;
    public string Slot = "";
    public int Version;
    public DateTimeOffset SavedUtc;
    public double PlayTimeSeconds;
    public string? Description;
    public double Progress;
    public string? Device;
    public string? Game;
    public string? GameVersion;
    public string? Engine;
    public SaveCompression Compression;
    public long PayloadBytes = -1;
    public string Sha256 = "";
    public string? Thumbnail;

    /// <summary>Null when every field a format-<see cref="SaveFile.CurrentFormat"/> header needs is present and valid.</summary>
    public string? Validate()
    {
        if (Kind != SaveFile.Kind)
            return $"not a save file (kind '{Kind}')";
        if (Format < 1)
            return "the header has no valid 'format'";
        if (!SaveServer.IsValidSlotName(Slot))
            return "the header has no valid 'slot'";
        if (Version < 1)
            return "the header has no valid 'version'";
        if (SavedUtc == default)
            return "the header has no 'savedUtc'";
        if (PayloadBytes < 0)
            return "the header has no 'payloadBytes'";
        if (Sha256.Length != 64)
            return "the header has no valid 'sha256'";
        return null;
    }

    public SaveSlotInfo ToInfo(string slot, string? thumbnailPath, long sizeBytes) =>
        new(slot, Format, Version, SavedUtc, TimeSpan.FromSeconds(PlayTimeSeconds), Description, Progress, Device, GameVersion,
            thumbnailPath, sizeBytes, IsReadable: true);
}

/// <summary>
/// The <c>.msave</c> container: line 1 is a compact JSON header (at most <see cref="MaxHeaderBytes"/>, ending with
/// <c>\n</c>), the rest is the payload JSON, optionally gzip. <c>payloadBytes</c> and <c>sha256</c> describe the stored
/// (possibly compressed) bytes, so a checksum never needs a re-serialization and a listing reads the first line only.
/// </summary>
internal static class SaveFile
{
    /// <summary>The header's <c>kind</c>.</summary>
    public const string Kind = "mainframe-save";

    /// <summary>The container format this engine writes; newer files load as <see cref="LoadStatus.TooNew"/>.</summary>
    public const int CurrentFormat = 1;

    /// <summary>The longest header line, without its <c>\n</c>.</summary>
    public const int MaxHeaderBytes = 64 * 1024;

    /// <summary>The first read of a header: most headers are a few hundred bytes.</summary>
    private const int FirstReadBytes = 4096;

    public const string Extension = ".msave";

    /// <summary>A lowercase hex SHA-256 of <paramref name="bytes"/>.</summary>
    public static string Hash(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexStringLower(hash);
    }

    public static byte[] Compress(ReadOnlySpan<byte> payload)
    {
        using var output = new MemoryStream(payload.Length / 4 + 64);
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(payload);
        return output.ToArray();
    }

    /// <summary>Gunzips <paramref name="stored"/>; throws <see cref="InvalidDataException"/> past <paramref name="maxBytes"/>.</summary>
    public static byte[] Decompress(ReadOnlyMemory<byte> stored, long maxBytes)
    {
        using var input = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(stored, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(stored.ToArray(), writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maxBytes)
                throw new InvalidDataException($"the payload is over the {maxBytes} byte limit when decompressed");
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    /// <summary>The whole file: the header line, <c>\n</c>, then <paramref name="stored"/>.</summary>
    public static byte[] Build(SaveHeader header, ReadOnlySpan<byte> stored)
    {
        var line = WriteHeader(header);
        var file = new byte[line.Length + 1 + stored.Length];
        line.CopyTo(file, 0);
        file[line.Length] = (byte)'\n';
        stored.CopyTo(file.AsSpan(line.Length + 1));
        return file;
    }

    /// <summary>The compact header JSON (no line break; control characters in strings are escaped).</summary>
    public static byte[] WriteHeader(SaveHeader h)
    {
        using var stream = new MemoryStream(512);
        using (var w = new Utf8JsonWriter(stream, SceneFormat.CompactWriteOptions))
        {
            w.WriteStartObject();
            w.WriteString("kind", Kind);
            w.WriteNumber("format", h.Format);
            w.WriteString("slot", h.Slot);
            w.WriteNumber("version", h.Version);
            w.WriteString("savedUtc", h.SavedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            w.WriteNumber("playTimeSeconds", Math.Round(h.PlayTimeSeconds, 3));
            WriteOptional(w, "description", h.Description);
            w.WriteNumber("progress", h.Progress);
            WriteOptional(w, "device", h.Device);
            WriteOptional(w, "game", h.Game);
            WriteOptional(w, "gameVersion", h.GameVersion);
            WriteOptional(w, "engine", h.Engine);
            w.WriteString("compression", h.Compression == SaveCompression.Gzip ? "gzip" : "none");
            w.WriteNumber("payloadBytes", h.PayloadBytes);
            w.WriteString("sha256", h.Sha256);
            WriteOptional(w, "thumbnail", h.Thumbnail);
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteOptional(Utf8JsonWriter w, string name, string? value)
    {
        if (value is not null)
            w.WriteString(name, value);
    }

    /// <summary>
    /// Reads the header line of <paramref name="stream"/> with one small buffered read (more only for long headers), never
    /// touching the payload. Returns the line without its <c>\n</c>, or null with <paramref name="error"/>.
    /// </summary>
    public static byte[]? ReadHeaderLine(Stream stream, out string? error)
    {
        var buffer = new byte[FirstReadBytes];
        var filled = 0;
        while (true)
        {
            var read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read == 0)
            {
                error = filled == 0 ? "the file is empty" : "the header line is truncated";
                return null;
            }

            var newline = buffer.AsSpan(filled, read).IndexOf((byte)'\n');
            if (newline >= 0)
            {
                error = null;
                return buffer.AsSpan(0, filled + newline).ToArray();
            }

            filled += read;
            if (filled > MaxHeaderBytes)
            {
                error = $"the header line is longer than {MaxHeaderBytes} bytes";
                return null;
            }

            if (filled == buffer.Length)
                Array.Resize(ref buffer, Math.Min(buffer.Length * 4, MaxHeaderBytes + 1));
        }
    }

    /// <summary>Parses the header line of a whole <paramref name="file"/>; <paramref name="payloadOffset"/> is where the payload starts.</summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> file, out SaveHeader header, out int payloadOffset, out string? error)
    {
        var newline = file[..Math.Min(file.Length, MaxHeaderBytes + 1)].IndexOf((byte)'\n');
        if (newline < 0)
        {
            header = new SaveHeader();
            payloadOffset = 0;
            error = file.Length > MaxHeaderBytes ? $"the header line is longer than {MaxHeaderBytes} bytes" : "the header line is truncated";
            return false;
        }

        payloadOffset = newline + 1;
        return TryParseHeaderLine(file[..newline], out header, out error);
    }

    /// <summary>
    /// Parses a header line leniently: unknown keys are skipped (later formats may add some), and the caller checks
    /// <see cref="SaveHeader.Kind"/>, <see cref="SaveHeader.Format"/> and <see cref="SaveHeader.Validate"/>. False only
    /// when the line is not a JSON object or a known key has the wrong type.
    /// </summary>
    public static bool TryParseHeaderLine(ReadOnlySpan<byte> line, out SaveHeader header, out string? error)
    {
        header = new SaveHeader();
        try
        {
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { MaxDepth = 16 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                error = "the header is not a JSON object";
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString();
                reader.Read();
                switch (key)
                {
                    case "kind": header.Kind = String(ref reader, key); break;
                    case "format": header.Format = reader.GetInt32(); break;
                    case "slot": header.Slot = String(ref reader, key) ?? ""; break;
                    case "version": header.Version = reader.GetInt32(); break;
                    case "savedUtc": header.SavedUtc = reader.GetDateTimeOffset(); break;
                    case "playTimeSeconds": header.PlayTimeSeconds = Finite(ref reader, key, min: 0); break;
                    case "description": header.Description = String(ref reader, key); break;
                    case "progress": header.Progress = Finite(ref reader, key, min: double.MinValue); break;
                    case "device": header.Device = String(ref reader, key); break;
                    case "game": header.Game = String(ref reader, key); break;
                    case "gameVersion": header.GameVersion = String(ref reader, key); break;
                    case "engine": header.Engine = String(ref reader, key); break;
                    case "compression":
                        header.Compression = String(ref reader, key) switch
                        {
                            "none" => SaveCompression.None,
                            "gzip" => SaveCompression.Gzip,
                            var other => throw new InvalidDataException($"unknown compression '{other}'"),
                        };
                        break;
                    case "payloadBytes": header.PayloadBytes = reader.GetInt64(); break;
                    case "sha256": header.Sha256 = String(ref reader, key) ?? ""; break;
                    case "thumbnail": header.Thumbnail = String(ref reader, key); break;
                    default: reader.Skip(); break;
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject)
            {
                error = "the header is not a JSON object";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or InvalidDataException)
        {
            error = $"the header is damaged: {e.Message}";
            return false;
        }
    }

    private static string? String(ref Utf8JsonReader reader, string? key) => reader.TokenType switch
    {
        JsonTokenType.Null => null,
        JsonTokenType.String => reader.GetString(),
        _ => throw new InvalidDataException($"'{key}' must be a string"),
    };

    private static double Finite(ref Utf8JsonReader reader, string? key, double min)
    {
        var value = reader.GetDouble();
        if (!double.IsFinite(value) || value < min)
            throw new InvalidDataException($"'{key}' is out of range");
        return value;
    }

    /// <summary>
    /// Checks the stored payload of <paramref name="file"/> against its header (length, SHA-256) and decompresses it.
    /// Throws <see cref="InvalidDataException"/> describing the damage.
    /// </summary>
    public static ReadOnlyMemory<byte> OpenPayload(byte[] file, int payloadOffset, SaveHeader header, long maxBytes)
    {
        var stored = file.AsMemory(payloadOffset);
        if (stored.Length != header.PayloadBytes)
        {
            throw new InvalidDataException(stored.Length < header.PayloadBytes
                ? $"the file is truncated ({stored.Length} of {header.PayloadBytes} payload bytes)"
                : $"the file has {stored.Length - header.PayloadBytes} bytes after the payload");
        }

        if (!string.Equals(Hash(stored.Span), header.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("the payload checksum does not match (the file is damaged)");
        if (header.Compression == SaveCompression.Gzip)
            return Decompress(stored, maxBytes);
        if (stored.Length > maxBytes)
            throw new InvalidDataException($"the payload is over the {maxBytes} byte limit");
        return stored;
    }
}
