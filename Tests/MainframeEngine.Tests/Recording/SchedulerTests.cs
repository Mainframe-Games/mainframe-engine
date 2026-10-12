namespace MainframeEngine.Tests.Recording;

/// <summary>Constant-frame-rate output from irregular frames, and exact sample counts per video frame.</summary>
public sealed class SchedulerTests
{
    private static int[] Run(FrameScheduler s, IEnumerable<double> times, Func<int, bool>? accept = null)
    {
        var written = new List<int>();
        var n = 0;
        foreach (var t in times)
        {
            if (!s.ShouldCapture(t))
            {
                written.Add(-1); // never copied
                continue;
            }

            var copies = s.CopiesFor(t);
            if (copies > 0 && (accept?.Invoke(n++) ?? true))
                s.Committed(copies);
            written.Add(copies);
        }

        return [.. written];
    }

    [Fact]
    public void ThirtyHertzInputIsDoubledToSixty()
    {
        var times = Enumerable.Range(0, 4).Select(i => i / 30.0);
        Assert.Equal([1, 2, 2, 2], Run(new FrameScheduler(60, offline: false), times));
    }

    [Fact]
    public void OneTwentyHertzInputSkipsEveryOtherFrameWithoutCopying()
    {
        var times = Enumerable.Range(0, 6).Select(i => i / 120.0);
        Assert.Equal([1, -1, 1, -1, 1, -1], Run(new FrameScheduler(60, offline: false), times));
    }

    [Fact]
    public void ADroppedWriteIsMadeUpByTheNextFrame()
    {
        var s = new FrameScheduler(60, offline: false);
        var times = Enumerable.Range(0, 3).Select(i => i / 60.0);
        var result = Run(s, times, accept: n => n != 1); // the writer queue was full for the second frame
        Assert.Equal([1, 1, 2], result);
        Assert.Equal(3, s.WrittenFrames);
    }

    [Fact]
    public void OfflineWritesEveryFrameOnce()
    {
        var s = new FrameScheduler(60, offline: true);
        Assert.Equal([1, 1, 1], Run(s, [0, 0, 0])); // offline ignores the clock
        Assert.Equal(3, s.WrittenFrames);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(60)]
    [InlineData(144)]
    public void SamplesPerFrameSumExactlyToTheTotal(int fps)
    {
        long sum = 0;
        for (long f = 0; f < 10_000; f++)
        {
            var n = SampleScheduler.SamplesForFrame(f, 48_000, fps);
            Assert.InRange(n, 48_000 / fps, 48_000 / fps + 1);
            sum += n;
        }

        Assert.Equal(SampleScheduler.SamplesThrough(10_000, 48_000, fps), sum);
        Assert.Equal((10_000L * 48_000 + fps / 2) / fps, sum);
    }

    [Fact]
    public void SixtyFpsIsExactlyEightHundredSamplesPerFrame()
    {
        Assert.Equal(800, SampleScheduler.SamplesForFrame(12_345, 48_000, 60));
    }
}
