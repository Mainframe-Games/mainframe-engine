using System.Collections.Immutable;
using System.Text.Json.Serialization;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Timelines;

/// <summary>
/// The output frame of a timeline: size, rate and audio rate. Width and height are even (H.264's 4:2:0 needs it).
/// </summary>
/// <param name="Width">Output width in pixels.</param>
/// <param name="Height">Output height in pixels.</param>
/// <param name="FrameRate">Frames per second; every edit snaps to it.</param>
/// <param name="SampleRate">Audio sample rate of the mix.</param>
/// <param name="Background">The colour behind every track, <c>#RRGGBB</c>.</param>
public sealed record TimelineSettings(int Width, int Height, FrameRate FrameRate, int SampleRate = 48000, string Background = "#000000")
{
    /// <summary>Throws when the settings cannot be rendered.</summary>
    public void Validate()
    {
        if (Width < 2 || Height < 2 || Width % 2 != 0 || Height % 2 != 0 || Width > 8192 || Height > 8192)
            throw new ArgumentException($"A timeline is 2 to 8192 pixels on each side, in even numbers; {Width}×{Height} is not.");
        if (SampleRate is < 8000 or > 192000)
            throw new ArgumentException($"{SampleRate} Hz is not a usable audio sample rate.");
        if (!TryParseColour(Background, out _))
            throw new ArgumentException($"The background '{Background}' is not a #RRGGBB colour.");
    }

    /// <summary><c>#RRGGBB</c> as ffmpeg's <c>0xRRGGBB</c>.</summary>
    internal string BackgroundForFfmpeg() => TryParseColour(Background, out var hex) ? "0x" + hex : "0x000000";

    private static bool TryParseColour(string? text, out string hex)
    {
        hex = "";
        if (text is not { Length: 7 } || text[0] != '#')
            return false;
        foreach (var c in text.AsSpan(1))
            if (!char.IsAsciiHexDigit(c))
                return false;
        hex = text[1..].ToUpperInvariant();
        return true;
    }
}

/// <summary>What a track holds.</summary>
public enum TrackKind
{
    [JsonStringEnumMemberName("video")]
    Video,

    [JsonStringEnumMemberName("audio")]
    Audio,
}

/// <summary>
/// A lane of the timeline. Video tracks stack in their order in <see cref="Timeline.Tracks"/> (the first video track is at
/// the bottom); audio tracks all mix.
/// </summary>
/// <param name="Id">Stable identity.</param>
/// <param name="Kind">Video or audio.</param>
/// <param name="Name">The header's label (V1, A2, or the user's).</param>
/// <param name="Enabled">Off: a video track is not drawn (the eye in the header); an audio track is silent.</param>
/// <param name="Locked">No edit touches a locked track, nor a clip linked to one of its clips.</param>
/// <param name="Muted">An audio track that is not heard.</param>
/// <param name="Solo">When any audio track is soloed, only soloed tracks are heard.</param>
/// <param name="GainDb">Audio gain of the whole track, in decibels.</param>
public sealed record Track(
    string Id,
    TrackKind Kind,
    string Name,
    bool Enabled = true,
    bool Locked = false,
    bool Muted = false,
    bool Solo = false,
    double GainDb = 0);

/// <summary>
/// The media file a clip shows, with what was probed when it was placed: a clip never needs to probe again to lay itself
/// out.
/// </summary>
/// <param name="Uid">The file's asset UID in the game project (moves in the media pool keep it), or empty.</param>
/// <param name="Path">The file, project-relative (<c>Assets/Video/Media/a.mp4</c>) or absolute; resolved by the caller.</param>
/// <param name="Hash">The file's <see cref="Projects.SourceHash"/>, keying caches and transcripts.</param>
/// <param name="Duration">Seconds; the right edge of each clip's segment partition.</param>
/// <param name="Width">Frame width in pixels (0 for audio).</param>
/// <param name="Height">Frame height in pixels (0 for audio).</param>
/// <param name="FrameRate">The file's frames per second (0 for audio).</param>
/// <param name="HasVideo">The file has a video stream.</param>
/// <param name="HasAudio">The file has an audio stream.</param>
public sealed record MediaRef(
    string Uid,
    string Path,
    string Hash,
    double Duration,
    int Width,
    int Height,
    double FrameRate,
    bool HasVideo,
    bool HasAudio);

/// <summary>How a clip's frame fills the timeline's before <see cref="ClipTransform.Scale"/>.</summary>
public enum FrameFit
{
    /// <summary>The whole frame shows, letterboxed or pillarboxed.</summary>
    [JsonStringEnumMemberName("fit")]
    Fit,

    /// <summary>The timeline's frame is covered, cropping the overflow (a 16:9 clip on a 9:16 timeline loses its sides).</summary>
    [JsonStringEnumMemberName("fill")]
    Fill,

    /// <summary>Both axes scale to the timeline's, distorting the image.</summary>
    [JsonStringEnumMemberName("stretch")]
    Stretch,
}

