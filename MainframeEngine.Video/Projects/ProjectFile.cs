using System.Text.Json.Serialization;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Projects;

/// <summary>
/// The on-disk shape of a <c>.mfcut</c> file. Kept separate from <see cref="VideoProject"/> so
/// the schema version is a persistence concern, not a model concern.
/// </summary>
internal sealed record ProjectFile(
    int Version,
    SourceInfo Source,
    List<Segment> Segments,
    List<Word> Transcript,
    DetectionSettings Settings);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    Converters = [typeof(JsonStringEnumConverter<SegmentOrigin>)])]
[JsonSerializable(typeof(ProjectFile))]
internal sealed partial class ProjectJsonContext : JsonSerializerContext
{
}
