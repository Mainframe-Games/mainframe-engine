using System.Runtime.CompilerServices;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// A band-limited wavetable: 1–256 single-cycle frames of 2048 samples, stored as eleven mip levels. Level L keeps
/// harmonics 1…1024 &gt;&gt; L (rebuilt by inverse FFT at 8 samples per highest harmonic, 128 at least and 4096 at most,
/// so the Hermite read's images stay ~70 dB down where they fold back past Nyquist), so a note reads
/// the level whose top harmonic stays under <c>max(fs − 20 kHz, fs/2)</c>: nothing audible aliases. Each level's frames
/// have one guard sample before and two after, so the 4-point Hermite read never wraps. Immutable once built; the render
/// thread only reads.
/// </summary>
public sealed class Wavetable
{
    public const int FrameSize = 2048;
    public const int MaxFrames = 256;
    public const int LevelCount = 11;
    private const int Guards = 3;

    private readonly float[][] _levels;
    private readonly int[] _shift; // log2 of each level's length
    private readonly int[] _stride;

    private Wavetable(string source, int frames, float[][] levels, int[] shift)
    {
        Source = source;
        Frames = frames;
        _levels = levels;
        _shift = shift;
        _stride = [.. shift.Select(static s => (1 << s) + Guards)];
    }

    public string Source { get; }

    public int Frames { get; }

    /// <summary>Samples per cycle at <paramref name="level"/>.</summary>
    public int LevelLength(int level) => 1 << _shift[level];

    /// <summary>
    /// Builds the mip levels from <paramref name="frames"/> (each <see cref="FrameSize"/> samples; DC removed, the whole
    /// table peak-normalized; at most <see cref="MaxFrames"/>). UI or worker thread: allocates and runs FFTs.
    /// </summary>
    public static Wavetable Build(string source, IReadOnlyList<float[]> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var count = Math.Clamp(frames.Count, 1, MaxFrames);
        var shift = new int[LevelCount];
        var levels = new float[LevelCount][];
        for (var l = 0; l < LevelCount; l++)
        {
            var harmonics = 1024 >> l;
            var length = Math.Clamp(8 * harmonics, 128, 2 * FrameSize);
            shift[l] = System.Numerics.BitOperations.Log2((uint)length);
            levels[l] = new float[count * (length + Guards)];
        }

        var re = new double[FrameSize];
        var im = new double[FrameSize];
        var spectra = new (double[] Re, double[] Im)[count];
        for (var f = 0; f < count; f++)
        {
            var frame = f < frames.Count ? frames[f] : null;
            for (var i = 0; i < FrameSize; i++)
            {
                re[i] = frame is not null && i < frame.Length && float.IsFinite(frame[i]) ? frame[i] : 0;
                im[i] = 0;
            }

            Fft.Transform(re, im, inverse: false);
            re[0] = im[0] = 0; // DC
            spectra[f] = ((double[])re.Clone(), (double[])im.Clone());
        }

        var peak = 0.0;
        for (var l = 0; l < LevelCount; l++)
        {
            var length = 1 << shift[l];
            var top = Math.Min(1024 >> l, length / 2 - 1);
            var scale = (double)length / FrameSize;
            var lr = new double[length];
            var li = new double[length];
            for (var f = 0; f < count; f++)
            {
                Array.Clear(lr);
                Array.Clear(li);
                var (sr, si) = spectra[f];
                for (var k = 1; k <= top; k++)
                {
                    lr[k] = sr[k] * scale;
                    li[k] = si[k] * scale;
                    lr[length - k] = sr[k] * scale;
                    li[length - k] = -si[k] * scale;
                }

                Fft.Transform(lr, li, inverse: true);
                var stride = length + Guards;
                var dst = levels[l].AsSpan(f * stride, stride);
                for (var i = 0; i < length; i++)
                {
                    dst[i + 1] = (float)lr[i];
                    if (l == 0)
                        peak = Math.Max(peak, Math.Abs(lr[i]));
                }

                dst[0] = dst[length];
                dst[length + 1] = dst[1];
                dst[length + 2] = dst[2];
            }
        }

        if (peak > 1e-9)
        {
            var gain = (float)(1 / peak);
            foreach (var level in levels)
            {
                for (var i = 0; i < level.Length; i++)
                    level[i] *= gain;
            }
        }

        return new Wavetable(source, count, levels, shift);
    }

    /// <summary>The highest harmonic frequency allowed at <paramref name="sampleRate"/> (aliases above 20 kHz are inaudible).</summary>
    public static float MaxHarmonic(float sampleRate) => MathF.Max(sampleRate - 20000, sampleRate * 0.5f);

    /// <summary>The level to read for a cycle at <paramref name="frequency"/> Hz (its highest effective frequency).</summary>
    public static int LevelFor(float frequency, float sampleRate)
    {
        if (!(frequency > 0))
            return 0;
        var ratio = 1024f * frequency / MaxHarmonic(sampleRate);
        return ratio <= 1 ? 0 : Math.Clamp((int)MathF.Ceiling(MathF.Log2(ratio)), 0, LevelCount - 1);
    }

    /// <summary>Render thread: frame <paramref name="frame"/> of <paramref name="level"/> at a 32-bit phase (Hermite).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Read(int level, int frame, uint phase)
    {
        var shift = _shift[level];
        var data = _levels[level];
        var index = (int)(phase >> (32 - shift));
        var t = ((phase << shift) >> 8) * (1f / (1 << 24));
        var at = frame * _stride[level] + index; // data[at + 1] is sample `index`
        var xm1 = data[at];
        var x0 = data[at + 1];
        var x1 = data[at + 2];
        var x2 = data[at + 3];
        var c1 = 0.5f * (x1 - xm1);
        var c2 = xm1 - 2.5f * x0 + 2f * x1 - 0.5f * x2;
        var c3 = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
        return ((c3 * t + c2) * t + c1) * t + x0;
    }

    /// <summary>
    /// Render thread: the table at a fractional frame <paramref name="position"/> (0…1 across the frames): smooth
    /// crossfades the two nearest frames, step plays the nearest.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Read(int level, float position, bool step, uint phase)
    {
        if (Frames == 1)
            return Read(level, 0, phase);
        var p = Math.Clamp(position, 0, 1) * (Frames - 1);
        if (step)
            return Read(level, (int)(p + 0.5f), phase);
        var f0 = (int)p;
        var t = p - f0;
        if (f0 >= Frames - 1)
            return Read(level, Frames - 1, phase);
        var a = Read(level, f0, phase);
        return t < 1e-4f ? a : a + (Read(level, f0 + 1, phase) - a) * t;
    }

    /// <summary>The level-0 samples of a frame (displays, tests).</summary>
    public ReadOnlySpan<float> Frame(int level, int frame) => _levels[level].AsSpan(frame * _stride[level] + 1, 1 << _shift[level]);
}
