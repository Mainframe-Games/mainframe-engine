using MainframeEngine.Audio.Music;

namespace MainframeEngine.Tests.Audio.Music;

public sealed class MusicGridTests
{
    private static readonly MusicGrid Grid120 = new(48000, 120, 4, 4); // 24 000 frames per beat

    [Fact]
    public void BeatsRoundOnceFromTheBeatNumber()
    {
        // 123 BPM at 48 kHz: 23 414.634… frames per beat; every beat is rounded from its own number, never accumulated.
        var grid = new MusicGrid(48000, 123, 4, 4);
        Assert.Equal(0, grid.FrameOfBeat(0L));
        Assert.Equal(23415, grid.FrameOfBeat(1L));
        Assert.Equal(46829, grid.FrameOfBeat(2L));
        for (long beat = 0; beat < 10_000; beat += 37)
            Assert.Equal((long)Math.Round(beat * 48000 * 60.0 / 123, MidpointRounding.AwayFromZero), grid.FrameOfBeat(beat));
    }

    [Fact]
    public void ASectionIsTheSumOfItsBarsAndLoopsWithoutDrift()
    {
        var grid = new MusicGrid(48000, 123, 4, 4);
        var length = grid.LengthFrames(16);
        long sum = 0;
        for (var bar = 0; bar < 16; bar++)
            sum += grid.FrameOfBeat((bar + 1) * 4L) - grid.FrameOfBeat(bar * 4L);
        Assert.Equal(length, sum);

        // After 1 000 loops a beat is still the loop start plus its in-section frame: the clock is whole sections.
        const long Loops = 1000;
        var start = Loops * length;
        for (var beat = 0L; beat < 64; beat++)
        {
            var frame = start + grid.FrameOfBeat(beat);
            Assert.Equal(beat, grid.BeatAt(frame - start));
            Assert.Equal(frame, start + grid.FrameOfBeat(grid.BeatAtOrAfter(frame - start)));
        }
    }

    [Fact]
    public void BeatLookupsAgreeWithFrameOfBeat()
    {
        var grid = new MusicGrid(44100, 97.5, 7, 8);
        for (long frame = 0; frame < 200_000; frame += 113)
        {
            var at = grid.BeatAt(frame);
            var after = grid.BeatAtOrAfter(frame);
            Assert.True(grid.FrameOfBeat(at) <= frame && grid.FrameOfBeat(at + 1) > frame);
            Assert.True(grid.FrameOfBeat(after) >= frame && (after == 0 || grid.FrameOfBeat(after - 1) < frame));
        }
    }

    [Fact]
    public void SixEightCountsEighthNotes()
    {
        var grid = new MusicGrid(48000, 120, 6, 8); // an eighth = 12 000 frames
        Assert.Equal(12000, grid.FrameOfBeat(1L));
        Assert.Equal(72000, grid.LengthFrames(1));
        Assert.Equal(72000, grid.NextQuantum(1, 144000, MusicQuantize.Bar));
    }

    [Theory]
    [InlineData(MusicQuantize.Immediate, 30000, 30000)]
    [InlineData(MusicQuantize.Beat, 30000, 48000)]
    [InlineData(MusicQuantize.Beat, 48000, 48000)] // on a boundary: that boundary
    [InlineData(MusicQuantize.Bar, 1, 96000)]
    [InlineData(MusicQuantize.Bar, 96000, 96000)]
    [InlineData(MusicQuantize.Bar, 96001, 192000)]
    [InlineData(MusicQuantize.SectionEnd, 0, 384000)]
    [InlineData(MusicQuantize.Bar, 300000, 384000)] // the last bar line is the end
    [InlineData(MusicQuantize.Bar, 384000, 384000)]
    public void NextQuantumInAFourBarSection(MusicQuantize quantize, long frame, long expected) =>
        Assert.Equal(expected, Grid120.NextQuantum(frame, 384000, quantize));

    [Fact]
    public void EveryNBarsCountsFromTheSectionStartAndTheEndIsAlwaysABoundary()
    {
        var length = Grid120.LengthFrames(5);
        Assert.Equal(192000, Grid120.NextQuantum(1, length, MusicQuantize.Bars, 2));
        Assert.Equal(384000, Grid120.NextQuantum(192001, length, MusicQuantize.Bars, 2));
        Assert.Equal(length, Grid120.NextQuantum(384001, length, MusicQuantize.Bars, 2)); // bar 6 does not exist: the end
        Assert.Equal(length, Grid120.NextQuantum(1, length, MusicQuantize.Bars, 9));
    }

    [Fact]
    public void ExitMarkersAndPastTheEnd()
    {
        long[] markers = [100000, 250000];
        Assert.Equal(100000, Grid120.NextQuantum(0, 384000, MusicQuantize.ExitMarker, exitMarkers: markers));
        Assert.Equal(250000, Grid120.NextQuantum(100001, 384000, MusicQuantize.ExitMarker, exitMarkers: markers));
        Assert.Equal(384000, Grid120.NextQuantum(250001, 384000, MusicQuantize.ExitMarker, exitMarkers: markers));
        Assert.Equal(384000, Grid120.NextQuantum(0, 384000, MusicQuantize.ExitMarker));
        Assert.Equal(-1, Grid120.NextQuantum(384001, 384000, MusicQuantize.Beat));
    }

    [Fact]
    public void FractionalBeatsAndDurations()
    {
        Assert.Equal(36000, Grid120.FrameOfBeat(1.5));
        Assert.Equal(12000, Grid120.FramesForBeats(0.5));
        Assert.Equal(0, Grid120.FramesForBeats(-1));
        Assert.Equal(0, Grid120.FramesForBeats(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MusicGrid(48000, 0, 4, 4));
    }
}
