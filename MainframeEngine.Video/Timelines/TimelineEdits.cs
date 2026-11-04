using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Timelines;

/// <summary>Which end of a clip a trim moves.</summary>
public enum TrimEdge
{
    Start,
    End,
}

/// <summary>
/// Every timeline edit, as pure functions (docs/design/future/video-nle.md). Each returns a new <see cref="Timeline"/>,
/// or <b>the same instance</b> when nothing changed or the edit is refused: a locked track (of the clip or of a clip
/// linked to it), a clip on the wrong kind of track, an unknown id. Times snap to the timeline's frames.
/// </summary>
/// <remarks>
/// Linked clips (a recording's picture and sound) select, move, trim and cut together. Placing a clip over others
/// overwrites them: covered clips are trimmed, split or removed (<c>ClearRange</c>); their linked partners elsewhere are
/// left alone, as an NLE without sync lock does. Ripple shifts the clips after an edit on the edited clips' tracks only.
/// </remarks>
public static class TimelineEdits
{
    private const double Epsilon = 1e-6;

    // ── Tracks ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Adds a track of <paramref name="kind"/> above the others of its kind (named V3, A3… unless named).</summary>
    public static Timeline AddTrack(Timeline timeline, TrackKind kind, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var count = timeline.Tracks.Count(t => t.Kind == kind);
        var track = TimelineFactory.NewTrack(kind, string.IsNullOrWhiteSpace(name) ? $"{(kind == TrackKind.Video ? 'V' : 'A')}{count + 1}" : name);
        var lastOfKind = -1;
        for (var i = 0; i < timeline.Tracks.Length; i++)
            if (timeline.Tracks[i].Kind == kind)
                lastOfKind = i;
        var index = lastOfKind >= 0 ? lastOfKind + 1 : kind == TrackKind.Video ? 0 : timeline.Tracks.Length;
        return timeline with { Tracks = timeline.Tracks.Insert(index, track) };
    }

