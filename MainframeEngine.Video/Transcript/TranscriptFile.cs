using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Transcript;

/// <summary>
/// A media file's transcript, cached beside it as <c>&lt;media&gt;.mftranscript.json</c> (G20d): made once per recording,
/// shared by every timeline clip of it. Keyed by the media's content hash, so a replaced file is transcribed again.
/// </summary>
/// <param name="Version">The format version (<see cref="TranscriptSerializer.CurrentVersion"/>).</param>
/// <param name="SourceHash">The media's <c>SourceHash</c> when it was transcribed.</param>
/// <param name="Model">The Whisper model key that made it (<c>base.en</c>).</param>
/// <param name="Words">The words in source time.</param>
public sealed record TranscriptFile(int Version, string SourceHash, string Model, IReadOnlyList<Word> Words);

/// <summary>Reads and writes <see cref="TranscriptFile"/>s.</summary>
public static class TranscriptSerializer
{
    public const int CurrentVersion = 1;

    /// <summary>The sidecar's suffix after the media file's own name.</summary>
    public const string Suffix = ".mftranscript.json";

    /// <summary>The transcript sidecar of <paramref name="mediaPath"/>.</summary>
    public static string PathFor(string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        return mediaPath + Suffix;
    }

    public static string Serialize(TranscriptFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return JsonSerializer.Serialize(file with { Version = CurrentVersion }, TranscriptJsonContext.Default.TranscriptFile);
    }

    /// <summary>The transcript in <paramref name="json"/>, or null when it is not one this version reads (a newer or broken file).</summary>
    public static TranscriptFile? Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            var file = JsonSerializer.Deserialize(json, TranscriptJsonContext.Default.TranscriptFile);
            return file is { Version: >= 1 and <= CurrentVersion, Words: not null, SourceHash: not null } ? file : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The cached words of the media at <paramref name="mediaPath"/> when its sidecar was made from <paramref name="sourceHash"/>.</summary>
    public static async Task<TranscriptFile?> LoadAsync(string mediaPath, string sourceHash, CancellationToken cancellationToken)
    {
        var path = PathFor(mediaPath);
        if (!File.Exists(path))
            return null;
        var file = Deserialize(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        return file is not null && string.Equals(file.SourceHash, sourceHash, StringComparison.Ordinal) ? file : null;
    }

    /// <summary>Writes the sidecar through a <c>.tmp</c> file, so a crash never leaves half of one.</summary>
    public static async Task SaveAsync(string mediaPath, TranscriptFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var path = PathFor(mediaPath);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, Serialize(file), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TranscriptFile))]
internal sealed partial class TranscriptJsonContext : JsonSerializerContext
{
}
