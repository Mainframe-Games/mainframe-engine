using System.Text.Json;
using System.Text.Json.Serialization;

namespace MainframeEngine;

/// <summary>What the sidecar says about the recording itself (spec §1.7).</summary>
internal sealed record CaptureInfo(
    string Video,
    string EngineVersion,
    string Game,
    string Scene,
    bool Offline,
    int FrameRate,
    int Width,
    int Height,
    double DurationSeconds,
    long DroppedFrames);

/// <summary>
/// Writes <c>&lt;video&gt;.mfcap.json</c>, version 1 of the Mainframe capture sidecar that Cutback (and
/// later the editor's Clips tab) reads to cut idle play, loading and pauses. The engine owns the format;
/// <c>Tests/…/Recording/Fixtures/sample.mfcap.json</c> is byte-identical to Cutback's copy, so a change
/// here fails both test suites until both readers agree.
/// </summary>
internal static class CaptureSidecarWriter
{
    public const string FormatName = "mainframe-capture";
    public const int Version = 1;
    public const string FileSuffix = ".mfcap.json";

    public static string PathFor(string videoPath) => videoPath + FileSuffix;

    public static string ToJson(CaptureInfo info, CaptureSpans spans)
    {
        var file = new SidecarFile(
            FormatName, Version, info.Video, info.EngineVersion, info.Game, info.Scene,
            info.Offline ? "offline" : "live", info.FrameRate, info.Width, info.Height, info.DurationSeconds, info.DroppedFrames,
            [.. spans.Input.Select(Span)], [.. spans.Loading.Select(Span)], [.. spans.Paused.Select(Span)],
            [.. spans.Keep.Select(Span)], [.. spans.Skip.Select(Span)],
            [.. spans.Markers.Select(m => new MarkerFile(m.Time, m.Label))]);
        return JsonSerializer.Serialize(file, SidecarJsonContext.Default.SidecarFile);
    }

    /// <summary>Writes next to <paramref name="videoPath"/> via a temp file and a move, so a reader never sees half a file.</summary>
    public static void Write(string videoPath, CaptureInfo info, CaptureSpans spans)
    {
        var path = PathFor(videoPath);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson(info, spans));
        File.Move(temp, path, overwrite: true);
    }

    private static SpanFile Span(CaptureSpan s) => new(s.Start, s.End, s.Label);

    internal sealed record SpanFile(
        double Start,
        double End,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label);

    internal sealed record MarkerFile(double Time, string Label);

    internal sealed record SidecarFile(
        string Format, int Version, string Video, string EngineVersion, string Game, string Scene, string Mode,
        int FrameRate, int Width, int Height, double DurationSeconds, long DroppedFrames,
        List<SpanFile> Input, List<SpanFile> Loading, List<SpanFile> Paused, List<SpanFile> Keep, List<SpanFile> Skip,
        List<MarkerFile> Markers);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CaptureSidecarWriter.SidecarFile))]
internal sealed partial class SidecarJsonContext : JsonSerializerContext
{
}