    /// <summary>Removes an unlocked track and its clips; their links to other tracks are dropped.</summary>
    public static Timeline RemoveTrack(Timeline timeline, string trackId)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.FindTrack(trackId) is not { Locked: false } track)
            return timeline;
        var clips = timeline.Clips.Where(c => c.TrackId != trackId).ToList();
        return Finish(timeline with { Tracks = timeline.Tracks.Remove(track) }, clips);
    }

    /// <summary>Changes a track's header state (name, enabled, lock, mute, solo, gain).</summary>
    public static Timeline SetTrack(Timeline timeline, string trackId, Func<Track, Track> change)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(change);
        if (timeline.FindTrack(trackId) is not { } track)
            return timeline;
        var changed = change(track) with { Id = track.Id, Kind = track.Kind };
        return changed == track ? timeline : timeline with { Tracks = timeline.Tracks.Replace(track, changed) };
    }

    // ── Placing media ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts <c>[sourceIn, sourceOut)</c> of <paramref name="media"/> at <paramref name="at"/>: its picture on
    /// <paramref name="videoTrackId"/> and its sound on <paramref name="audioTrackId"/> (either may be null; a file without
    /// that stream skips it), linked. Clips at and after <paramref name="at"/> on those tracks move later; one spanning it
    /// is split.
    /// </summary>
    public static Timeline Insert(Timeline timeline, MediaRef media, double sourceIn, double sourceOut, double at, string? videoTrackId, string? audioTrackId)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (NewClips(timeline, media, sourceIn, sourceOut, at, videoTrackId, audioTrackId) is not { } placed)
            return timeline;
        var rate = timeline.Rate;
        var length = TimelineLayout.Length(placed[0], rate);
        var start = placed[0].Start;
        var clips = timeline.Clips.ToList();
        var groups = new Dictionary<string, string>();
        foreach (var trackId in placed.Select(c => c.TrackId))
        {
            SplitAt(clips, trackId, start, rate, groups);
            for (var i = 0; i < clips.Count; i++)
                if (clips[i].TrackId == trackId && clips[i].Start >= start - Epsilon)
                    clips[i] = clips[i] with { Start = clips[i].Start + length };
        }

        clips.AddRange(placed);
        return Finish(timeline, clips);
    }

    /// <summary>Like <see cref="Insert"/>, but over what is there: covered clips are trimmed, split or removed; nothing moves.</summary>
    public static Timeline Overwrite(Timeline timeline, MediaRef media, double sourceIn, double sourceOut, double at, string? videoTrackId, string? audioTrackId)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (NewClips(timeline, media, sourceIn, sourceOut, at, videoTrackId, audioTrackId) is not { } placed)
            return timeline;
        var rate = timeline.Rate;
        var clips = timeline.Clips.ToList();
        var groups = new Dictionary<string, string>();
        foreach (var clip in placed)
            ClearRange(clips, clip.TrackId, clip.Start, TimelineLayout.End(clip, rate), rate, groups);
        clips.AddRange(placed);
        return Finish(timeline, clips);
    }

    /// <summary>Places the media after the last clip of the target tracks.</summary>
    public static Timeline Append(Timeline timeline, MediaRef media, double sourceIn, double sourceOut, string? videoTrackId, string? audioTrackId)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var end = 0.0;
        foreach (var clip in timeline.Clips)
            if (clip.TrackId == videoTrackId || clip.TrackId == audioTrackId)
                end = Math.Max(end, TimelineLayout.End(clip, timeline.Rate));
        return Overwrite(timeline, media, sourceIn, sourceOut, end, videoTrackId, audioTrackId);
    }

    // ── Cutting and trimming ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits every clip under <paramref name="time"/> on <paramref name="trackIds"/> (null: every unlocked track) and
    /// their linked partners; the right halves stay linked to each other. Locked tracks are never cut.
    /// </summary>
    public static Timeline Razor(Timeline timeline, double time, IEnumerable<string>? trackIds = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var rate = timeline.Rate;
        time = rate.Snap(time);
        var tracks = (trackIds ?? timeline.Tracks.Select(t => t.Id)).ToHashSet(StringComparer.Ordinal);
        var targets = timeline.Clips.Where(c => tracks.Contains(c.TrackId) && Spans(c, time, rate)).ToList();
        foreach (var clip in targets.ToList())
            targets.AddRange(timeline.LinkedWith(clip).Where(p => !targets.Contains(p) && Spans(p, time, rate)));
        targets.RemoveAll(c => IsLocked(timeline, c.TrackId));
        if (targets.Count == 0)
            return timeline;

        var clips = timeline.Clips.ToList();
        var groups = new Dictionary<string, string>();
        foreach (var clip in targets)
        {
            var (left, right) = ClipOps.Split(clip, time, rate);
            clips[clips.IndexOf(clip)] = left;
            clips.Add(right with { LinkGroup = RightGroup(clip.LinkGroup, groups) });
        }

        return Finish(timeline, clips);
    }

    /// <summary>
    /// Moves a clip's start or end (and its linked partners') to <paramref name="time"/>, stopping at the neighbouring
    /// clips, the source's footage and one frame of length. Nothing else moves.
    /// </summary>
    public static Timeline Trim(Timeline timeline, string clipId, TrimEdge edge, double time) => TrimCore(timeline, clipId, edge, time, ripple: false);

    /// <summary>
    /// A trim that closes or opens the gap: the clip keeps its start, and the clips after it on its tracks (and its linked
    /// partners' tracks) move by the change in length (Premiere's Q and W, and the ripple edit tool).
    /// </summary>
    public static Timeline RippleTrim(Timeline timeline, string clipId, TrimEdge edge, double time) => TrimCore(timeline, clipId, edge, time, ripple: true);

    private static Timeline TrimCore(Timeline timeline, string clipId, TrimEdge edge, double time, bool ripple)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.FindClip(clipId) is not { } clip)
            return timeline;
        var rate = timeline.Rate;
        var group = timeline.LinkedWith(clip);
        if (group.Any(c => IsLocked(timeline, c.TrackId)))
            return timeline;

        var edgeTime = edge == TrimEdge.Start ? clip.Start : TimelineLayout.End(clip, rate);
        var delta = rate.Snap(time) - edgeTime;
        var ids = group.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var member in group)
        {
            var start = member.Start;
            var end = TimelineLayout.End(member, rate);
            if (edge == TrimEdge.Start)
            {
                delta = Math.Min(delta, end - rate.FrameDuration - start); // keep a frame
                if (ripple)
                    delta = Math.Max(delta, ClipOps.EarliestStart(member, rate) - start);
                else
                    delta = Math.Max(delta, Math.Max(ClipOps.EarliestStart(member, rate), PreviousEnd(timeline, member, ids)) - start);
            }
            else
            {
                delta = Math.Max(delta, start + rate.FrameDuration - end);
                var limit = ClipOps.LatestEnd(member, rate);
                if (!ripple)
                    limit = Math.Min(limit, NextStart(timeline, member, ids));
                delta = Math.Min(delta, limit - end);
            }
        }

        delta = rate.Snap(delta);
        if (Math.Abs(delta) < Epsilon)
            return timeline;

        var clips = timeline.Clips.ToList();
        foreach (var member in group)
        {
            var oldEnd = TimelineLayout.End(member, rate);
            Clip changed;
            if (edge == TrimEdge.End)
                changed = ClipOps.TrimEndTo(member, oldEnd + delta, rate);
            else if (!ripple)
                changed = ClipOps.TrimStartTo(member, member.Start + delta, rate);
            else
                changed = ClipOps.TrimStartTo(member, member.Start + delta, rate) with { Start = member.Start }; // keeps its place

            clips[clips.IndexOf(member)] = changed;
            if (ripple)
                Shift(clips, member.TrackId, oldEnd, TimelineLayout.End(changed, rate) - oldEnd, ids);
        }

        return Finish(timeline, clips);
    }

    // ── Removing ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Removes the clips and their linked partners, leaving gaps.</summary>
    public static Timeline Lift(Timeline timeline, IEnumerable<string> clipIds) => Remove(timeline, clipIds, ripple: false);

    /// <summary>Removes the clips and their linked partners, and closes the gaps on their tracks.</summary>
    public static Timeline RippleDelete(Timeline timeline, IEnumerable<string> clipIds) => Remove(timeline, clipIds, ripple: true);

    private static Timeline Remove(Timeline timeline, IEnumerable<string> clipIds, bool ripple)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(clipIds);
        var removed = Expand(timeline, clipIds);
        if (removed.Count == 0 || removed.Any(c => IsLocked(timeline, c.TrackId)))
            return timeline;
        var rate = timeline.Rate;
        var ids = removed.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var clips = timeline.Clips.Where(c => !ids.Contains(c.Id)).ToList();
        if (ripple)
        {
            for (var i = 0; i < clips.Count; i++)
            {
                var shift = 0.0;
                foreach (var gone in removed)
                    if (gone.TrackId == clips[i].TrackId && TimelineLayout.End(gone, rate) <= clips[i].Start + Epsilon)
                        shift += TimelineLayout.Length(gone, rate);
                if (shift > 0)
                    clips[i] = clips[i] with { Start = rate.Snap(clips[i].Start - shift) };
            }
        }

        return Finish(timeline, clips);
    }

    // ── Moving ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Moves a clip (and its linked partners by the same time) to <paramref name="newStart"/>, onto
    /// <paramref name="newTrackId"/> if given (a track of the same kind; partners keep their tracks). What the moved clips
    /// land on is overwritten. Clips never start before 0.
    /// </summary>
    public static Timeline Move(Timeline timeline, string clipId, double newStart, string? newTrackId = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.FindClip(clipId) is not { } clip || timeline.FindTrack(clip.TrackId) is not { } from)
            return timeline;
        var rate = timeline.Rate;
        var to = newTrackId is null ? from : timeline.FindTrack(newTrackId);
        if (to is null || to.Kind != from.Kind || to.Locked)
            return timeline;
        var group = timeline.LinkedWith(clip);
        if (group.Any(c => IsLocked(timeline, c.TrackId)))
            return timeline;

        var delta = rate.Snap(newStart) - clip.Start;
        delta = Math.Max(delta, -group.Min(c => c.Start));
        if (Math.Abs(delta) < Epsilon && to.Id == from.Id)
            return timeline;

        var ids = group.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var clips = timeline.Clips.Where(c => !ids.Contains(c.Id)).ToList();
        var moved = group.Select(c => c with
        {
            Start = rate.Snap(c.Start + delta),
            TrackId = c.Id == clip.Id ? to.Id : c.TrackId,
        }).ToList();
        var groups = new Dictionary<string, string>();
        foreach (var m in moved)
            ClearRange(clips, m.TrackId, m.Start, TimelineLayout.End(m, rate), rate, groups);
        clips.AddRange(moved);
        return Finish(timeline, clips);
    }

    // ── Links, transforms, cuts ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Links the clips (two or more) into one new group; each leaves its old group.</summary>
    public static Timeline Link(Timeline timeline, IEnumerable<string> clipIds)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(clipIds);
        var ids = clipIds.Where(id => timeline.FindClip(id) is not null).ToHashSet(StringComparer.Ordinal);
        if (ids.Count < 2)
            return timeline;
        var group = TimelineFactory.NewId();
        return Finish(timeline, timeline.Clips.Select(c => ids.Contains(c.Id) ? c with { LinkGroup = group } : c));
    }

    /// <summary>Unlinks the groups of the clips: every member becomes independent.</summary>
    public static Timeline Unlink(Timeline timeline, IEnumerable<string> clipIds)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(clipIds);
        var groups = clipIds.Select(timeline.FindClip).Where(c => c?.LinkGroup is not null).Select(c => c!.LinkGroup!).ToHashSet(StringComparer.Ordinal);
        if (groups.Count == 0)
            return timeline;
        return timeline.With(timeline.Clips.Select(c => c.LinkGroup is { } g && groups.Contains(g) ? c with { LinkGroup = null } : c));
    }

    /// <summary>Sets a video clip's reframe.</summary>
    public static Timeline SetTransform(Timeline timeline, string clipId, ClipTransform transform)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(transform);
        if (timeline.FindClip(clipId) is not { } clip || clip.Transform == transform || IsLocked(timeline, clip.TrackId)
            || timeline.FindTrack(clip.TrackId)?.Kind != TrackKind.Video)
            return timeline;
        return timeline.With(timeline.Clips.Replace(clip, clip with { Transform = transform }));
    }

    /// <summary>
    /// Edits a clip's cut list (Cutback's <see cref="SegmentList"/> operations: detections, region toggles, boundary
    /// drags). The same cuts apply to its linked partners showing the same file, and the clips after each on its track
    /// ripple by the change in length. Refused when it would leave nothing of the clip to play.
    /// </summary>
    public static Timeline SetClipCuts(Timeline timeline, string clipId, Action<SegmentList> edit)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(edit);
        if (timeline.FindClip(clipId) is not { } clip)
            return timeline;
        var members = timeline.LinkedWith(clip).Where(c => c.Media.Path == clip.Media.Path && c.Media.Hash == clip.Media.Hash).ToList();
        if (members.Any(c => IsLocked(timeline, c.TrackId)))
            return timeline;

        var list = new SegmentList(clip.Media.Duration, clip.Segments);
        edit(list);
        var segments = list.Segments;
        if (segments.SequenceEqual(clip.Segments))
            return timeline;
        if (TimelineLayout.Length(clip with { Segments = [.. segments] }, timeline.Rate) <= Epsilon)
            return timeline; // a clip cut to nothing could never be selected to restore it

        var rate = timeline.Rate;
        var ids = members.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var clips = timeline.Clips.ToList();
        foreach (var member in members)
        {
            var oldEnd = TimelineLayout.End(member, rate);
            var changed = member with { Segments = [.. segments] };
            clips[clips.IndexOf(member)] = changed;
            Shift(clips, member.TrackId, oldEnd, TimelineLayout.End(changed, rate) - oldEnd, ids);
        }

        return Finish(timeline, clips);
    }

    // ── Range and markers ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Sets the in and out points (null clears one). Refused when in is not before out.</summary>
    public static Timeline SetInOut(Timeline timeline, double? inPoint, double? outPoint)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var rate = timeline.Rate;
        double? i = inPoint is { } a ? rate.Snap(Math.Max(0, a)) : null;
        double? o = outPoint is { } b ? rate.Snap(Math.Max(0, b)) : null;
        if (i is { } x && o is { } y && x >= y)
            return timeline;
        return i == timeline.InPoint && o == timeline.OutPoint ? timeline : timeline with { InPoint = i, OutPoint = o };
    }

    /// <summary>Adds a marker at <paramref name="time"/>.</summary>
    public static Timeline AddMarker(Timeline timeline, double time, string name)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return timeline with { Markers = timeline.Markers.Add(new Marker(TimelineFactory.NewId(), timeline.Rate.Snap(Math.Max(0, time)), name ?? "")) };
    }

    /// <summary>Removes a marker.</summary>
    public static Timeline RemoveMarker(Timeline timeline, string markerId)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var marker = timeline.Markers.FirstOrDefault(m => m.Id == markerId);
        return marker is null ? timeline : timeline with { Markers = timeline.Markers.Remove(marker) };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    // The clips Insert/Overwrite place, or null when refused (unknown or locked track, wrong kind, empty window).
    private static List<Clip>? NewClips(Timeline timeline, MediaRef media, double sourceIn, double sourceOut, double at, string? videoTrackId, string? audioTrackId)
    {
        ArgumentNullException.ThrowIfNull(media);
        var rate = timeline.Rate;
        sourceIn = Math.Clamp(sourceIn, 0, media.Duration);
        sourceOut = Math.Clamp(sourceOut, 0, media.Duration);
        if (sourceOut - sourceIn < rate.FrameDuration / 2)
            return null;
        var start = rate.Snap(Math.Max(0, at));
        var tracks = new List<string>(2);
        if (media.HasVideo && videoTrackId is not null)
        {
            if (timeline.FindTrack(videoTrackId) is not { Kind: TrackKind.Video, Locked: false })
                return null;
            tracks.Add(videoTrackId);
        }

        if (media.HasAudio && audioTrackId is not null)
        {
            if (timeline.FindTrack(audioTrackId) is not { Kind: TrackKind.Audio, Locked: false })
                return null;
            tracks.Add(audioTrackId);
        }

        if (tracks.Count == 0)
            return null;
        var group = tracks.Count > 1 ? TimelineFactory.NewId() : null;
        return [.. tracks.Select(t => TimelineFactory.NewClip(media, t, start, sourceIn, sourceOut, group))];
    }

    // Trims, splits or removes the clips on a track that overlap [from, to).
    private static void ClearRange(List<Clip> clips, string trackId, double from, double to, FrameRate rate, Dictionary<string, string> groups)
    {
        for (var i = clips.Count - 1; i >= 0; i--)
        {
            var clip = clips[i];
            if (clip.TrackId != trackId)
                continue;
            var end = TimelineLayout.End(clip, rate);
            if (end <= from + Epsilon || clip.Start >= to - Epsilon)
                continue;
            if (clip.Start >= from - Epsilon && end <= to + Epsilon)
            {
                clips.RemoveAt(i);
            }
            else if (clip.Start < from && end > to)
            {
                clips[i] = ClipOps.TrimEndTo(clip, from, rate);
                clips.Add(ClipOps.TrimStartTo(clip, to, rate) with { Id = TimelineFactory.NewId(), LinkGroup = RightGroup(clip.LinkGroup, groups) });
            }
            else if (clip.Start < from)
            {
                clips[i] = ClipOps.TrimEndTo(clip, from, rate);
            }
            else
            {
                clips[i] = ClipOps.TrimStartTo(clip, to, rate);
            }
        }
    }

    // Splits the clip on a track spanning `time` (none: nothing).
    private static void SplitAt(List<Clip> clips, string trackId, double time, FrameRate rate, Dictionary<string, string> groups)
    {
        var index = clips.FindIndex(c => c.TrackId == trackId && Spans(c, time, rate));
        if (index < 0)
            return;
        var (left, right) = ClipOps.Split(clips[index], time, rate);
        clips[index] = left;
        clips.Add(right with { LinkGroup = RightGroup(clips[index].LinkGroup, groups) });
    }

    // Moves the clips on a track starting at or after `from` by `delta` (ripple), except `exclude`.
    private static void Shift(List<Clip> clips, string trackId, double from, double delta, HashSet<string> exclude)
    {
        if (Math.Abs(delta) < Epsilon)
            return;
        for (var i = 0; i < clips.Count; i++)
            if (clips[i].TrackId == trackId && !exclude.Contains(clips[i].Id) && clips[i].Start >= from - Epsilon)
                clips[i] = clips[i] with { Start = clips[i].Start + delta };
    }

    // Right halves of split linked clips share a new group, one per original group.
    private static string? RightGroup(string? group, Dictionary<string, string> groups)
    {
        if (group is null)
            return null;
        if (!groups.TryGetValue(group, out var right))
            groups[group] = right = TimelineFactory.NewId();
        return right;
    }

    private static bool Spans(Clip clip, double time, FrameRate rate) =>
        clip.Start < time - Epsilon && TimelineLayout.End(clip, rate) > time + Epsilon;

    private static bool IsLocked(Timeline timeline, string trackId) => timeline.FindTrack(trackId)?.Locked ?? true;

    private static List<Clip> Expand(Timeline timeline, IEnumerable<string> clipIds)
    {
        var result = new List<Clip>();
        foreach (var id in clipIds)
            if (timeline.FindClip(id) is { } clip)
                foreach (var member in timeline.LinkedWith(clip))
                    if (!result.Contains(member))
                        result.Add(member);
        return result;
    }

    private static double PreviousEnd(Timeline timeline, Clip clip, HashSet<string> exclude)
    {
        var end = 0.0;
        foreach (var other in timeline.Clips)
            if (other.TrackId == clip.TrackId && !exclude.Contains(other.Id) && other.Start < clip.Start - Epsilon)
                end = Math.Max(end, TimelineLayout.End(other, timeline.Rate));
        return end;
    }

    private static double NextStart(Timeline timeline, Clip clip, HashSet<string> exclude)
    {
        var start = double.MaxValue;
        foreach (var other in timeline.Clips)
            if (other.TrackId == clip.TrackId && !exclude.Contains(other.Id) && other.Start > clip.Start + Epsilon)
                start = Math.Min(start, other.Start);
        return start;
    }

    // Drops clips with nothing left to play and link groups with one member left, then builds the timeline.
    private static Timeline Finish(Timeline timeline, IEnumerable<Clip> clips)
    {
        var rate = timeline.Rate;
        var kept = clips.Where(c => TimelineLayout.Length(c, rate) > Epsilon).ToList();
        var counts = kept.Where(c => c.LinkGroup is not null).GroupBy(c => c.LinkGroup!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        for (var i = 0; i < kept.Count; i++)
            if (kept[i].LinkGroup is { } g && counts[g] < 2)
                kept[i] = kept[i] with { LinkGroup = null };
        return timeline.With(kept);
    }
}
