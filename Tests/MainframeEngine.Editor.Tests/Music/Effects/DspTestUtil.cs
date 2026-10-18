using MainframeEngine.Editor.Music;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>Signals and measurements for the built-in effect tests (interleaved stereo).</summary>
internal static class DspTestUtil
{
    public const int Rate = MusicTestUtil.Rate;

    public static BuiltinEffect Create(string id, Dictionary<string, float>? values = null, int sampleRate = Rate)
    {
        var info = BuiltinEffects.Find(id) ?? throw new ArgumentException(id);
        var effect = info.Create(sampleRate);
        effect.Apply(values, false);
        return effect;
    }

    public static float[] Sine(double frequency, float amplitude, double seconds, int sampleRate = Rate)
    {
        var frames = (int)(seconds * sampleRate);
        var result = new float[frames * 2];
        for (var f = 0; f < frames; f++)
        {
            var v = amplitude * (float)Math.Sin(2 * Math.PI * frequency * f / sampleRate);
            result[2 * f] = v;
            result[2 * f + 1] = v;
        }

        return result;
    }

    public static float[] Impulse(double seconds, int at = 0, int sampleRate = Rate)
    {
        var result = new float[(int)(seconds * sampleRate) * 2];
        result[2 * at] = 1;
        result[2 * at + 1] = 1;
        return result;
    }

    /// <summary>Runs <paramref name="effect"/> over <paramref name="signal"/> in engine-sized blocks (in place).</summary>
    public static float[] Run(IEffect effect, float[] signal, double tempo = 120, int sampleRate = Rate, long startFrame = 0)
    {
        var position = startFrame;
        for (var start = 0; start < signal.Length; start += SongEngine.BlockFrames * 2)
        {
            var span = signal.AsSpan(start, Math.Min(SongEngine.BlockFrames * 2, signal.Length - start));
            effect.Process(span, new EffectContext(sampleRate, tempo, true, position));
            position += span.Length / 2;
        }

        return signal;
    }

    /// <summary>RMS in dB of one channel (0 = left) over frames [from, to).</summary>
    public static double RmsDb(ReadOnlySpan<float> stereo, int channel, int fromFrame, int toFrame)
    {
        double sum = 0;
        for (var f = fromFrame; f < toFrame; f++)
            sum += stereo[2 * f + channel] * (double)stereo[2 * f + channel];
        return 10 * Math.Log10(Math.Max(sum / Math.Max(1, toFrame - fromFrame), 1e-30));
    }

    public static double PeakDb(ReadOnlySpan<float> stereo, int fromFrame, int toFrame)
    {
        var peak = 0f;
        for (var i = fromFrame * 2; i < toFrame * 2; i++)
            peak = Math.Max(peak, Math.Abs(stereo[i]));
        return 20 * Math.Log10(Math.Max(peak, 1e-15));
    }

    /// <summary>The effect's gain in dB for a steady sine at <paramref name="frequency"/> (settled part only).</summary>
    public static double GainAt(BuiltinEffect effect, double frequency, float amplitude = 0.25f)
    {
        var input = Sine(frequency, amplitude, 0.6);
        var output = Run(effect, (float[])input.Clone());
        var frames = input.Length / 2;
        return RmsDb(output, 0, frames / 2, frames) - RmsDb(input, 0, frames / 2, frames);
    }
}
