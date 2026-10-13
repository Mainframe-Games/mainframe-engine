using System.Text.Json;
using System.Text.Json.Serialization;

namespace MainframeEngine.Video.Capture;

internal sealed record CaptureSpanFile(double? Start, double? End, string? Label);

internal sealed record CaptureMarkerFile(double? Time, string? Label);

/// <summary>
/// The on-disk shape of a <c>.mfcap.json</c> file. Every member is nullable so that a missing key
/// is handled by <see cref="CaptureSidecarSerializer"/> rather than by the JSON source generator.
/// See <see cref="CaptureSidecar"/> for the reader's view.
/// </summary>
internal sealed record CaptureSidecarFile(
    string? Format,
    int? Version,
    string? Video,
    string? EngineVersion,
    string? Game,
    string? Scene,
    string? Mode,
    double? FrameRate,
    int? Width,
    int? Height,
    double? DurationSeconds,
    int? DroppedFrames,
    List<CaptureSpanFile>? Input,
    List<CaptureSpanFile>? Loading,
    List<CaptureSpanFile>? Paused,
    List<CaptureSpanFile>? Keep,
    List<CaptureSpanFile>? Skip,
    List<CaptureMarkerFile>? Markers);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(CaptureSidecarFile))]
internal sealed partial class CaptureSidecarJsonContext : JsonSerializerContext
{
}