/// <summary>Pixels cut from each edge of the source frame before it is fitted.</summary>
public readonly record struct CropRect(double Left, double Top, double Right, double Bottom)
{
    [JsonIgnore]
    public bool IsEmpty => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
}

/// <summary>How a video clip sits in the timeline's frame (the Inspector's reframe).</summary>
/// <param name="Fit">Fit, fill or stretch into the frame.</param>
/// <param name="Scale">Applied after <paramref name="Fit"/>; 1 = as fitted.</param>
/// <param name="X">Pixels right of centre, in timeline pixels.</param>
/// <param name="Y">Pixels below centre, in timeline pixels.</param>
/// <param name="Crop">Source pixels removed before fitting.</param>
/// <param name="Opacity">0 (invisible) to 1.</param>
public sealed record ClipTransform(
    FrameFit Fit = FrameFit.Fill,
    double Scale = 1,
    double X = 0,
    double Y = 0,
    CropRect Crop = default,
    double Opacity = 1)
{
    /// <summary>Fill, unscaled, centred, uncropped, opaque.</summary>
    public static ClipTransform Default { get; } = new();
}

/// <summary>
/// A window <c>[SourceIn, SourceOut)</c> of a media file placed at <see cref="Start"/> on a track. Its
/// <see cref="Segments"/> partition the whole file (Cutback's cut list, now per clip): only kept time inside the window
/// plays, back to back, so the clip lasts the kept time ÷ <see cref="Speed"/> (<see cref="TimelineLayout.Length"/>).
/// </summary>
/// <param name="Id">Stable identity.</param>
/// <param name="TrackId">The track it is on.</param>
/// <param name="Media">The file it shows.</param>
/// <param name="Start">Timeline seconds, on a frame.</param>
/// <param name="SourceIn">Source seconds where the window starts.</param>
/// <param name="SourceOut">Source seconds where the window ends.</param>
/// <param name="Segments">The partition of <c>[0, Media.Duration]</c>: kept and cut regions.</param>
/// <param name="Transform">Reframe (video clips).</param>
/// <param name="Speed">Playback speed; 1 = normal.</param>
/// <param name="LinkGroup">Clips sharing a group select, move, trim and cut together (a recording's picture and sound).</param>
/// <param name="Enabled">Off: the clip keeps its place but is not seen or heard.</param>
public sealed record Clip(
    string Id,
    string TrackId,
    MediaRef Media,
    double Start,
    double SourceIn,
    double SourceOut,
    ImmutableArray<Segment> Segments,
    ClipTransform Transform,
    double Speed = 1,
    string? LinkGroup = null,
    bool Enabled = true);

/// <summary>A named point on the timeline (snapping target).</summary>
public sealed record Marker(string Id, double Time, string Name);

/// <summary>
/// One edit: settings, tracks, clips and markers. Immutable; <see cref="TimelineEdits"/> returns a new timeline per edit,
/// or the same instance when nothing changed.
/// </summary>
/// <param name="Uid">The file's asset UID (<c>vtl_…</c>).</param>
/// <param name="Settings">Output size and rate.</param>
/// <param name="Tracks">Video and audio tracks; video stacking order is their order here.</param>
/// <param name="Clips">Every clip, in no particular order.</param>
/// <param name="Markers">Named points.</param>
/// <param name="InPoint">The export and playback range start, or null for 0.</param>
/// <param name="OutPoint">The range end, or null for the end of the last clip.</param>
public sealed record Timeline(
    string Uid,
    TimelineSettings Settings,
    ImmutableArray<Track> Tracks,
    ImmutableArray<Clip> Clips,
    ImmutableArray<Marker> Markers,
    double? InPoint = null,
    double? OutPoint = null)
{
    [JsonIgnore]
    public FrameRate Rate => Settings.FrameRate;

    /// <summary>The track with this id, or null.</summary>
    public Track? FindTrack(string id)
    {
        foreach (var track in Tracks)
            if (track.Id == id)
                return track;
        return null;
    }

    /// <summary>The clip with this id, or null.</summary>
    public Clip? FindClip(string id)
    {
        foreach (var clip in Clips)
            if (clip.Id == id)
                return clip;
        return null;
    }

    /// <summary>The clips on a track, by start.</summary>
    public IReadOnlyList<Clip> ClipsOn(string trackId) =>
        [.. Clips.Where(c => c.TrackId == trackId).OrderBy(c => c.Start)];

    /// <summary>The tracks of one kind, in stacking order.</summary>
    public IReadOnlyList<Track> TracksOf(TrackKind kind) => [.. Tracks.Where(t => t.Kind == kind)];

    /// <summary>The clips linked with <paramref name="clip"/>, itself included.</summary>
    public IReadOnlyList<Clip> LinkedWith(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.LinkGroup is null ? [clip] : [.. Clips.Where(c => c.LinkGroup == clip.LinkGroup)];
    }
}
