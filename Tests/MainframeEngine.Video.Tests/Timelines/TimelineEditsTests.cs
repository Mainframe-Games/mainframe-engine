using MainframeEngine.Video.Timelines;
using static MainframeEngine.Video.Tests.Timelines.TimelineTestData;

namespace MainframeEngine.Video.Tests.Timelines;

public sealed class TimelineEditsTests
{
    // ── Placing ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AppendPlacesALinkedPairAfterTheLastClip()
    {
        var t = Append(Append(Vertical(), 0, 4), 10, 12);
        var video = t.ClipsOn(V1(t));
        var audio = t.ClipsOn(A1(t));
        Assert.Equal([0.0, 4.0], video.Select(c => c.Start));
        Assert.Equal([0.0, 4.0], audio.Select(c => c.Start));
        Assert.NotNull(video[1].LinkGroup);
        Assert.Equal(video[1].LinkGroup, audio[1].LinkGroup);
        Assert.NotEqual(video[0].LinkGroup, video[1].LinkGroup);
        Partitioned(t);
    }

    [Fact]
    public void AudioOnlyMediaSkipsTheVideoTrack()
    {
        var fresh = Vertical();
        var t = TimelineEdits.Append(fresh, Music(), 0, 30, V1(fresh), A2(fresh));
        var clip = Assert.Single(t.Clips);
        Assert.Equal(A2(t), clip.TrackId);
        Assert.Null(clip.LinkGroup);
    }

    [Fact]
    public void InsertSplitsAndPushesLaterClips()
    {
        var t = Append(Vertical(), 0, 10);
        t = TimelineEdits.Insert(t, Recording("b.mp4"), 0, 2, at: 4, V1(t), A1(t));
        var video = t.ClipsOn(V1(t));
        Assert.Equal(3, video.Count);
        Assert.Equal([0.0, 4.0, 6.0], video.Select(c => c.Start));
        Assert.Equal(4, video[2].SourceIn, 6); // the right half resumes where the left stopped
        Assert.Equal(12, End(t, video[2]), 6);
        Assert.Equal(video[2].LinkGroup, t.ClipsOn(A1(t))[2].LinkGroup); // the right halves stay linked
        Assert.NotEqual(video[0].LinkGroup, video[2].LinkGroup);
        Partitioned(t);
    }

    [Fact]
    public void OverwriteSplitsCoveredClip()
    {
        var t = Append(Vertical(), 0, 10);
        t = TimelineEdits.Overwrite(t, Recording("b.mp4"), 0, 2, at: 4, V1(t), null);
        var video = t.ClipsOn(V1(t));
        Assert.Equal([0.0, 4.0, 6.0], video.Select(c => c.Start));
        Assert.Equal([4.0, 6.0, 10.0], video.Select(c => End(t, c)));
        Assert.Equal(6, video[2].SourceIn, 6);
        Assert.Equal(10, End(t, t.ClipsOn(A1(t))[0]), 6); // its audio was not on a target track
    }

    // ── Razor ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RazorKeepsPartitionInBothHalves()
    {
        var t = Append(Vertical(), 0, 20);
        t = Cut(t, On(t, V1(t), 0), 2, 5);
        var before = On(t, V1(t), 0);
        t = TimelineEdits.Razor(t, 10, [V1(t)]);
        var video = t.ClipsOn(V1(t));
        Assert.Equal(2, video.Count);
        Assert.Equal(before.Segments, video[0].Segments);
        Assert.Equal(before.Segments, video[1].Segments);
        Assert.Equal(End(t, video[0]), video[1].Start);
        Assert.Equal(video[0].SourceOut, video[1].SourceIn, 6);
        Assert.Equal(13, video[1].SourceIn, 6); // 10 s of timeline = 2 kept + 3 cut + 8 kept
        Assert.Equal(2, t.ClipsOn(A1(t)).Count); // the linked audio is cut too
        Assert.Equal(TimelineLayout.Length(before, t.Rate), video.Sum(c => TimelineLayout.Length(c, t.Rate)), 6);
        Partitioned(t);
    }

    [Fact]
    public void RazorOutsideAnyClipChangesNothing()
    {
        var t = Append(Vertical(), 0, 5);
        Assert.Same(t, TimelineEdits.Razor(t, 7));
        Assert.Same(t, TimelineEdits.Razor(t, 0));
    }

    // ── Ripple and lift ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RippleDeleteClosesGapAndMovesLinked()
    {
        var t = Append(Append(Append(Vertical(), 0, 3), 10, 14), 20, 22); // A 0-3, B 3-7, C 7-9
        var b = On(t, V1(t), 1);
        t = TimelineEdits.RippleDelete(t, [b.Id]);
        Assert.Equal([0.0, 3.0], t.ClipsOn(V1(t)).Select(c => c.Start));
        Assert.Equal([0.0, 3.0], t.ClipsOn(A1(t)).Select(c => c.Start));
        Assert.Equal(20, On(t, V1(t), 1).SourceIn, 6);
    }

