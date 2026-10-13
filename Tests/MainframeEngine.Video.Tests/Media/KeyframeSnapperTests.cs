using MainframeEngine.Video.Models;
using MainframeEngine.Video.Media.Export;

namespace MainframeEngine.Video.Tests.Media;

public sealed class KeyframeSnapperTests
{
    private static Segment Kept(double start, double end) => Segment.Create(start, end, enabled: true, SegmentOrigin.Auto);

    private static readonly double[] Keyframes = [0.0, 2.0, 4.0, 6.0, 8.0, 10.0];

    [Fact]
    public void StartMovesBackToThePrecedingKeyframeAndTheEndStaysExact()
    {
        // Stream copy must begin on a keyframe but can stop anywhere, so a kept region only ever
        // grows: nothing the user kept is lost, some extra is included.
        var ranges = KeyframeSnapper.Snap([Kept(2.9, 7.2)], Keyframes, duration: 12.0);

        Assert.Equal([(2.0, 7.2)], ranges);
    }

    [Fact]
    public void AStartAlreadyOnAKeyframeStaysPut()
    {
        var ranges = KeyframeSnapper.Snap([Kept(4.0, 7.0)], Keyframes, duration: 12.0);

        Assert.Equal([(4.0, 7.0)], ranges);
    }

    [Fact]
    public void AStartBeforeTheFirstKeyframeBecomesZero()
    {
        var ranges = KeyframeSnapper.Snap([Kept(0.5, 3.0)], [2.0, 4.0], duration: 12.0);

        Assert.Equal([(0.0, 3.0)], ranges);
    }

    [Fact]
    public void TheEndOfFileIsKeptAsIs()
    {
        var ranges = KeyframeSnapper.Snap([Kept(9.5, 12.0)], Keyframes, duration: 12.0);

        Assert.Equal([(8.0, 12.0)], ranges);
    }

    [Fact]
    public void RangesThatOverlapAfterSnappingAreMerged()
    {
        var ranges = KeyframeSnapper.Snap([Kept(0.0, 3.1), Kept(3.1, 5.0)], Keyframes, duration: 12.0);

        Assert.Equal([(0.0, 5.0)], ranges);
    }

    [Fact]
    public void ACutShorterThanTheGapToThePreviousKeyframeDisappears()
    {
        // Kept [0,3) and [3.5,5): the second range snaps back to 2.0, swallowing the cut.
        var segments = new[] { Kept(0.0, 3.0), Segment.Create(3.0, 3.5, enabled: false, SegmentOrigin.Auto), Kept(3.5, 5.0) };

        var ranges = KeyframeSnapper.Snap(segments, Keyframes, duration: 12.0);

        Assert.Equal([(0.0, 5.0)], ranges);
    }

    [Fact]
    public void DisabledSegmentsAreIgnored()
    {
        var segments = new[] { Kept(0.0, 2.0), Segment.Create(2.0, 8.0, enabled: false, SegmentOrigin.Auto), Kept(8.0, 12.0) };

        var ranges = KeyframeSnapper.Snap(segments, Keyframes, duration: 12.0);

        Assert.Equal([(0.0, 2.0), (8.0, 12.0)], ranges);
    }

    [Fact]
    public void WithoutKeyframesTheRangesAreUnchanged()
    {
        var ranges = KeyframeSnapper.Snap([Kept(2.9, 7.2)], [], duration: 12.0);

        Assert.Equal([(2.9, 7.2)], ranges);
    }

    [Fact]
    public void ParsesFfprobePacketCsvLinesForKeyframes()
    {
        var lines = new[]
        {
            "0.000000,K__",
            "0.033333,___",
            "2.000000,K__",
            "N/A,K__",
            "4.000000,K_D",
            "",
        };

        Assert.Equal([0.0, 2.0, 4.0], KeyframeSnapper.ParseKeyframes(lines));
    }
}
