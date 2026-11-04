using System.Globalization;

namespace MainframeEngine.Video.Timelines.Export;

/// <summary>
/// ffmpeg arguments of the editor's timeline preview (docs/design/future/video-nle.md, Preview): the export graph at
/// preview size (<see cref="TimelineGraphBuilder.BuildPreview"/>), raw RGBA frames or float stereo samples on stdout.
/// The graph goes inline (<c>-filter_complex</c>): a preview range holds the clips from the playhead on, and every
/// ffmpeg version takes it; the export's script file needs the version check this path avoids.
/// </summary>
public static class TimelinePreviewArguments
{
    /// <summary>Raw RGBA frames of <c>[start, end)</c>.</summary>
    public static IReadOnlyList<string> Video(Timeline timeline, double start, double end, int width, int height, FrameRate rate, Func<MediaRef, string>? resolvePath)
    {
        var graph = TimelineGraphBuilder.BuildPreview(timeline, start, end, width, height, rate, audio: false, resolvePath);
        return [.. graph.InputArguments, "-filter_complex", graph.Graph, "-map", TimelineGraphBuilder.VideoOutput, "-an", "-pix_fmt", "rgba", "-f", "rawvideo", "-"];
    }

    /// <summary>Interleaved stereo float samples of <c>[start, end)</c> at <paramref name="sampleRate"/>.</summary>
    public static IReadOnlyList<string> Audio(Timeline timeline, double start, double end, int sampleRate, Func<MediaRef, string>? resolvePath)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var atRate = timeline with { Settings = timeline.Settings with { SampleRate = sampleRate } };
        var graph = TimelineGraphBuilder.BuildPreview(atRate, start, end, 2, 2, timeline.Rate, audio: true, resolvePath);
        return [.. graph.InputArguments, "-filter_complex", graph.Graph, "-map", TimelineGraphBuilder.AudioOutput, "-vn",
            "-ac", "2", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "-"];
    }

    /// <summary>The one frame at <paramref name="time"/>, for scrubbing while paused.</summary>
    public static IReadOnlyList<string> Frame(Timeline timeline, double time, int width, int height, FrameRate rate, Func<MediaRef, string>? resolvePath)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var start = timeline.Rate.Snap(Math.Max(0, time));
        return [.. Video(timeline, start, start + timeline.Rate.FrameDuration, width, height, rate, resolvePath).SkipLast(1), "-frames:v", "1", "-"];
    }
}
