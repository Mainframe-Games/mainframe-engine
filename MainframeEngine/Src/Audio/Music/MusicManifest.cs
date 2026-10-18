using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MainframeEngine.Audio.Music;

/// <summary>
/// A baked adaptive song (<c>.mmusic</c>): the grid, parameters, sections in frames with their stems, layers, curves and
/// transitions. The editor writes it (Bake) and games load it; the editor's live preview builds one in memory from the
/// song. Unknown fields are kept (<see cref="Extra"/>). See docs/design/future/adaptive-music.md.
/// </summary>
public sealed class MusicManifest
{
    public int Format { get; set; } = MusicManifestFormat.Current;

    /// <summary>The manifest's own UID (<c>mus_…</c>), kept across bakes.</summary>
    public string Uid { get; set; } = "";

    /// <summary>The song it was baked from (<c>sng_…</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Song { get; set; }

    /// <summary>The stems' sample rate; every frame count here is at this rate.</summary>
    public int SampleRate { get; set; } = 48000;

    /// <summary>Quarter notes per minute.</summary>
    public double Tempo { get; set; } = 120;

    /// <summary>[beats per bar, beat unit].</summary>
    public int[] TimeSignature { get; set; } = [4, 4];

    /// <summary>The section <c>Play</c> starts at.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Start { get; set; }

    public List<MusicParameterInfo> Parameters { get; set; } = [];

    public List<MusicManifestLayer> Layers { get; set; } = [];

    public List<MusicManifestSection> Sections { get; set; } = [];

    public List<MusicCurveInfo> Curves { get; set; } = [];

    public List<MusicTransitionInfo> Transitions { get; set; } = [];

    /// <summary>Bake bookkeeping (editor version, date, input hashes); games ignore it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Bake { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A layer: a group of tracks baked to one stem per section.</summary>
public sealed class MusicManifestLayer
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MusicLayerFilter Filter { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A section in frames, with its markers and stems.</summary>
public sealed class MusicManifestSection
{
    /// <summary>The key of a stinger's single mixed stem in <see cref="Stems"/>.</summary>
    public const string MixStem = "mix";

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public MusicSectionKind Kind { get; set; }

    /// <summary>Whole bars.</summary>
    public int Bars { get; set; }

    /// <summary><see cref="MusicGrid.LengthFrames"/> of <see cref="Bars"/>; every body stem has exactly this length.</summary>
    public long LengthFrames { get; set; }

    public bool Loop { get; set; }

    /// <summary>The section that follows when it does not loop (null: the music ends).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Then { get; set; }

    /// <summary>Stingers: where they land (beat when unset).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MusicQuantize? Quantize { get; set; }

    /// <summary>Stingers: beats of the stinger that play before the quantum.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float PickupBeats { get; set; }

    public List<MusicManifestMarker> Markers { get; set; } = [];

    /// <summary>Stems by layer id (<see cref="MixStem"/> for a stinger); null: the layer is silent here.</summary>
    public Dictionary<string, MusicStemSet?> Stems { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A named point in a section (frames from its start); <see cref="Exit"/> markers are transition points.</summary>
public sealed class MusicManifestMarker
{
    public string Name { get; set; } = "";

    public long Frame { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Exit { get; set; }

    /// <summary>
    /// A tail point's baked tails by layer id ("cut at the marker + tail"; null entry: that layer is silent there): an
    /// exit at this marker fires them like a section-end exit fires the section's tails. Null: not a tail point.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, MusicStemRef?>? Tails { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A layer's stems in one section: the body (the section's exact length) and its tail.</summary>
public sealed class MusicStemSet
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MusicStemRef? Body { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MusicStemRef? Tail { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A stem file: by UID, with its project path as a fallback.</summary>
public sealed class MusicStemRef
{
    public string Uid { get; set; } = "";

    public string Path { get; set; } = "";

    /// <summary>Frames in the file (tails; bodies have the section's length).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Frames { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The result of reading a manifest: newer formats are read best-effort, with a notice.</summary>
public readonly record struct MusicManifestReadResult(MusicManifest Manifest, string? Notice);

/// <summary>
/// Reads and writes <c>.mmusic</c>: versioned JSON (<see cref="Current"/>, upgraded through <see cref="Migrations"/> like
/// <c>project.mfproj</c>) through the source-generated <see cref="MusicJsonContext"/>, so trimmed and AOT games can load it.
/// </summary>
public static class MusicManifestFormat
{
    public const string Extension = ".mmusic";

    /// <summary>The format this engine writes.</summary>
    public const int Current = 1;

    /// <summary>Upgrade steps (none yet: format 1 is the first).</summary>
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

    public static MusicManifestReadResult Load(string path) => Parse(File.ReadAllBytes(path), System.IO.Path.GetFileName(path));

    /// <summary>Parses (and upgrades) a manifest. Throws <see cref="InvalidDataException"/> for files that are not one.</summary>
    public static MusicManifestReadResult Parse(ReadOnlySpan<byte> json, string source)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: ReadOptions) as JsonObject
                   ?? throw new InvalidDataException($"'{source}' is not adaptive music (the top level is not an object).");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"'{source}' is not valid JSON: {e.Message}", e);
        }

        var format = root["format"] is JsonValue v && v.TryGetValue<int>(out var f) ? f : 1;
        string? notice = null;
        if (format > Current)
            notice = $"'{source}' was baked by a newer editor (format {format}; this engine reads {Current}). It plays what this engine understands.";
        else if (format < Current)
            ProjectSettingsFormat.Upgrade(root, format, Current, Migrations, source);

        MusicManifest manifest;
        try
        {
            manifest = root.Deserialize(MusicJsonContext.Default.MusicManifest) ?? throw new InvalidDataException($"'{source}' is empty.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"'{source}' is not valid adaptive music: {e.Message}", e);
        }

        Normalize(manifest);
        return new MusicManifestReadResult(manifest, notice);
    }

    /// <summary>The manifest as UTF-8 JSON (trailing newline).</summary>
    public static byte[] Write(MusicManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            JsonSerializer.Serialize(writer, manifest, MusicJsonContext.Default.MusicManifest);
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    // Missing collections from hand-written files become empty.
    private static void Normalize(MusicManifest m)
    {
        m.Parameters ??= [];
        m.Layers ??= [];
        m.Sections ??= [];
        m.Curves ??= [];
        m.Transitions ??= [];
        if (m.TimeSignature is not { Length: 2 })
            m.TimeSignature = [4, 4];
        foreach (var section in m.Sections)
        {
            section.Markers ??= [];
            section.Stems ??= [];
        }

        foreach (var curve in m.Curves)
            curve.Points ??= [];
        foreach (var transition in m.Transitions)
            transition.From ??= [];
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MusicManifest))]
internal sealed partial class MusicJsonContext : JsonSerializerContext;
