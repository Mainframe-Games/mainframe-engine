namespace MainframeEngine.Editor.Music;

/// <summary>
/// Stereo Freeverb (Jezar's public-domain design: 8 parallel damped combs into 4 series all-passes per channel, the
/// right channel spread by 23 samples), ported from the engine's bus reverb (<c>MainframeEngine/Src/Audio/Graph/
/// ReverbProcessor.cs</c>) with settable room size, damping and width. Delay lines are allocated once in the constructor
/// at lengths scaled from the 44.1 kHz tunings; the output is the wet signal only.
/// </summary>
public sealed class Freeverb
{
    private static readonly int[] CombTunings = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllPassTunings = [556, 441, 341, 225];
    private const int StereoSpread = 23;
    private const float FixedGain = 0.015f;
    private const float WetScale = 3f;

    private readonly Comb[] _combsL;
    private readonly Comb[] _combsR;
    private readonly AllPass[] _allPassL;
    private readonly AllPass[] _allPassR;
    private float _wet1 = WetScale, _wet2;

    public Freeverb(int sampleRate)
    {
        var scale = sampleRate / 44100f;
        _combsL = [.. CombTunings.Select(t => new Comb(Scaled(t, scale)))];
        _combsR = [.. CombTunings.Select(t => new Comb(Scaled(t + StereoSpread, scale)))];
        _allPassL = [.. AllPassTunings.Select(t => new AllPass(Scaled(t, scale)))];
        _allPassR = [.. AllPassTunings.Select(t => new AllPass(Scaled(t + StereoSpread, scale)))];
        Set(0.5f, 0.5f, 1f);
    }

    private static int Scaled(int samples, float scale) => Math.Max(1, (int)(samples * scale));

    /// <param name="roomSize">0..1 (tail length).</param>
    /// <param name="damping">0..1 (high-frequency absorption).</param>
    /// <param name="width">0..1 stereo width of the tail.</param>
    public void Set(float roomSize, float damping, float width)
    {
        var feedback = Math.Clamp(roomSize, 0f, 1f) * 0.28f + 0.7f;
        var damp = Math.Clamp(damping, 0f, 1f) * 0.4f;
        foreach (var comb in _combsL)
            comb.Set(feedback, damp);
        foreach (var comb in _combsR)
            comb.Set(feedback, damp);
        width = Math.Clamp(width, 0f, 1f);
        _wet1 = WetScale * (width / 2f + 0.5f);
        _wet2 = WetScale * ((1f - width) / 2f);
    }

    /// <summary>One stereo sample in; the reverb (wet only) out.</summary>
    public void Process(float inL, float inR, out float outL, out float outR)
    {
        var input = (inL + inR) * FixedGain;
        float l = 0f, r = 0f;
        for (var c = 0; c < _combsL.Length; c++)
        {
            l += _combsL[c].Process(input);
            r += _combsR[c].Process(input);
        }

        for (var a = 0; a < _allPassL.Length; a++)
        {
            l = _allPassL[a].Process(l);
            r = _allPassR[a].Process(r);
        }

        outL = l * _wet1 + r * _wet2;
        outR = r * _wet1 + l * _wet2;
    }

    public void Clear()
    {
        foreach (var c in _combsL)
            c.Clear();
        foreach (var c in _combsR)
            c.Clear();
        foreach (var a in _allPassL)
            a.Clear();
        foreach (var a in _allPassR)
            a.Clear();
    }

    private sealed class Comb(int length)
    {
        private readonly float[] _buffer = new float[length];
        private int _index;
        private float _store;
        private float _feedback;
        private float _damping;

        public void Set(float feedback, float damping) => (_feedback, _damping) = (feedback, damping);

        public float Process(float input)
        {
            var output = _buffer[_index];
            _store = output * (1f - _damping) + _store * _damping;
            Dsp.Flush(ref _store);
            var next = input + _store * _feedback;
            Dsp.Flush(ref next);
            _buffer[_index] = next;
            if (++_index == _buffer.Length)
                _index = 0;
            return output;
        }

        public void Clear()
        {
            Array.Clear(_buffer);
            _store = 0;
        }
    }

    private sealed class AllPass(int length)
    {
        private const float Feedback = 0.5f;
        private readonly float[] _buffer = new float[length];
        private int _index;

        public float Process(float input)
        {
            var buffered = _buffer[_index];
            var next = input + buffered * Feedback;
            Dsp.Flush(ref next);
            _buffer[_index] = next;
            if (++_index == _buffer.Length)
                _index = 0;
            return buffered - input;
        }

        public void Clear() => Array.Clear(_buffer);
    }
}

/// <summary>
/// 2× oversampling around a waveshaper (saturation, distortion): linear-interpolated upsampling, the shaper at twice the
/// rate, then a 4th-order Butterworth low-pass at 0.45 × the base rate before keeping every other sample. Cuts the
/// aliasing of hard curves well enough for a mix while staying cheap.
/// </summary>
public sealed class Oversampler2x
{
    private StereoBiquad _up1 = new(), _up2 = new(), _down1 = new(), _down2 = new();
    private float _lastL, _lastR;

    public Oversampler2x(int sampleRate)
    {
        var rate = sampleRate * 2.0;
        var cutoff = sampleRate * 0.45;
        _up1.Coefficients = _down1.Coefficients = BiquadCoefficients.Design(BiquadShape.LowPass, cutoff, 0.54119610, 0, rate);
        _up2.Coefficients = _down2.Coefficients = BiquadCoefficients.Design(BiquadShape.LowPass, cutoff, 1.30656296, 0, rate);
    }

    /// <summary>Runs <paramref name="shaper"/> (a static function with explicit state) at twice the rate over <paramref name="stereo"/>.</summary>
    public void Process<TShaper>(Span<float> stereo, ref TShaper shaper)
        where TShaper : struct, IStereoShaper
    {
        for (var f = 0; f < stereo.Length / 2; f++)
        {
            var l = stereo[2 * f];
            var r = stereo[2 * f + 1];
            // Two samples at the doubled rate: the midpoint, then the sample itself.
            float al = (l + _lastL) * 0.5f, ar = (r + _lastR) * 0.5f, bl = l, br = r;
            (_lastL, _lastR) = (l, r);
            _up1.Process(ref al, ref ar);
            _up2.Process(ref al, ref ar);
            _up1.Process(ref bl, ref br);
            _up2.Process(ref bl, ref br);
            shaper.Shape(ref al, ref ar);
            shaper.Shape(ref bl, ref br);
            _down1.Process(ref al, ref ar);
            _down2.Process(ref al, ref ar);
            _down1.Process(ref bl, ref br);
            _down2.Process(ref bl, ref br);
            stereo[2 * f] = bl;
            stereo[2 * f + 1] = br;
        }

        _up1.FlushState();
        _up2.FlushState();
        _down1.FlushState();
        _down2.FlushState();
    }

    public void Reset()
    {
        _up1.Reset();
        _up2.Reset();
        _down1.Reset();
        _down2.Reset();
        _lastL = _lastR = 0;
    }
}

/// <summary>A per-sample stereo waveshaper run by <see cref="Oversampler2x"/>.</summary>
public interface IStereoShaper
{
    void Shape(ref float left, ref float right);
}
