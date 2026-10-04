using SoundFlow.Abstracts;

namespace MainframeEngine.Audio;

/// <summary>
/// Stereo Freeverb (Jezar's public-domain design: 8 parallel damped comb filters into 4 series all-passes per
/// channel, the right channel's delays spread by 23 samples) for bus reverb. Every delay line is allocated once in the
/// constructor at a fixed length scaled from the 44.1 kHz tunings, so processing never allocates and the tail is never
/// reset. Replaces SoundFlow's <c>AlgorithmicReverbModifier</c>, which reallocates its comb buffers (and drops the
/// tail) whenever its LFO-modulated delay crosses an integer — i.e. continually on the audio thread.
/// </summary>
internal sealed class ReverbProcessor : SoundModifier
{
    private static readonly int[] CombTunings = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllPassTunings = [556, 441, 341, 225];
    private const int StereoSpread = 23;
    private const float FixedGain = 0.015f;
    private const float AllPassFeedback = 0.5f;

    private readonly Comb[] _combsL;
    private readonly Comb[] _combsR;
    private readonly AllPass[] _allPassL;
    private readonly AllPass[] _allPassR;
    private readonly float _wet1;
    private readonly float _wet2;
    private readonly float _dry;

    /// <param name="roomSize">0..1 (tail length).</param>
    /// <param name="damp">0..1 (high-frequency absorption).</param>
    /// <param name="wet">Reverb level.</param>
    /// <param name="dry">Direct level.</param>
    /// <param name="width">0..1 stereo width of the reverb.</param>
    public ReverbProcessor(int sampleRate, float roomSize, float damp, float wet, float dry, float width)
    {
        var scale = sampleRate / 44100f;
        var feedback = Math.Clamp(roomSize, 0f, 1f) * 0.28f + 0.7f;
        var damping = Math.Clamp(damp, 0f, 1f) * 0.4f;
        width = Math.Clamp(width, 0f, 1f);
        wet = Math.Max(0f, wet) * 3f; // Freeverb's wet scale
        _wet1 = wet * (width / 2f + 0.5f);
        _wet2 = wet * ((1f - width) / 2f);
        _dry = Math.Max(0f, dry);

        _combsL = new Comb[CombTunings.Length];
        _combsR = new Comb[CombTunings.Length];
        for (var i = 0; i < CombTunings.Length; i++)
        {
            _combsL[i] = new Comb(Scaled(CombTunings[i], scale), feedback, damping);
            _combsR[i] = new Comb(Scaled(CombTunings[i] + StereoSpread, scale), feedback, damping);
        }

        _allPassL = new AllPass[AllPassTunings.Length];
        _allPassR = new AllPass[AllPassTunings.Length];
        for (var i = 0; i < AllPassTunings.Length; i++)
        {
            _allPassL[i] = new AllPass(Scaled(AllPassTunings[i], scale));
            _allPassR[i] = new AllPass(Scaled(AllPassTunings[i] + StereoSpread, scale));
        }

        Name = "Reverb";
    }

    private static int Scaled(int samples, float scale) => Math.Max(1, (int)(samples * scale));

    public override void Process(Span<float> buffer, int channels)
    {
        if (!Enabled || channels < 1)
            return;
        for (var i = 0; i + channels - 1 < buffer.Length; i += channels)
        {
            var inL = buffer[i];
            var inR = channels > 1 ? buffer[i + 1] : inL;
            var input = (inL + inR) * FixedGain;

            float outL = 0f, outR = 0f;
            for (var c = 0; c < _combsL.Length; c++)
            {
                outL += _combsL[c].Process(input);
                outR += _combsR[c].Process(input);
            }

            for (var a = 0; a < _allPassL.Length; a++)
            {
                outL = _allPassL[a].Process(outL);
                outR = _allPassR[a].Process(outR);
            }

            buffer[i] = outL * _wet1 + outR * _wet2 + inL * _dry;
            if (channels > 1)
                buffer[i + 1] = outR * _wet1 + outL * _wet2 + inR * _dry;
        }
    }

    public override float ProcessSample(float sample, int channel) => sample; // block processing only (Process)

    private sealed class Comb(int length, float feedback, float damping)
    {
        private readonly float[] _buffer = new float[length];
        private int _index;
        private float _store;

        public float Process(float input)
        {
            var output = _buffer[_index];
            _store = output * (1f - damping) + _store * damping;
            if (Math.Abs(_store) < 1e-20f)
                _store = 0f; // flush denormals
            _buffer[_index] = input + _store * feedback;
            if (++_index == _buffer.Length)
                _index = 0;
            return output;
        }
    }

    private sealed class AllPass(int length)
    {
        private readonly float[] _buffer = new float[length];
        private int _index;

        public float Process(float input)
        {
            var buffered = _buffer[_index];
            if (Math.Abs(buffered) < 1e-20f)
                buffered = 0f;
            _buffer[_index] = input + buffered * AllPassFeedback;
            if (++_index == _buffer.Length)
                _index = 0;
            return buffered - input;
        }
    }
}
