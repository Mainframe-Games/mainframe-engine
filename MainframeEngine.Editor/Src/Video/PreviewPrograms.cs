using MainframeEngine.Video.Media.Preview;
using MainframeEngine.Video.Timelines;
using MainframeEngine.Video.Timelines.Export;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// What a <see cref="VideoPreview"/> plays: the frame size and rate, and the ffmpeg arguments of each decode. Positions
/// are the program's own seconds (source time for a file, timeline time for a timeline). Called on the main thread only:
/// the preview builds every decode's arguments there and hands them to its threads.
/// </summary>
public interface IPreviewProgram
{
    int Width { get; }

    int Height { get; }

    double FrameRate { get; }

    /// <summary>Raw RGBA frames of <c>[start, end)</c>.</summary>
    IReadOnlyList<string> VideoArguments(double start, double end);

    /// <summary>Interleaved stereo float samples of <c>[start, end)</c> at <paramref name="sampleRate"/>.</summary>
    IReadOnlyList<string> AudioArguments(double start, double end, int sampleRate);

    /// <summary>The one frame at <paramref name="time"/>.</summary>
    IReadOnlyList<string> FrameArguments(double time);
}

/// <summary>One media file (the Source viewer): <see cref="PreviewDecoding"/>'s arguments at its preview size.</summary>
public sealed class SourceProgram : IPreviewProgram
{
    private readonly string _path;

    public SourceProgram(string path, int sourceWidth, int sourceHeight, double sourceFrameRate)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        HasVideo = sourceWidth > 0 && sourceHeight > 0;
        (Width, Height) = HasVideo ? PreviewDecoding.PreviewSize(sourceWidth, sourceHeight) : (16, 16);
        FrameRate = PreviewDecoding.PreviewFrameRate(sourceFrameRate);
    }

    /// <summary>False for an audio file: frames are black (no video decode starts).</summary>
    public bool HasVideo { get; }

    public int Width { get; }

    public int Height { get; }

    public double FrameRate { get; }

    public IReadOnlyList<string> VideoArguments(double start, double end) => HasVideo
        ? PreviewDecoding.VideoArguments(_path, start, end, Width, Height, FrameRate)
        : BlackArguments(end - start);

    public IReadOnlyList<string> AudioArguments(double start, double end, int sampleRate) => PreviewDecoding.AudioArguments(_path, start, end, sampleRate);

    public IReadOnlyList<string> FrameArguments(double time) => HasVideo
        ? PreviewDecoding.FrameArguments(_path, time, Width, Height)
        : [.. BlackArguments(1 / FrameRate).SkipLast(1), "-frames:v", "1", "-"];

    private IReadOnlyList<string> BlackArguments(double seconds) =>
        ["-f", "lavfi", "-i", $"color=c=black:s={Width}x{Height}:r={FrameRate:0.###}:d={seconds:0.######}", "-pix_fmt", "rgba", "-f", "rawvideo", "-"];
}

/// <summary>
/// A timeline (the Timeline viewer): its export graph at preview size (<see cref="TimelinePreviewArguments"/>), so the
/// preview is what export writes. <see cref="Timeline"/> is the snapshot the next decode reads; the tab sets it on every
/// edit, and the preview restarts when the version it is given changes.
/// </summary>
public sealed class TimelineProgram : IPreviewProgram
{
    private readonly Func<MediaRef, string> _resolve;
    private readonly FrameRate _rate;

    public TimelineProgram(Timeline timeline, Func<MediaRef, string> resolve)
    {
        Timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        Settings = timeline.Settings;
        (Width, Height) = TimelineGraphBuilder.PreviewSize(Settings);
        _rate = TimelineGraphBuilder.PreviewRate(Settings);
    }

    /// <summary>The settings the program was made for: a timeline with other settings needs a new program (and preview).</summary>
    public TimelineSettings Settings { get; }

    public Timeline Timeline { get; set; }

    public int Width { get; }

    public int Height { get; }

    public double FrameRate => _rate.Fps;

    public IReadOnlyList<string> VideoArguments(double start, double end) =>
        TimelinePreviewArguments.Video(Timeline, start, end, Width, Height, _rate, _resolve);

    public IReadOnlyList<string> AudioArguments(double start, double end, int sampleRate) =>
        TimelinePreviewArguments.Audio(Timeline, start, end, sampleRate, _resolve);

    public IReadOnlyList<string> FrameArguments(double time) =>
        TimelinePreviewArguments.Frame(Timeline, time, Width, Height, _rate, _resolve);
}