    [Fact]
    public void LiftLeavesTheGap()
    {
        var t = Append(Append(Vertical(), 0, 3), 10, 14);
        t = TimelineEdits.Lift(t, [On(t, V1(t), 0).Id]);
        Assert.Equal(3, Assert.Single(t.ClipsOn(V1(t))).Start);
        Assert.Single(t.ClipsOn(A1(t)));
    }

    [Fact]
    public void RippleTrimEndAndStart()
    {
        var t = Append(Append(Vertical(), 0, 6), 10, 14); // A 0-6, B 6-10
        var a = On(t, V1(t), 0);

        var w = TimelineEdits.RippleTrim(t, a.Id, TrimEdge.End, 4); // W at 4
        Assert.Equal(4, End(w, On(w, V1(w), 0)), 6);
        Assert.Equal(4, On(w, V1(w), 1).Start, 6);
        Assert.Equal(4, On(w, A1(w), 1).Start, 6);

        var q = TimelineEdits.RippleTrim(t, a.Id, TrimEdge.Start, 2); // Q at 2
        var trimmed = On(q, V1(q), 0);
        Assert.Equal(0, trimmed.Start, 6);
        Assert.Equal(2, trimmed.SourceIn, 6);
        Assert.Equal(4, On(q, V1(q), 1).Start, 6);
    }

    [Fact]
    public void TrimStopsAtNeighboursAndTheSource()
    {
        var t = Append(Append(Vertical(), 5, 8), 20, 25); // A 0-3 (5..8), B 3-8
        var a = On(t, V1(t), 0);
        var longer = TimelineEdits.Trim(t, a.Id, TrimEdge.End, 6);
        Assert.Same(t, longer); // B is in the way
        var earlier = TimelineEdits.Trim(t, On(t, V1(t), 1).Id, TrimEdge.Start, 1);
        Assert.Same(t, earlier); // A is in the way
        var lifted = TimelineEdits.Lift(t, [On(t, V1(t), 1).Id]);
        var extended = TimelineEdits.Trim(lifted, a.Id, TrimEdge.End, 100);
        Assert.Equal(60 - 5, End(extended, On(extended, V1(extended), 0)), 6); // the rest of the recording
        var shortened = TimelineEdits.Trim(lifted, a.Id, TrimEdge.Start, 2);
        Assert.Equal(2, On(shortened, V1(shortened), 0).Start, 6);
        Assert.Equal(7, On(shortened, V1(shortened), 0).SourceIn, 6);
        Assert.Equal(2, On(shortened, A1(shortened), 0).Start, 6); // its sound with it
    }

    // ── Moving ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MovesCarryLinkedClipsAndOverwrite()
    {
        var t = Append(Append(Vertical(), 0, 4), 10, 14); // A 0-4, B 4-8
        var b = On(t, V1(t), 1);
        t = TimelineEdits.Move(t, b.Id, 2);
        Assert.Equal([0.0, 2.0], t.ClipsOn(V1(t)).Select(c => c.Start));
        Assert.Equal(2, End(t, On(t, V1(t), 0)), 6); // A was overwritten from 2
        Assert.Equal([0.0, 2.0], t.ClipsOn(A1(t)).Select(c => c.Start));
    }

    [Fact]
    public void MoveToAnotherTrackOfTheSameKind()
    {
        var t = Append(Vertical(), 0, 4);
        var v = On(t, V1(t), 0);
        t = TimelineEdits.Move(t, v.Id, 1, V2(t));
        Assert.Equal(V2(t), t.FindClip(v.Id)!.TrackId);
        Assert.Equal(1, t.FindClip(v.Id)!.Start, 6);
        Assert.Equal(1, On(t, A1(t), 0).Start, 6); // the sound moved in time, not track
    }

    [Fact]
    public void MoveRefusesWrongTrackKind()
    {
        var t = Append(Vertical(), 0, 4);
        Assert.Same(t, TimelineEdits.Move(t, On(t, A1(t), 0).Id, 0, V2(t)));
    }

    [Fact]
    public void MovesNeverStartBeforeZero()
    {
        var t = Append(Vertical(), 0, 4);
        var moved = TimelineEdits.Move(t, On(t, V1(t), 0).Id, 3);
        moved = TimelineEdits.Move(moved, On(moved, V1(moved), 0).Id, -5);
        Assert.Equal(0, On(moved, V1(moved), 0).Start);
    }

