using MainframeEngine.Video.Media;
using MainframeEngine.Video.Projects;

namespace MainframeEngine.Video.Timelines;

/// <summary>Builds the <see cref="MediaRef"/> a clip stores: probe and hash once, when a file is placed.</summary>
public static class MediaRefs
{
    /// <summary>Video, audio and image extensions video mode places on a timeline (images from phase 2).</summary>
    public static IReadOnlySet<string> VideoExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".webm", ".m4v" };

    public static IReadOnlySet<string> AudioExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".wav", ".ogg", ".mp3", ".flac", ".m4a", ".aac" };

    /// <summary>Probes and hashes <paramref name="fullPath"/>; the clip stores <paramref name="storedPath"/> and <paramref name="uid"/>.</summary>
    /// <exception cref="MediaProbeException">ffprobe could not read it, or it has neither video nor audio.</exception>
    public static async Task<MediaRef> ProbeAsync(FfmpegLocation ffmpeg, string fullPath, string storedPath, string uid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        var info = await new MediaProbe(ffmpeg).ProbeAsync(fullPath, cancellationToken, requireVideo: false, still: TimelineFactory.IsImagePath(fullPath))
            .ConfigureAwait(false);
        var hash = await SourceHash.ComputeAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var media = From(info, storedPath, uid, hash);
        return TimelineFactory.IsImagePath(fullPath) ? TimelineFactory.ImageMedia(media) : media; // a still: any length fits
    }

    /// <summary>The reference for probed media.</summary>
    public static MediaRef From(MediaInfo info, string storedPath, string uid, string hash)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new MediaRef(uid ?? "", storedPath, hash, info.DurationSeconds, info.Width, info.Height, info.FrameRate, info.HasVideo, info.HasAudio);
    }
}
