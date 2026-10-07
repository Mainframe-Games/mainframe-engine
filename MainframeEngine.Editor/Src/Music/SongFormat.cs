using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MainframeEngine.Editor.Music;

/// <summary>The result of reading a song: newer-format files are read best-effort and must stay read-only.</summary>
public readonly record struct SongReadResult(Song Song, bool ReadOnly, string? Notice);

/// <summary>
/// Reads and writes <c>.msong</c> files: versioned JSON (<see cref="Current"/>) upgraded through <see cref="Migrations"/>
/// like <c>project.mfproj</c> (<see cref="ProjectSettingsFormat.Upgrade"/>), serialized with the source-generated
/// <see cref="SongJsonContext"/> (no reflection), written like the project file (2-space indent, <c>\n</c>, non-ASCII
/// unescaped) so the FileSystem's reference fix-ups rewrite it in the same style.
/// </summary>
public static class SongFormat
{
    public const string Extension = ".msong";

    /// <summary>The format this editor writes.</summary>
    public const int Current = 1;

    /// <summary>Upgrade steps, ordered by <see cref="ProjectMigration.From"/> (none yet: format 1 is the first).</summary>
    public static IReadOnlyList<ProjectMigration> Migrations { get; } = [];

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static SongReadResult Load(string path) => Parse(File.ReadAllBytes(path), Path.GetFileName(path));

    /// <summary>Parses (and upgrades) a song. Throws <see cref="InvalidDataException"/> for files that are not songs.</summary>
    public static SongReadResult Parse(ReadOnlySpan<byte> json, string source) => Parse(json, source, Current, Migrations);

    public static SongReadResult Parse(ReadOnlySpan<byte> json, string source, int currentFormat, IReadOnlyList<ProjectMigration> migrations)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: ReadOptions) as JsonObject
                   ?? throw new InvalidDataException($"'{source}' is not a song (the top level is not an object).");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"'{source}' is not valid JSON: {e.Message}", e);
        }

        var format = root["format"] is JsonValue v && v.TryGetValue<int>(out var f) ? f : 1;
        string? notice = null;
        if (format > currentFormat)
            notice = $"'{source}' was saved by a newer editor (song format {format}; this one writes {currentFormat}). It opens read-only.";
        else if (format < currentFormat)
            ProjectSettingsFormat.Upgrade(root, format, currentFormat, migrations, source);

        Song song;
        try
        {
            song = root.Deserialize(SongJsonContext.Default.Song) ?? throw new InvalidDataException($"'{source}' is empty.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"'{source}' is not a valid song: {e.Message}", e);
        }

        song.Format = Math.Max(format, currentFormat);
        Normalize(song);
        return new SongReadResult(song, notice is not null, notice);
    }

    /// <summary>The song as UTF-8 JSON (trailing newline).</summary>
    public static byte[] Write(Song song)
    {
        ArgumentNullException.ThrowIfNull(song);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            JsonSerializer.Serialize(writer, song, SongJsonContext.Default.Song);
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    /// <summary>Writes atomically (temp file, then replace).</summary>
    public static void Save(Song song, string path) => AtomicFile.WriteAllBytes(path, Write(song));

    // Missing collections from hand-written files become empty; the time signature always has two parts.
    private static void Normalize(Song song)
    {
        song.Loop ??= new SongLoop();
        song.Render ??= new SongRenderSettings();
        song.Master ??= new SongMaster();
        song.Master.Inserts ??= [];
        song.Tracks ??= [];
        if (song.TimeSignature is not { Length: 2 })
            song.TimeSignature = [4, 4];
        if (song.Ppq <= 0)
            song.Ppq = Song.DefaultPpq;
        if (!(song.Tempo > 0) || !double.IsFinite(song.Tempo))
            song.Tempo = 120;
        foreach (var track in song.Tracks)
        {
            track.Inserts ??= [];
            track.Clips ??= [];
            foreach (var clip in track.Clips)
            {
                if (clip is MidiClip midi)
                    midi.Notes ??= [];
            }
        }
    }
}

/// <summary>
/// Clips are one JSON array per track: objects with a <c>file</c> are <see cref="AudioClip"/>s, the rest
/// <see cref="MidiClip"/>s (no type discriminator, the proposal's format).
/// </summary>
public sealed class SongClipListConverter : JsonConverter<List<SongClip>>
{
    public override List<SongClip> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return [];
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected an array of clips.");
        var list = new List<SongClip>();
        var context = SongJsonContext.Default;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            using var element = JsonDocument.ParseValue(ref reader);
            var root = element.RootElement;
            SongClip? clip = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("file", out _)
                ? root.Deserialize(context.AudioClip)
                : root.Deserialize(context.MidiClip);
            if (clip is not null)
                list.Add(clip);
        }

        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<SongClip> value, JsonSerializerOptions options)
    {
        var context = SongJsonContext.Default;
        writer.WriteStartArray();
        foreach (var clip in value)
        {
            switch (clip)
            {
                case AudioClip audio:
                    JsonSerializer.Serialize(writer, audio, context.AudioClip);
                    break;
                case MidiClip midi:
                    JsonSerializer.Serialize(writer, midi, context.MidiClip);
                    break;
            }
        }

        writer.WriteEndArray();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Song))]
[JsonSerializable(typeof(MidiClip))]
[JsonSerializable(typeof(AudioClip))]
internal sealed partial class SongJsonContext : JsonSerializerContext;