    // ── Links and locks ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LinkedClipsShareCuts()
    {
        var t = Append(Vertical(), 0, 20);
        t = Cut(t, On(t, V1(t), 0), 5, 8);
        Assert.Equal(17, End(t, On(t, V1(t), 0)), 6);
        Assert.Equal(17, End(t, On(t, A1(t), 0)), 6);
        Assert.Equal(On(t, V1(t), 0).Segments, On(t, A1(t), 0).Segments);
    }

    [Fact]
    public void CutsRippleLaterClips()
    {
        var t = Append(Append(Vertical(), 0, 10), 20, 25); // A 0-10, B 10-15
        t = Cut(t, On(t, V1(t), 0), 2, 4);
        Assert.Equal(8, On(t, V1(t), 1).Start, 6);
        Assert.Equal(8, On(t, A1(t), 1).Start, 6);
        var restored = TimelineEdits.SetClipCuts(t, On(t, V1(t), 0).Id, list => list.SetRange(2, 4, enabled: true));
        Assert.Equal(10, On(restored, V1(restored), 1).Start, 6);
    }

    [Fact]
    public void ACutLeavingNothingIsRefused()
    {
        var t = Append(Vertical(), 2, 4);
        Assert.Same(t, Cut(t, On(t, V1(t), 0), 1, 5));
    }

    [Fact]
    public void UnlinkedClipsMoveAlone()
    {
        var t = Append(Vertical(), 0, 4);
        t = TimelineEdits.Unlink(t, [On(t, V1(t), 0).Id]);
        Assert.All(t.Clips, c => Assert.Null(c.LinkGroup));
        t = TimelineEdits.Move(t, On(t, V1(t), 0).Id, 2);
        Assert.Equal(2, On(t, V1(t), 0).Start);
        Assert.Equal(0, On(t, A1(t), 0).Start);

        var cut = Cut(t, On(t, V1(t), 0), 1, 2);
        Assert.Equal(4, End(cut, On(cut, A1(cut), 0)), 6); // unlinked audio keeps its cuts
        var relinked = TimelineEdits.Link(t, t.Clips.Select(c => c.Id));
        Assert.Single(relinked.Clips.Select(c => c.LinkGroup).Distinct());
    }

    [Fact]
    public void LockedTrackRefusesEdits()
    {
        var t = Append(Append(Vertical(), 0, 4), 10, 14);
        var locked = TimelineEdits.SetTrack(t, A1(t), x => x with { Locked = true });
        var v = On(locked, V1(locked), 0);
        Assert.Same(locked, TimelineEdits.Move(locked, v.Id, 10)); // its linked audio is locked
        Assert.Same(locked, TimelineEdits.RippleDelete(locked, [v.Id]));
        Assert.Same(locked, TimelineEdits.Trim(locked, v.Id, TrimEdge.End, 2));
        Assert.Same(locked, Cut(locked, v, 1, 2));
        Assert.Same(locked, TimelineEdits.Insert(locked, Recording(), 0, 1, 0, V1(locked), A1(locked)));
        Assert.Same(locked, TimelineEdits.RemoveTrack(locked, A1(locked)));
        var razored = TimelineEdits.Razor(locked, 2);
        Assert.Equal(3, razored.ClipsOn(V1(razored)).Count); // V1 is cut, the locked A1 is not
        Assert.Equal(2, razored.ClipsOn(A1(razored)).Count);
    }

    // ── Frames, tracks, range ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EditsSnapToFrames()
    {
        var fresh = Vertical();
        var t = TimelineEdits.Insert(fresh, Recording(), 0, 2, at: 1.0101, V1(fresh), null);
        Assert.Equal(1.0, On(t, V1(t), 0).Start);
        Assert.Equal(30, Rate30.FrameAt(On(t, V1(t), 0).Start));
    }

    [Fact]
    public void TracksAreAddedAboveTheirKindAndRemovedWithTheirClips()
    {
        var t = Append(Vertical(), 0, 4);
        t = TimelineEdits.AddTrack(t, TrackKind.Video);
        Assert.Equal(["V1", "V2", "V3", "A1", "A2"], t.Tracks.Select(x => x.Name));
        t = TimelineEdits.RemoveTrack(t, A1(t));
        Assert.Null(Assert.Single(t.Clips).LinkGroup); // its partner lost the link
        Assert.Same(t, TimelineEdits.SetTrack(t, V1(t), x => x));
    }

    [Fact]
    public void InAndOutMustBeOrdered()
    {
        var t = Vertical();
        Assert.Same(t, TimelineEdits.SetInOut(t, 5, 2));
        var ranged = TimelineEdits.SetInOut(t, 1.01, 4);
        Assert.Equal(1.0, ranged.InPoint);
    }
}
