using System.Collections.Immutable;
using System.Security.Cryptography;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Timelines;

/// <summary>New timelines, tracks, clips and ids.</summary>
public static class TimelineFactory
{
    /// <summary>
    /// The UID prefix of a timeline file. The engine's AssetDatabase reads <c>.mftimeline</c> files as self-describing
    /// assets (<c>AssetUid.TimelinePrefix</c>); the two must agree.
    /// </summary>
    public const string UidPrefix = "vtl";

    /// <summary>A new empty timeline with V1, V2, A1 and A2.</summary>
    public static Timeline New(TimelineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        return new Timeline(NewUid(), settings,
            [NewTrack(TrackKind.Video, "V1"), NewTrack(TrackKind.Video, "V2"), NewTrack(TrackKind.Audio, "A1"), NewTrack(TrackKind.Audio, "A2")],
            [], []);
    }

    /// <summary>
    /// Settings that match <paramref name="media"/>: its size (made even) and rate, for a timeline built from one recording.
    /// </summary>
    public static TimelineSettings SettingsFor(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        var width = Math.Max(2, media.Width - media.Width % 2);
        var height = Math.Max(2, media.Height - media.Height % 2);
        var rate = media.FrameRate > 0 ? FrameRate.FromFps(media.FrameRate) : new FrameRate(30, 1);
        return media.HasVideo ? new TimelineSettings(width, height, rate) : new TimelineSettings(1920, 1080, rate);
    }

    /// <summary>A timeline file UID: <c>vtl_</c> and 12 lowercase hex digits, the engine's asset UID format.</summary>
    public static string NewUid() => $"{UidPrefix}_{RandomNumberGenerator.GetHexString(12, lowercase: true)}";

    /// <summary>A new id for a track, clip, marker or link group.</summary>
    public static string NewId() => Segment.NewId();

    public static Track NewTrack(TrackKind kind, string name) => new(NewId(), kind, name);

    /// <summary>A clip showing <c>[sourceIn, sourceOut)</c> of <paramref name="media"/> uncut, at <paramref name="start"/>.</summary>
    public static Clip NewClip(MediaRef media, string trackId, double start, double sourceIn, double sourceOut, string? linkGroup = null)
    {
        ArgumentNullException.ThrowIfNull(media);
        return new Clip(NewId(), trackId, media, start, sourceIn, sourceOut,
            [Segment.Create(0, media.Duration, enabled: true, SegmentOrigin.Auto)], ClipTransform.Default, LinkGroup: linkGroup);
    }

    internal static Timeline With(this Timeline timeline, IEnumerable<Clip> clips) => timeline with { Clips = [.. clips] };
}
