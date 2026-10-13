using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Tests;

public sealed class SegmentListTests
{
    private const double Duration = 10.0;

    /// <summary>[0,3) kept, [3,6) removed, [6,10) kept, all auto-detected.</summary>
    private static SegmentList ThreeSegments() => new(
        Duration,
        [
            Segment.Create(0.0, 3.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(3.0, 6.0, enabled: false, SegmentOrigin.Auto, "silence 3s"),
            Segment.Create(6.0, 10.0, enabled: true, SegmentOrigin.Auto),
        ]);

    // ---- construction -------------------------------------------------------------------------

    [Fact]
    public void NewListIsASingleEnabledSegmentSpanningTheDuration()
    {
        var list = new SegmentList(Duration);

        Assert.Single(list.Segments);
        Assert.Equal(0.0, list.Segments[0].Start);
        Assert.Equal(Duration, list.Segments[0].End);
        Assert.True(list.Segments[0].Enabled);
        Assert.Equal(SegmentOrigin.Auto, list.Segments[0].Origin);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NewListRejectsNonPositiveOrNonFiniteDuration(double duration)
    {
        var act = () => new SegmentList(duration);

        Assert.Throws<ArgumentOutOfRangeException>(act);
    }

    [Fact]
    public void ConstructingFromSegmentsAcceptsAValidPartition()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 4.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(4.0, 10.0, enabled: false, SegmentOrigin.Auto, "silence"),
        };

        var list = new SegmentList(Duration, segments);

        Assert.Equal(segments, list.Segments);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void ConstructingFromSegmentsSortsByStart()
    {
        var segments = new[]
        {
            Segment.Create(4.0, 10.0, enabled: false, SegmentOrigin.Auto),
            Segment.Create(0.0, 4.0, enabled: true, SegmentOrigin.Auto),
        };

        var list = new SegmentList(Duration, segments);

        Assert.Equal(list.Segments.Select(s => s.Start).Order(), list.Segments.Select(s => s.Start));
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void ConstructingFromSegmentsWithAGapThrows()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 4.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(5.0, 10.0, enabled: true, SegmentOrigin.Auto),
        };

        var act = () => new SegmentList(Duration, segments);

        var e = Assert.Throws<InvalidPartitionException>(act);
        Assert.Contains("gap", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructingFromSegmentsWithAnOverlapThrows()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 5.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(4.0, 10.0, enabled: true, SegmentOrigin.Auto),
        };

        var act = () => new SegmentList(Duration, segments);

        var e = Assert.Throws<InvalidPartitionException>(act);
        Assert.Contains("overlap", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructingFromSegmentsThatDoNotStartAtZeroThrows()
    {
        var segments = new[] { Segment.Create(1.0, 10.0, enabled: true, SegmentOrigin.Auto) };

        var act = () => new SegmentList(Duration, segments);

        Assert.Throws<InvalidPartitionException>(act);
    }

    [Fact]
    public void ConstructingFromSegmentsThatDoNotEndAtDurationThrows()
    {
        var segments = new[] { Segment.Create(0.0, 9.0, enabled: true, SegmentOrigin.Auto) };

        var act = () => new SegmentList(Duration, segments);

        Assert.Throws<InvalidPartitionException>(act);
    }

    [Fact]
    public void ConstructingFromEmptySegmentsThrows()
    {
        var act = () => new SegmentList(Duration, Array.Empty<Segment>());

        Assert.Throws<InvalidPartitionException>(act);
    }

    [Fact]
    public void ConstructingFromSegmentsWithDuplicateIdsThrows()
    {
        var segments = new[]
        {
            new Segment("same", 0.0, 4.0, true, SegmentOrigin.Auto, null),
            new Segment("same", 4.0, 10.0, true, SegmentOrigin.Auto, null),
        };

        var act = () => new SegmentList(Duration, segments);

        var e = Assert.Throws<InvalidPartitionException>(act);
        Assert.Contains("id", e.Message, StringComparison.Ordinal);
    }

    // ---- lookup -------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(2.999, 0)]
    [InlineData(3.0, 1)]   // boundary belongs to the segment that starts there
    [InlineData(6.0, 2)]
    [InlineData(9.999, 2)]
    [InlineData(10.0, 2)]  // the duration itself maps to the last segment
    public void IndexAtReturnsTheSegmentContainingTheTime(double time, int expected)
    {
        var list = ThreeSegments();

        Assert.Equal(expected, list.IndexAt(time));
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(10.001)]
    public void IndexAtReturnsMinusOneOutsideTheTimeline(double time)
    {
        var list = ThreeSegments();

        Assert.Equal(-1, list.IndexAt(time));
    }

    // ---- Split --------------------------------------------------------------------------------

    [Fact]
    public void SplitDividesASegmentIntoTwoSharingTheBoundary()
    {
        var list = new SegmentList(Duration);

        var result = list.Split(4.0);

        Assert.True(result);
        Assert.Equal(2, list.Segments.Count);
        Assert.Equal(0.0, list.Segments[0].Start);
        Assert.Equal(4.0, list.Segments[0].End);
        Assert.Equal(4.0, list.Segments[1].Start);
        Assert.Equal(Duration, list.Segments[1].End);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void SplitHalvesInheritEnabledStateAndOrigin()
    {
        // Splitting is not a decision about either half, so it must not lock them against
        // re-detection. Only a toggle or a boundary drag on a cut does that.
        var list = new SegmentList(Duration, [Segment.Create(0.0, Duration, enabled: false, SegmentOrigin.Auto, "silence 10.00s")]);
        var original = list.Segments[0];

        list.Split(4.0);

        Assert.All(list.Segments, s => Assert.True(!s.Enabled));
        Assert.All(list.Segments, s => Assert.True(s.Origin == SegmentOrigin.Auto));
        Assert.All(list.Segments, s => Assert.True(s.Reason == "silence 10.00s"));
        Assert.Equal(original.Id, list.Segments[0].Id); // the left half keeps the original identity
        Assert.NotEqual(original.Id, list.Segments[1].Id);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(3.0)]
    [InlineData(6.0)]
    [InlineData(10.0)]
    public void SplitAtAnExistingBoundaryIsANoOp(double time)
    {
        var list = ThreeSegments();
        var before = list.Segments.ToList();

        var result = list.Split(time);

        Assert.False(result);
        Assert.Equal(before, list.Segments);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(11.0)]
    [InlineData(double.NaN)]
    public void SplitOutsideTheTimelineThrows(double time)
    {
        var list = new SegmentList(Duration);

        Action act = () => list.Split(time);

        Assert.Throws<ArgumentOutOfRangeException>(act);
    }

    [Fact]
    public void SplitInTheLastSegmentKeepsTheEndAtDuration()
    {
        var list = ThreeSegments();

        list.Split(8.0);

        Assert.Equal(4, list.Segments.Count);
        Assert.Equal(Duration, list.Segments[^1].End);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    // ---- MoveBoundary -------------------------------------------------------------------------

    [Fact]
    public void MoveBoundaryShiftsTheSharedEdgeOfBothNeighbours()
    {
        var list = ThreeSegments();

        var applied = list.MoveBoundary(1, 3.5);

        Assert.Equal(3.5, applied);
        Assert.Equal(3.5, list.Segments[0].End);
        Assert.Equal(3.5, list.Segments[1].Start);
        Assert.Equal(6.0, list.Segments[1].End); // the far edge of the neighbour is untouched
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void MoveBoundaryMarksTheCutNeighbourManualButNotTheKeptOne()
    {
        // Dragging the edge of a cut is a decision about that cut. Locking the kept neighbour too
        // would stop re-detection from finding new silences inside it.
        var list = ThreeSegments();

        list.MoveBoundary(1, 3.5);

        Assert.Equal(SegmentOrigin.Auto, list.Segments[0].Origin); // the kept side stays open to re-detection
        Assert.Equal(SegmentOrigin.Manual, list.Segments[1].Origin); // the cut was shaped by hand
        Assert.Equal(SegmentOrigin.Auto, list.Segments[2].Origin); // an unrelated segment is not touched
    }

    [Fact]
    public void MoveBoundaryBetweenTwoCutsMarksBothManual()
    {
        var list = new SegmentList(Duration,
        [
            Segment.Create(0.0, 3.0, enabled: false, SegmentOrigin.Auto),
            Segment.Create(3.0, 6.0, enabled: false, SegmentOrigin.Auto),
            Segment.Create(6.0, 10.0, enabled: true, SegmentOrigin.Auto),
        ]);

        list.MoveBoundary(1, 3.5);

        Assert.Equal(SegmentOrigin.Manual, list.Segments[0].Origin);
        Assert.Equal(SegmentOrigin.Manual, list.Segments[1].Origin);
    }

    [Fact]
    public void MoveBoundaryBetweenTwoKeptSegmentsChangesNoOrigin()
    {
        var list = new SegmentList(Duration);
        list.Split(4.0);

        list.MoveBoundary(1, 4.5);

        Assert.All(list.Segments, s => Assert.True(s.Origin == SegmentOrigin.Auto));
    }

    [Fact]
    public void MoveBoundaryPastTheNextBoundaryIsClampedSoTheNeighbourKeepsAMinimumLength()
    {
        var list = ThreeSegments();

        // Try to drag the 3.0 boundary to 7.0, past the 6.0 boundary.
        var applied = list.MoveBoundary(1, 7.0);

        Assert.True(applied < 6.0, $"{applied} < 6.0");
        Assert.True(applied > 3.0, $"{applied} > 3.0");
        Assert.Equal(6.0 - SegmentList.MinSegmentLength, applied, 1e-9);
        Assert.Equal(3, list.Segments.Count); // clamping never deletes a segment
        Assert.Equal(SegmentList.MinSegmentLength, list.Segments[1].Duration, 1e-9);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void MoveBoundaryBeforeThePreviousBoundaryIsClamped()
    {
        var list = ThreeSegments();

        // Try to drag the 6.0 boundary to 1.0, past the 3.0 boundary.
        var applied = list.MoveBoundary(2, 1.0);

        Assert.Equal(3.0 + SegmentList.MinSegmentLength, applied, 1e-9);
        Assert.Equal(3, list.Segments.Count);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void MoveBoundaryOfTheFirstBoundaryCannotGoBelowZero()
    {
        var list = new SegmentList(Duration);
        list.Split(2.0);

        var applied = list.MoveBoundary(1, -5.0);

        Assert.Equal(SegmentList.MinSegmentLength, applied, 1e-9);
        Assert.Equal(0.0, list.Segments[0].Start);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Theory]
    [InlineData(0)]   // the start of the timeline is not movable
    [InlineData(3)]   // the end of the timeline is not movable
    [InlineData(-1)]
    [InlineData(4)]
    public void MoveBoundaryRejectsTheFixedEndsAndOutOfRangeIndices(int boundaryIndex)
    {
        var list = ThreeSegments();

        Action act = () => list.MoveBoundary(boundaryIndex, 5.0);

        Assert.Throws<ArgumentOutOfRangeException>(act);
    }

    [Fact]
    public void MoveBoundaryWithANonFiniteTimeThrows()
    {
        var list = ThreeSegments();

        Action act = () => list.MoveBoundary(1, double.NaN);

        Assert.Throws<ArgumentOutOfRangeException>(act);
    }

    // ---- Toggle -------------------------------------------------------------------------------

    [Fact]
    public void ToggleFlipsEnabledAndMarksTheSegmentManual()
    {
        var list = new SegmentList(Duration);

        list.Toggle(0);

        Assert.False(list.Segments[0].Enabled);
        Assert.Equal(SegmentOrigin.Manual, list.Segments[0].Origin);
        PartitionAssert.Valid(list.Segments, Duration);

        list.Toggle(0);

        Assert.True(list.Segments[0].Enabled);
    }

    [Fact]
    public void TogglePreservesIdBoundsAndReason()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 4.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(4.0, 10.0, enabled: false, SegmentOrigin.Auto, "silence 6s"),
        };
        var list = new SegmentList(Duration, segments);

        list.Toggle(1);

        Assert.Equal(segments[1].Id, list.Segments[1].Id);
        Assert.Equal(4.0, list.Segments[1].Start);
        Assert.Equal(10.0, list.Segments[1].End);
        Assert.Equal("silence 6s", list.Segments[1].Reason);
    }

    [Fact]
    public void ToggleFirstAndLastSegmentsLeavesThePartitionIntact()
    {
        var list = ThreeSegments();

        list.Toggle(0);
        list.Toggle(2);

        Assert.False(list.Segments[0].Enabled);
        Assert.False(list.Segments[2].Enabled);
        Assert.Equal(0.0, list.Segments[0].Start);
        Assert.Equal(Duration, list.Segments[2].End);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void ToggleOnASingleSegmentProjectWorksBothWays()
    {
        var list = new SegmentList(Duration);

        list.Toggle(0);
        Assert.Single(list.Segments, s => !s.Enabled);

        list.Toggle(0);
        Assert.Single(list.Segments, s => s.Enabled);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void ToggleOutOfRangeThrows(int index)
    {
        var list = ThreeSegments();

        var act = () => list.Toggle(index);

        Assert.Throws<ArgumentOutOfRangeException>(act);
    }

    // ---- MergeAdjacentSameState ---------------------------------------------------------------

    [Fact]
    public void MergeAdjacentSameStateJoinsRunsOfEqualEnabledState()
    {
        var list = new SegmentList(Duration);
        list.Split(2.0);
        list.Split(4.0);
        list.Split(6.0);
        list.Split(8.0);
        // [0,2) on, [2,4) on, [4,6) off, [6,8) off, [8,10) on
        list.Toggle(2);
        list.Toggle(3);

        list.MergeAdjacentSameState();

        Assert.Equal(3, list.Segments.Count);
        Assert.Equal(0.0, list.Segments[0].Start);
        Assert.Equal(4.0, list.Segments[0].End);
        Assert.True(list.Segments[0].Enabled);
        Assert.Equal(4.0, list.Segments[1].Start);
        Assert.Equal(8.0, list.Segments[1].End);
        Assert.False(list.Segments[1].Enabled);
        Assert.Equal(8.0, list.Segments[2].Start);
        Assert.Equal(10.0, list.Segments[2].End);
        Assert.True(list.Segments[2].Enabled);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void MergeAdjacentSameStateKeepsTheFirstSegmentsIdAndReason()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 4.0, enabled: false, SegmentOrigin.Auto, "silence 4s"),
            Segment.Create(4.0, 10.0, enabled: false, SegmentOrigin.Auto, "silence 6s"),
        };
        var list = new SegmentList(Duration, segments);

        list.MergeAdjacentSameState();

        Assert.Single(list.Segments);
        Assert.Equal(segments[0].Id, list.Segments[0].Id);
        Assert.Equal("silence 4s", list.Segments[0].Reason);
    }

    [Fact]
    public void MergeAdjacentSameStatePromotesOriginToManualIfAnyPartWasManual()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 4.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(4.0, 10.0, enabled: true, SegmentOrigin.Manual),
        };
        var list = new SegmentList(Duration, segments);

        list.MergeAdjacentSameState();

        Assert.Equal(SegmentOrigin.Manual, list.Segments[0].Origin);
    }

    [Fact]
    public void MergeAdjacentSameStateOnAlternatingStatesChangesNothing()
    {
        var list = ThreeSegments();
        var before = list.Segments.ToList();

        list.MergeAdjacentSameState();

        Assert.Equal(before, list.Segments);
    }

    [Fact]
    public void MergeAdjacentSameStateOnASingleSegmentChangesNothing()
    {
        var list = new SegmentList(Duration);
        var before = list.Segments[0];

        list.MergeAdjacentSameState();

        Assert.Equal(before, Assert.Single(list.Segments));
    }

    // ---- Restore ------------------------------------------------------------------------------

    [Fact]
    public void RestoreReplacesThePartitionWithTheGivenSegments()
    {
        var list = ThreeSegments();
        var snapshot = list.Segments.ToArray();
        list.Toggle(0);
        list.Split(8.0);

        list.Restore(snapshot);

        Assert.Equal(snapshot, list.Segments);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void RestoreRaisesChangedOnce()
    {
        var list = ThreeSegments();
        var snapshot = list.Segments.ToArray();
        list.Toggle(0);
        var count = 0;
        list.Changed += (_, _) => count++;

        list.Restore(snapshot);

        Assert.Equal(1, count);
    }

    [Fact]
    public void RestoreRejectsSegmentsThatDoNotPartitionTheDuration()
    {
        var list = ThreeSegments();
        var before = list.Segments.ToList();
        var bad = new[]
        {
            Segment.Create(0.0, 4.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(5.0, 10.0, enabled: true, SegmentOrigin.Auto),
        };

        var act = () => list.Restore(bad);

        Assert.Throws<InvalidPartitionException>(act);
        Assert.Equal(before, list.Segments);
    }

    // ---- SetRange -----------------------------------------------------------------------------

    [Fact]
    public void SetRangeInsideOneSegmentCarvesOutAManualSectionWithTheRequestedState()
    {
        var list = new SegmentList(Duration);

        var index = list.SetRange(2.0, 5.0, enabled: false);

        Assert.Equal(1, index);
        Assert.Equal(3, list.Segments.Count);
        Assert.Equal(0.0, list.Segments[0].Start);
        Assert.Equal(2.0, list.Segments[0].End);
        Assert.True(list.Segments[0].Enabled);
        Assert.Equal(SegmentOrigin.Auto, list.Segments[0].Origin);
        Assert.Equal(2.0, list.Segments[1].Start);
        Assert.Equal(5.0, list.Segments[1].End);
        Assert.False(list.Segments[1].Enabled);
        Assert.Equal(SegmentOrigin.Manual, list.Segments[1].Origin);
        Assert.Null(list.Segments[1].Reason);
        Assert.Equal(5.0, list.Segments[2].Start);
        Assert.Equal(10.0, list.Segments[2].End);
        Assert.True(list.Segments[2].Enabled);
        Assert.Equal(SegmentOrigin.Auto, list.Segments[2].Origin);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void SetRangeSpanningSeveralSegmentsCollapsesThemIntoOne()
    {
        var list = ThreeSegments(); // [0,3) on, [3,6) off, [6,10) on

        var index = list.SetRange(1.0, 8.0, enabled: true);

        Assert.Equal(1, index);
        Assert.Equal(3, list.Segments.Count);
        Assert.Equal(0.0, list.Segments[0].Start);
        Assert.Equal(1.0, list.Segments[0].End);
        Assert.True(list.Segments[0].Enabled);
        Assert.Equal(1.0, list.Segments[1].Start);
        Assert.Equal(8.0, list.Segments[1].End);
        Assert.True(list.Segments[1].Enabled);
        Assert.Equal(SegmentOrigin.Manual, list.Segments[1].Origin);
        Assert.Equal(8.0, list.Segments[2].Start);
        Assert.Equal(10.0, list.Segments[2].End);
        Assert.True(list.Segments[2].Enabled);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void SetRangeKeepsTheFirstCoveredSegmentsId()
    {
        var list = ThreeSegments();
        var originalId = list.Segments[1].Id;

        list.SetRange(3.0, 8.0, enabled: false);

        Assert.Equal(originalId, list.Segments[1].Id);
    }

    [Fact]
    public void SetRangeOnExistingBoundariesDoesNotAddSegments()
    {
        var list = ThreeSegments();

        var index = list.SetRange(3.0, 6.0, enabled: true);

        Assert.Equal(1, index);
        Assert.Equal(3, list.Segments.Count);
        Assert.Equal(3.0, list.Segments[1].Start);
        Assert.Equal(6.0, list.Segments[1].End);
        Assert.True(list.Segments[1].Enabled);
        Assert.Equal(SegmentOrigin.Manual, list.Segments[1].Origin);
    }

    [Fact]
    public void SetRangeDoesNotMergeWithNeighboursOutsideTheRange()
    {
        var list = ThreeSegments(); // [0,3) on, [3,6) off, [6,10) on

        list.SetRange(4.0, 5.0, enabled: true);

        // [0,3) on, [3,4) off, [4,5) on, [5,6) off, [6,10) on: the new kept section stays distinct.
        Assert.Equal(5, list.Segments.Count);
        Assert.Equal(4.0, list.Segments[2].Start);
        Assert.Equal(5.0, list.Segments[2].End);
        Assert.True(list.Segments[2].Enabled);
        Assert.False(list.Segments[1].Enabled);
        Assert.False(list.Segments[3].Enabled);
    }

    [Fact]
    public void SetRangeClampsToTheTimeline()
    {
        var list = new SegmentList(Duration);

        var index = list.SetRange(-1.0, 4.0, enabled: false);

        Assert.Equal(0, index);
        Assert.Equal(2, list.Segments.Count);
        Assert.Equal(0.0, list.Segments[0].Start);
        Assert.Equal(4.0, list.Segments[0].End);
        Assert.False(list.Segments[0].Enabled);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void SetRangeOverTheWholeTimelineYieldsASingleSegment()
    {
        var list = ThreeSegments();

        var index = list.SetRange(0.0, Duration, enabled: false);

        Assert.Equal(0, index);
        Assert.False(Assert.Single(list.Segments).Enabled);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Theory]
    [InlineData(4.0, 4.0)]
    [InlineData(4.0, 4.0005)]
    [InlineData(5.0, 4.0)]
    public void SetRangeShorterThanTheMinimumLengthChangesNothing(double start, double end)
    {
        var list = ThreeSegments();
        var before = list.Segments.ToList();
        var count = 0;
        list.Changed += (_, _) => count++;

        var index = list.SetRange(start, end, enabled: true);

        Assert.Equal(-1, index);
        Assert.Equal(0, count);
        Assert.Equal(before, list.Segments);
    }

    [Fact]
    public void SetRangeRaisesChangedOnce()
    {
        var list = ThreeSegments();
        var count = 0;
        list.Changed += (_, _) => count++;

        list.SetRange(1.0, 8.0, enabled: false);

        Assert.Equal(1, count);
    }

    [Fact]
    public void SplitStillRaisesChangedOnce()
    {
        var list = ThreeSegments();
        var count = 0;
        list.Changed += (_, _) => count++;

        list.Split(1.0);

        Assert.Equal(1, count);
    }

    // ---- Dissolve -----------------------------------------------------------------------------

    [Fact]
    public void DissolveBetweenSameStateNeighboursMergesAllThreeIntoTheLeftOne()
    {
        var list = ThreeSegments(); // [0,3) on, [3,6) off, [6,10) on
        var leftId = list.Segments[0].Id;

        var changed = list.Dissolve(1);

        Assert.True(changed);
        Assert.Single(list.Segments);
        Assert.Equal(leftId, list.Segments[0].Id);
        Assert.Equal(0.0, list.Segments[0].Start);
        Assert.Equal(10.0, list.Segments[0].End);
        Assert.True(list.Segments[0].Enabled);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void DissolveMergedSegmentTakesTheFirstReasonAndTheMergedOrigin()
    {
        var list = new SegmentList(Duration,
        [
            Segment.Create(0.0, 3.0, enabled: false, SegmentOrigin.Auto, "silence 3s"),
            Segment.Create(3.0, 6.0, enabled: true, SegmentOrigin.Manual),
            Segment.Create(6.0, 10.0, enabled: false, SegmentOrigin.Manual, "silence 4s"),
        ]);

        list.Dissolve(1);

        Assert.Single(list.Segments);
        Assert.Equal("silence 3s", list.Segments[0].Reason);
        Assert.Equal(SegmentOrigin.Manual, list.Segments[0].Origin);
        Assert.False(list.Segments[0].Enabled);
    }

    [Fact]
    public void DissolveBetweenDifferentNeighboursJoinsTheLeftOne()
    {
        var list = new SegmentList(Duration,
        [
            Segment.Create(0.0, 3.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(3.0, 6.0, enabled: false, SegmentOrigin.Manual),
            Segment.Create(6.0, 8.0, enabled: true, SegmentOrigin.Manual),
            Segment.Create(8.0, 10.0, enabled: true, SegmentOrigin.Auto),
        ]);
        var left = list.Segments[1];
        var right = list.Segments[3];

        // Left is a cut, right is kept: the region joins the left one.
        list.Dissolve(2);

        Assert.Equal(3, list.Segments.Count);
        Assert.Equal(left with { End = 8.0 }, list.Segments[1]);
        Assert.Equal(right, list.Segments[2]);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void DissolveFirstSegmentJoinsTheRightOne()
    {
        var list = ThreeSegments();
        var right = list.Segments[1];

        list.Dissolve(0);

        Assert.Equal(2, list.Segments.Count);
        Assert.Equal(right with { Start = 0.0 }, list.Segments[0]);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void DissolveLastSegmentJoinsTheLeftOne()
    {
        var list = ThreeSegments();
        var left = list.Segments[1];

        list.Dissolve(2);

        Assert.Equal(2, list.Segments.Count);
        Assert.Equal(left with { End = Duration }, list.Segments[1]);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void DissolveTheOnlySegmentChangesNothing()
    {
        var list = new SegmentList(Duration);
        var before = list.Segments[0];
        var count = 0;
        list.Changed += (_, _) => count++;

        var changed = list.Dissolve(0);

        Assert.False(changed);
        Assert.Equal(0, count);
        Assert.Equal(before, Assert.Single(list.Segments));
    }

    [Fact]
    public void DissolveRaisesChangedOnce()
    {
        var list = ThreeSegments();
        var count = 0;
        list.Changed += (_, _) => count++;

        list.Dissolve(1);

        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void DissolveRejectsAnIndexOutsideTheList(int index)
    {
        var list = ThreeSegments();

        Action act = () => list.Dissolve(index);

        Assert.Throws<ArgumentOutOfRangeException>(act);
    }

    // ---- change notification ------------------------------------------------------------------

    [Fact]
    public void MutationsRaiseChangedOnceEach()
    {
        var list = ThreeSegments();
        var count = 0;
        list.Changed += (_, _) => count++;

        list.Split(1.0);              // [0,1) on, [1,3) on, [3,6) off, [6,10) on
        list.Toggle(1);               // [1,3) off: now adjacent to [3,6) off
        list.MoveBoundary(1, 0.5);
        list.MergeAdjacentSameState(); // joins [0.5,3) and [3,6)

        Assert.Equal(4, count);
    }

    [Fact]
    public void NoOpSplitDoesNotRaiseChanged()
    {
        var list = ThreeSegments();
        var count = 0;
        list.Changed += (_, _) => count++;

        list.Split(3.0);

        Assert.Equal(0, count);
    }
}
