using MainframeEngine.Video.Media;

namespace MainframeEngine.Video.Tests.Media;

public sealed class WaveformTests
{
    [Fact]
    public void DownsampleMergesGroupsOfPeaks()
    {
        var level = new WaveformLevel(samplesPerBucket: 16, sampleRate: 8000, peaks:
        [
            new Peak(-1, 1), new Peak(-9, 2), new Peak(-3, 8), new Peak(0, 0),
            new Peak(-2, 2), new Peak(-4, 5),
        ]);

        var coarser = level.Downsample(4);

        Assert.Equal(64, coarser.SamplesPerBucket);
        Assert.Equal([new Peak(-9, 8), new Peak(-4, 5)], coarser.Peaks);
    }

    [Fact]
    public void SecondsPerBucketDerivesFromSampleRate()
    {
        var level = new WaveformLevel(samplesPerBucket: 16, sampleRate: 8000, peaks: []);

        Assert.Equal(0.002, level.SecondsPerBucket, 1e-12);
    }

    [Fact]
    public void BuildCreatesLevelsFromFinestByTheGivenFactor()
    {
        var finest = new Peak[1000];
        Array.Fill(finest, new Peak(-100, 100));

        var waveform = Waveform.Build(finest, finestSamplesPerBucket: 16, sampleRate: 8000, levelFactor: 4, levelCount: 3);

        Assert.Equal([16, 64, 256], waveform.Levels.Select(l => l.SamplesPerBucket));
        Assert.Equal([1000, 250, 63], waveform.Levels.Select(l => l.Peaks.Count));
        Assert.Equal(8000, waveform.SampleRate);
    }

    [Fact]
    public void LevelForPicksTheFinestLevelNoFinerThanNeeded()
    {
        var finest = new Peak[4096];
        var waveform = Waveform.Build(finest, finestSamplesPerBucket: 16, sampleRate: 8000, levelFactor: 4, levelCount: 4);
        // levels: 0.002s, 0.008s, 0.032s, 0.128s per bucket

        Assert.Equal(64, waveform.LevelFor(secondsPerPixel: 0.010).SamplesPerBucket); // 0.008s buckets are the coarsest that still exceed pixel resolution
        Assert.Equal(16, waveform.LevelFor(secondsPerPixel: 0.001).SamplesPerBucket); // nothing finer exists
        Assert.Equal(1024, waveform.LevelFor(secondsPerPixel: 10.0).SamplesPerBucket); // nothing coarser exists
    }

    [Fact]
    public void DurationComesFromTheFinestLevel()
    {
        var finest = new Peak[500]; // 500 * 16 samples / 8000 Hz = 1 s
        var waveform = Waveform.Build(finest, finestSamplesPerBucket: 16, sampleRate: 8000, levelFactor: 4, levelCount: 2);

        Assert.Equal(1.0, waveform.DurationSeconds, 1e-9);
    }
}

public sealed class WaveformSnapTests
{
    // 8000 Hz, 16 samples per bucket => 2 ms per bucket. 500 buckets = 1 s.
    private static Waveform Loud(int quietBucket = -1, int quietRunLength = 1)
    {
        var peaks = new Peak[500];
        Array.Fill(peaks, new Peak(-8000, 8000));
        for (var i = 0; i < quietRunLength && quietBucket >= 0; i++)
        {
            peaks[quietBucket + i] = new Peak(-50, 50);
        }

        return Waveform.Build(peaks, 16, 8000, 4, 2);
    }

    [Fact]
    public void SnapsToTheQuietestBucketWithinTheWindow()
    {
        // bucket 250 spans [0.500, 0.502); its centre is 0.501
        var waveform = Loud(quietBucket: 250);

        var snapped = waveform.SnapToZeroCrossing(0.510, windowSeconds: 0.020);

        Assert.Equal(0.501, snapped, 1e-9);
    }

    [Fact]
    public void DoesNotSnapBeyondTheWindow()
    {
        var waveform = Loud(quietBucket: 250);

        var snapped = waveform.SnapToZeroCrossing(0.600, windowSeconds: 0.020);

        Assert.Equal(0.600, snapped, 0.002); // nothing quieter nearby, so it stays put (within one bucket)
    }

    [Fact]
    public void TiesGoToTheBucketClosestToTheRequestedTime()
    {
        var waveform = Loud(); // uniform amplitude everywhere

        var snapped = waveform.SnapToZeroCrossing(0.300, windowSeconds: 0.020);

        Assert.Equal(0.300, snapped, 0.002);
    }

    [Fact]
    public void WithinAQuietRunItStaysNearTheRequestedTime()
    {
        // buckets 200..219 quiet: [0.400, 0.440)
        var waveform = Loud(quietBucket: 200, quietRunLength: 20);

        var snapped = waveform.SnapToZeroCrossing(0.420, windowSeconds: 0.020);

        Assert.Equal(0.420, snapped, 0.002);
    }

    [Fact]
    public void ClampsToTheWaveformRange()
    {
        var waveform = Loud();

        Assert.True(waveform.SnapToZeroCrossing(-1.0, 0.020) >= 0.0);
        Assert.True(waveform.SnapToZeroCrossing(5.0, 0.020) <= waveform.DurationSeconds);
    }

    // ---- Envelope ---------------------------------------------------------------------------

    [Fact]
    public void EnvelopeAveragesPeakAmplitudeOverEachBucket()
    {
        // 2 ms peaks; a 10 ms envelope bucket covers five of them.
        var finest = new Peak[10];
        Array.Fill(finest, new Peak(-100, 60), 0, 5);   // amplitude 100
        Array.Fill(finest, new Peak(-20, 1000), 5, 5);  // amplitude 1000
        var waveform = Waveform.Build(finest, finestSamplesPerBucket: 16, sampleRate: 8000, levelFactor: 4, levelCount: 1);

        Assert.Equal([100.0, 1000.0], waveform.Envelope(0.010));
    }

    [Fact]
    public void EnvelopeAveragesAShortTrailingBucketOverThePeaksItHas()
    {
        var finest = new Peak[7];
        Array.Fill(finest, new Peak(-100, 100), 0, 5);
        finest[5] = new Peak(-200, 0);
        finest[6] = new Peak(0, 400);
        var waveform = Waveform.Build(finest, finestSamplesPerBucket: 16, sampleRate: 8000, levelFactor: 4, levelCount: 1);

        Assert.Equal([100.0, 300.0], waveform.Envelope(0.010));
    }
}
