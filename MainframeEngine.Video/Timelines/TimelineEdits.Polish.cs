namespace MainframeEngine.Video.Timelines;

/// <summary>The phase-2 edits (G20.2): volume and fades, speed, titles and images, transitions.</summary>
public static partial class TimelineEdits
{
    /// <summary>The slowest and fastest clip speeds.</summary>
    public const double MinSpeed = 0.25, MaxSpeed = 4;

    /// <summary>A transition's default length (Premiere's: one second).</summary>
    public const double DefaultTransitionSeconds = 1;

    /// <summary>A still's default length when placed (an image; a title).</summary>
    public const double DefaultStillSeconds = 5;

    /// <summary>What a dissolve without enough handles is refused with.</summary>
    public const string NotEnoughFrames = "Not enough frames past the edit: a dissolve takes half its length from beyond each clip's end.";

    /// <summary>
    /// Sets a clip's volume (dB, −60…24) and fades (seconds, snapped to frames; together at most the clip's length). The
    /// clip alone, not its linked partners: a picture's fade is from black, its sound's from silence.
    /// </summary>
    public static Timeline SetMix(Timeline timeline, string clipId, double volumeDb, double fadeIn, double fadeOut)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.FindClip(clipId) is not { } clip || IsLocked(timeline, clip.TrackId))
            return timeline;
        var rate = timeline.Rate;
        var length = TimelineLayout.Length(clip, rate);
        var inSeconds = rate.Snap(Math.Clamp(Finite(fadeIn), 0, length));
        var outSeconds = rate.Snap(Math.Clamp(Finite(fadeOut), 0, length - inSeconds));
        var volume = Math.Clamp(Finite(volumeDb), -60, 24);
        if (volume == clip.VolumeDb && inSeconds == clip.FadeIn && outSeconds == clip.FadeOut)
            return timeline;
        return timeline.With(timeline.Clips.Replace(clip, clip with { VolumeDb = volume, FadeIn = inSeconds, FadeOut = outSeconds }));
    }

    /// <summary>
    /// Sets the speed of a clip and of its linked partners showing the same file (0.25–4): their length changes and the
    /// clips after each on its track ripple.
    /// </summary>
    public static Timeline SetSpeed(Timeline timeline, string clipId, double speed)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.FindClip(clipId) is not { } clip)
            return timeline;
        speed = Math.Clamp(Finite(speed, 1), MinSpeed, MaxSpeed);
        var members = timeline.LinkedWith(clip).Where(c => c.Media.Hash == clip.Media.Hash).ToList();
        if (members.All(c => Math.Abs(c.Speed - speed) < Epsilon) || members.Any(c => IsLocked(timeline, c.TrackId)))
            return timeline;
        var rate = timeline.Rate;
        var ids = members.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var clips = timeline.Clips.ToList();
        foreach (var member in members)
        {
            var oldEnd = TimelineLayout.End(member, rate);
            var changed = member with { Speed = speed };
            var length = TimelineLayout.Length(changed, rate);
            changed = changed with { FadeIn = Math.Min(changed.FadeIn, length), FadeOut = Math.Min(changed.FadeOut, Math.Max(0, length - changed.FadeIn)) };
            clips[clips.IndexOf(member)] = changed;
            Shift(clips, member.TrackId, oldEnd, TimelineLayout.End(changed, rate) - oldEnd, ids);
        }

        return Finish(timeline, clips);
    }

    /// <summary>
    /// Places a still (an image's media, or a title in <paramref name="title"/>) for <paramref name="seconds"/> at
    /// <paramref name="at"/> on a video track, overwriting or inserting.
    /// </summary>
    public static Timeline PlaceStill(Timeline timeline, MediaRef media, ClipKind kind, TitleStyle? title, double at, double seconds, string trackId, bool insert)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(media);
        if (kind == ClipKind.Media || (kind == ClipKind.Title) != (title is not null))
            throw new ArgumentException("A still is an image or a title (with its style).", nameof(kind));
        var length = timeline.Rate.Snap(Math.Clamp(seconds, timeline.Rate.FrameDuration, TimelineFactory.StillDuration));
        var before = timeline.Clips.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var placed = insert
            ? Insert(timeline, media, 0, length, at, trackId, null)
            : Overwrite(timeline, media, 0, length, at, trackId, null);
        if (ReferenceEquals(placed, timeline))
            return timeline;
        return placed with { Clips = [.. placed.Clips.Select(c => before.Contains(c.Id) ? c : c with { Kind = kind, Title = title, Transform = c.Transform with { Fit = FrameFit.Fit } })] };
    }

    /// <summary>Changes a title clip's look; its media follows the new style (a new raster).</summary>
    public static Timeline SetTitle(Timeline timeline, string clipId, TitleStyle style)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(style);
        if (timeline.FindClip(clipId) is not { Kind: ClipKind.Title } clip || clip.Title == style || IsLocked(timeline, clip.TrackId))
            return timeline;
        var media = TimelineFactory.TitleMedia(style, timeline.Settings);
        return timeline.With(timeline.Clips.Replace(clip, clip with { Title = style, Media = media }));
    }

    /// <summary>
    /// Auto-captions: a new video track ("Captions") on top with a caption-styled title clip per caption. Nothing when
    /// there are no captions.
    /// </summary>
    public static Timeline AddCaptions(Timeline timeline, IReadOnlyList<Caption> captions)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(captions);
        if (captions.Count == 0)
            return timeline;
        var next = AddTrack(timeline, TrackKind.Video, "Captions");
        var track = next.TracksOf(TrackKind.Video)[^1].Id;
        foreach (var caption in captions)
        {
            var style = TitleStyle.Caption(caption.Text, timeline.Settings.Height);
            next = PlaceStill(next, TimelineFactory.TitleMedia(style, timeline.Settings), ClipKind.Title, style, caption.Start,
                caption.End - caption.Start, track, insert: false);
        }

        return next;
    }

    // ── Transitions ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The clip that meets <paramref name="clip"/> at its end on its track (the next clip, touching), or null.</summary>
    public static Clip? Following(Timeline timeline, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(clip);
        var end = TimelineLayout.End(clip, timeline.Rate);
        foreach (var other in timeline.ClipsOn(clip.TrackId))
            if (other.Id != clip.Id && Math.Abs(other.Start - end) < timeline.Rate.FrameDuration / 2)
                return other;
        return null;
    }

    /// <summary>Why a transition of <paramref name="kind"/> and <paramref name="seconds"/> cannot go between the clips, or null.</summary>
    public static string? TransitionProblem(Timeline timeline, string outClipId, string inClipId, TransitionKind kind, double seconds)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.FindClip(outClipId) is not { } a || timeline.FindClip(inClipId) is not { } b || a.TrackId != b.TrackId
            || Following(timeline, a)?.Id != b.Id)
            return "A transition goes on an edit: where one clip ends and the next begins on the same track.";
        if (IsLocked(timeline, a.TrackId))
            return "The track is locked.";
        var rate = timeline.Rate;
        var half = rate.Snap(Math.Max(rate.FrameDuration, seconds / 2));
        if (half > TimelineLayout.Length(a, rate) + Epsilon || half > TimelineLayout.Length(b, rate) + Epsilon)
            return "The transition is longer than one of its clips.";
        if (kind == TransitionKind.Dissolve && (!HasHandle(a, half, after: true) || !HasHandle(b, half, after: false)))
            return NotEnoughFrames;
        return null;
    }

    /// <summary>
    /// Adds (or replaces) a transition at the edit between two clips, and the same one between their linked partners when
    /// those meet at the same edit. Refused (the same timeline) when <see cref="TransitionProblem"/> names a reason.
    /// </summary>
    public static Timeline AddTransition(Timeline timeline, string outClipId, string inClipId, TransitionKind kind, double seconds)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (TransitionProblem(timeline, outClipId, inClipId, kind, seconds) is not null)
            return timeline;
        var rate = timeline.Rate;
        var duration = 2 * rate.Snap(Math.Max(rate.FrameDuration, seconds / 2)); // centred: a whole frame on each side
        var a = timeline.FindClip(outClipId)!;
        var b = timeline.FindClip(inClipId)!;
        var pairs = new List<(Clip Out, Clip In)> { (a, b) };
        foreach (var partner in timeline.LinkedWith(a))
            if (partner.Id != a.Id && Following(timeline, partner) is { } next && timeline.LinkedWith(b).Any(c => c.Id == next.Id)
                && TransitionProblem(timeline, partner.Id, next.Id, kind, seconds) is null)
                pairs.Add((partner, next));
        var transitions = timeline.Transitions.Where(t => !pairs.Any(p => p.Out.Id == t.OutClip)).ToList();
        foreach (var (outClip, inClip) in pairs)
            transitions.Add(new Transition(TimelineFactory.NewId(), outClip.TrackId, outClip.Id, inClip.Id, kind, duration));
        return timeline with { Transitions = [.. transitions] };
    }

    /// <summary>Removes a transition (and its linked twin at the same edit).</summary>
    public static Timeline RemoveTransition(Timeline timeline, string transitionId)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.FindTransition(transitionId) is not { } transition || IsLocked(timeline, transition.TrackId))
            return timeline;
        var twins = Twins(timeline, transition);
        return timeline with { Transitions = [.. timeline.Transitions.Where(t => !twins.Contains(t.Id))] };
    }

    /// <summary>Changes a transition's kind and length (and its twin's); refused like <see cref="AddTransition"/>.</summary>
    public static Timeline SetTransition(Timeline timeline, string transitionId, TransitionKind kind, double seconds)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.FindTransition(transitionId) is not { } transition)
            return timeline;
        var rate = timeline.Rate;
        if (transition.Kind == kind && Math.Abs(transition.Duration - 2 * rate.Snap(Math.Max(rate.FrameDuration, seconds / 2))) < Epsilon)
            return timeline;
        return AddTransition(timeline, transition.OutClip, transition.InClip, kind, seconds);
    }

    // The transition and its linked twin(s) at the same edit.
    private static HashSet<string> Twins(Timeline timeline, Transition transition)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { transition.Id };
        if (timeline.FindClip(transition.OutClip) is not { } a)
            return ids;
        var partners = timeline.LinkedWith(a).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var t in timeline.Transitions)
            if (partners.Contains(t.OutClip))
                ids.Add(t.Id);
        return ids;
    }

    /// <summary>Whether a transition still sits on an edit of its clips (else edits drop it).</summary>
    internal static bool StillFits(Timeline timeline, Transition transition) =>
        timeline.FindClip(transition.OutClip) is { } a && timeline.FindClip(transition.InClip) is { } b && a.TrackId == transition.TrackId
        && b.TrackId == transition.TrackId && Math.Abs(TimelineLayout.End(a, timeline.Rate) - b.Start) < timeline.Rate.FrameDuration / 2
        && transition.Duration / 2 <= Math.Min(TimelineLayout.Length(a, timeline.Rate), TimelineLayout.Length(b, timeline.Rate)) + Epsilon;

    // Kept source time beyond the clip's window, enough for `seconds` of timeline at its speed (a still always has it).
    private static bool HasHandle(Clip clip, double seconds, bool after)
    {
        if (clip.IsStill)
            return true;
        var needed = seconds * clip.Speed;
        var (from, to) = after ? (clip.SourceOut, clip.SourceOut + needed) : (clip.SourceIn - needed, clip.SourceIn);
        if (from < -Epsilon || to > clip.Media.Duration + Epsilon)
            return false;
        var kept = TimelineLayout.KeptPosition(clip.Segments, to) - TimelineLayout.KeptPosition(clip.Segments, from);
        return kept >= needed - 1e-4;
    }

    private static double Finite(double value, double fallback = 0) => double.IsFinite(value) ? value : fallback;

}
