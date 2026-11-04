using System.Text.Json.Serialization;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Timelines;

/// <summary>
/// The on-disk shape of a <c>.mftimeline</c> file (version 2: G20.2 added clip kinds, volume, fades, titles and
/// transitions, each with a default, so version 1 files read unchanged). The model's immutable arrays become lists here.
/// </summary>
internal sealed record TimelineFile(
    int Version,
    string Uid,
    TimelineSettings Settings,
    List<Track> Tracks,
    List<TimelineFileClip> Clips,
    List<Marker> Markers,
    double? In,
    double? Out,
    List<Transition>? Transitions = null);

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
    List<Segment> Segments,
    ClipKind Kind = ClipKind.Media,
    double VolumeDb = 0,
    double FadeIn = 0,
    double FadeOut = 0,
    TitleStyle? Title = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(JsonStringEnumConverter<SegmentOrigin>), typeof(JsonStringEnumConverter<TrackKind>), typeof(JsonStringEnumConverter<FrameFit>),
        typeof(JsonStringEnumConverter<ClipKind>), typeof(JsonStringEnumConverter<TitleAlign>), typeof(JsonStringEnumConverter<TransitionKind>)])]
[JsonSerializable(typeof(TimelineFile))]
internal sealed partial class TimelineJsonContext : JsonSerializerContext
{
}
