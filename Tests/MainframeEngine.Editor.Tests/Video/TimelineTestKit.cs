using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Timelines;
using Timeline = MainframeEngine.Video.Timelines.Timeline;

namespace MainframeEngine.Editor.Tests.Video;

/// <summary>Shared timelines and media for the editor's timeline tests: a 1920×1080 timeline at 30 fps and 10 s recordings.</summary>
internal static class TimelineTestKit
{
    public static readonly FrameRate Rate30 = new(30, 1);

    public static MediaRef Recording(string path = "Assets/Video/Media/rec.mp4", double duration = 10) =>
        new("ast_" + Math.Abs(path.GetHashCode(StringComparison.Ordinal)).ToString("x12", System.Globalization.CultureInfo.InvariantCulture)[..12],
            path, "hash-" + path, duration, 1920, 1080, 30, HasVideo: true, HasAudio: true);

    public static MediaRef Music(string path = "Assets/Video/Media/music.ogg", double duration = 30) =>
        new("", path, "hash-" + path, duration, 0, 0, 0, HasVideo: false, HasAudio: true);

    public static Timeline Landscape() => TimelineFactory.New(new TimelineSettings(1920, 1080, Rate30));

    public static string V1(Timeline t) => t.TracksOf(TrackKind.Video)[0].Id;

    public static string V2(Timeline t) => t.TracksOf(TrackKind.Video)[1].Id;

    public static string A1(Timeline t) => t.TracksOf(TrackKind.Audio)[0].Id;

    public static string A2(Timeline t) => t.TracksOf(TrackKind.Audio)[1].Id;

    /// <summary>Appends <c>[sourceIn, sourceOut)</c> of a recording on V1/A1.</summary>
    public static Timeline Append(Timeline t, double sourceIn, double sourceOut, string path = "Assets/Video/Media/rec.mp4") =>
        TimelineEdits.Append(t, Recording(path), sourceIn, sourceOut, V1(t), A1(t));

    /// <summary>A document over <paramref name="timeline"/>, saved nowhere until a test saves it.</summary>
    public static TimelineDocument Document(Timeline timeline, string directory) =>
        new(timeline, Path.Combine(directory, "Assets", "Video", "Timelines", "Edit.mftimeline"), directory);

    /// <summary>The canvas point in the middle of <paramref name="trackId"/>'s lane at <paramref name="time"/>.</summary>
    public static System.Numerics.Vector2 At(TimelineViewState view, Timeline timeline, string trackId, double time)
    {
        var lane = view.Lanes(timeline).First(l => l.TrackId == trackId);
        return new System.Numerics.Vector2((float)view.TimeToX(time), view.LaneTop(lane) + lane.Height / 2);
    }
}
