using MainframeEngine.Video.Models;
using MainframeEngine.Video.Timelines;

namespace MainframeEngine.Video.Tests.Timelines;

/// <summary>Shared timelines and media for the timeline tests: a 1080×1920 timeline at 30 fps and a 60 s 1080p recording.</summary>
internal static class TimelineTestData
{
    public static readonly FrameRate Rate30 = new(30, 1);

    public static MediaRef Recording(string path = "Assets/Video/Media/rec.mp4", double duration = 60) =>
        new("ast_0123456789ab", path, "hash-" + path, duration, 1920, 1080, 60, HasVideo: true, HasAudio: true);

    public static MediaRef Music(double duration = 120) =>
        new("aud_0123456789ab", "Assets/Video/Media/music.ogg", "hash-music", duration, 0, 0, 0, HasVideo: false, HasAudio: true);

    public static Timeline Vertical() => TimelineFactory.New(new TimelineSettings(1080, 1920, Rate30));

    public static string V1(Timeline t) => t.TracksOf(TrackKind.Video)[0].Id;

    public static string V2(Timeline t) => t.TracksOf(TrackKind.Video)[1].Id;

    public static string A1(Timeline t) => t.TracksOf(TrackKind.Audio)[0].Id;

    public static string A2(Timeline t) => t.TracksOf(TrackKind.Audio)[1].Id;

    /// <summary>Appends <c>[sourceIn, sourceOut)</c> of a recording on V1/A1.</summary>
    public static Timeline Append(Timeline t, double sourceIn, double sourceOut, string path = "Assets/Video/Media/rec.mp4") =>
        TimelineEdits.Append(t, Recording(path), sourceIn, sourceOut, V1(t), A1(t));

    public static Clip On(Timeline t, string trackId, int index) => t.ClipsOn(trackId)[index];

    public static double End(Timeline t, Clip c) => TimelineLayout.End(c, t.Rate);

    /// <summary>Cuts <c>[from, to)</c> of a clip's source (manual), through <see cref="TimelineEdits.SetClipCuts"/>.</summary>
    public static Timeline Cut(Timeline t, Clip clip, double from, double to) =>
        TimelineEdits.SetClipCuts(t, clip.Id, list => list.SetRange(from, to, enabled: false));

    public static void Partitioned(Timeline t)
    {
        foreach (var clip in t.Clips)
            SegmentList.ValidatePartition(clip.Segments, clip.Media.Duration);
    }
}
