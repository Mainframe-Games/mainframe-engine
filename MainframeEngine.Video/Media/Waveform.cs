namespace MainframeEngine.Video.Media;

/// <summary>One zoom level of a waveform: peaks at a fixed number of source samples per bucket.</summary>
public sealed class WaveformLevel
{
    public WaveformLevel(int samplesPerBucket, int sampleRate, IReadOnlyList<Peak> peaks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(samplesPerBucket, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentNullException.ThrowIfNull(peaks);
        SamplesPerBucket = samplesPerBucket;
        SampleRate = sampleRate;
        Peaks = peaks;
    }

    public int SamplesPerBucket { get; }

    public int SampleRate { get; }

    public IReadOnlyList<Peak> Peaks { get; }

    public double SecondsPerBucket => (double)SamplesPerBucket / SampleRate;

    /// <summary>Merges every <paramref name="factor"/> peaks into one. A short trailing group is kept.</summary>
    public WaveformLevel Downsample(int factor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(factor, 0);

        var count = (Peaks.Count + factor - 1) / factor;
        var result = new Peak[count];
        for (var i = 0; i < count; i++)
        {
            var min = short.MaxValue;
            var max = short.MinValue;
            var end = Math.Min(Peaks.Count, (i + 1) * factor);
            for (var j = i * factor; j < end; j++)
            {
                var p = Peaks[j];
                if (p.Min < min)
                {
                    min = p.Min;
                }

                if (p.Max > max)
                {
                    max = p.Max;
                }
            }

            result[i] = new Peak(min, max);
        }

        return new WaveformLevel(SamplesPerBucket * factor, SampleRate, result);
    }
}

/// <summary>
/// A reduced waveform at several zoom levels, finest first. Built once per source and cached for
/// the life of the project.
/// </summary>
public sealed class Waveform
{
    private Waveform(int sampleRate, IReadOnlyList<WaveformLevel> levels)
    {
        SampleRate = sampleRate;
        Levels = levels;
    }

    public int SampleRate { get; }

    /// <summary>Zoom levels ordered from finest (smallest bucket) to coarsest.</summary>
    public IReadOnlyList<WaveformLevel> Levels { get; }

    public double DurationSeconds => Levels[0].Peaks.Count * Levels[0].SecondsPerBucket;

    /// <summary>
    /// Builds <paramref name="levelCount"/> levels starting from the finest peaks, each
    /// <paramref name="levelFactor"/> times coarser than the last.
    /// </summary>
    public static Waveform Build(IReadOnlyList<Peak> finestPeaks, int finestSamplesPerBucket, int sampleRate, int levelFactor, int levelCount)
    {
        ArgumentNullException.ThrowIfNull(finestPeaks);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(levelFactor, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(levelCount, 0);

        var levels = new List<WaveformLevel>(levelCount)
        {
            new(finestSamplesPerBucket, sampleRate, finestPeaks),
        };
        for (var i = 1; i < levelCount; i++)
        {
            levels.Add(levels[^1].Downsample(levelFactor));
        }

        return new Waveform(sampleRate, levels);
    }

    /// <summary>
    /// Nudges <paramref name="time"/> to the quietest point within ±<paramref name="windowSeconds"/>
    /// so a cut placed there lands in a lull rather than mid-waveform. With only min/max peaks a
    /// true zero crossing is not knowable, so "quietest 2 ms bucket" is the practical equivalent;
    /// ties go to the bucket nearest the requested time. Returns the bucket centre.
    /// </summary>
    public double SnapToZeroCrossing(double time, double windowSeconds)
    {
        var level = Levels[0];
        var count = level.Peaks.Count;
        if (count == 0)
        {
            return time;
        }

        var spb = level.SecondsPerBucket;
        var duration = count * spb;
        time = Math.Clamp(time, 0, duration);

        var centre = Math.Clamp((int)(time / spb), 0, count - 1);
        var radius = Math.Max(0, (int)Math.Round(windowSeconds / spb));
        var lo = Math.Max(0, centre - radius);
        var hi = Math.Min(count - 1, centre + radius);

        var best = centre;
        var bestAmplitude = Amplitude(level.Peaks[centre]);
        for (var i = lo; i <= hi; i++)
        {
            var amplitude = Amplitude(level.Peaks[i]);
            if (amplitude < bestAmplitude
                || (amplitude == bestAmplitude && Math.Abs(i - centre) < Math.Abs(best - centre)))
            {
                best = i;
                bestAmplitude = amplitude;
            }
        }

        return Math.Min(duration, (best + 0.5) * spb);
    }

    /// <summary>
    /// Mean peak amplitude per <paramref name="bucketSeconds"/>, from the finest level: a coarse
    /// loudness envelope good enough to tell speech from pauses. A short trailing bucket is
    /// averaged over the peaks it has.
    /// </summary>
    public double[] Envelope(double bucketSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketSeconds);
        var level = Levels[0];
        var perBucket = Math.Max(1, (int)Math.Round(bucketSeconds / level.SecondsPerBucket));
        var count = (level.Peaks.Count + perBucket - 1) / perBucket;
        var envelope = new double[count];
        for (var i = 0; i < count; i++)
        {
            var end = Math.Min(level.Peaks.Count, (i + 1) * perBucket);
            var sum = 0.0;
            for (var j = i * perBucket; j < end; j++)
            {
                sum += Amplitude(level.Peaks[j]);
            }

            envelope[i] = sum / (end - i * perBucket);
        }

        return envelope;
    }

    private static int Amplitude(Peak p) => Math.Max(-(int)p.Min, (int)p.Max);

    /// <summary>
    /// The level to draw at a given zoom: the coarsest whose buckets are still no wider than one
    /// pixel, so the renderer only ever merges buckets, never stretches them. Falls back to the
    /// finest level when zoomed in past it and the coarsest when zoomed out past it.
    /// </summary>
    public WaveformLevel LevelFor(double secondsPerPixel)
    {
        var chosen = Levels[0];

        // Indexed, not foreach: this runs every frame while drawing, and enumerating the interface allocates.
        for (var i = 0; i < Levels.Count; i++)
        {
            var level = Levels[i];
            if (level.SecondsPerBucket <= secondsPerPixel)
            {
                chosen = level;
            }
            else
            {
                break;
            }
        }

        return chosen;
    }
}
