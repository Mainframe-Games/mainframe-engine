using System.Text.Json.Serialization;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Timelines;

/// <summary>The on-disk shape of a <c>.mftimeline</c> file (version 1). The model's immutable arrays become lists here.</summary>
internal sealed record TimelineFile(
    int Version,
    string Uid,
    TimelineSettings Settings,
    List<Track> Tracks,
    List<TimelineFileClip> Clips,
    List<Marker> Markers,
    double? In,
    double? Out);

internal sealed record TimelineFileClip(
    string Id,
    string Track,
    MediaRef Media,
    double Start,
    double In,
    double Out,
    double Speed,
    string? Link,
    bool Enabled,
    ClipTransform Transform,
    List<Segment> Segments);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(JsonStringEnumConverter<SegmentOrigin>), typeof(JsonStringEnumConverter<TrackKind>), typeof(JsonStringEnumConverter<FrameFit>)])]
[JsonSerializable(typeof(TimelineFile))]
internal sealed partial class TimelineJsonContext : JsonSerializerContext
{
}
