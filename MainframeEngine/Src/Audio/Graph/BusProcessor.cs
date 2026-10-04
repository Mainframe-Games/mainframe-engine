using SoundFlow.Abstracts;

namespace MainframeEngine.Audio;

/// <summary>
/// Final stage of a bus mixer (after its effects): the bus fader — volume, mute and solo folded into one smoothed
/// gain by the game thread — plus a peak meter for the bus VU display. Audio thread only; lock- and allocation-free.
/// </summary>
internal sealed class BusProcessor : SoundModifier
{
    private readonly float _ramp;
    private float _gain;
    private float _targetGain;
    private float _peak;

    public BusProcessor(int sampleRate, float initialGain)
    {
        _ramp = 1f - MathF.Exp(-1f / (SpatialSmoother.RampSeconds * sampleRate));
        _gain = _targetGain = initialGain;
        Name = "Bus Fader";
    }

    public float TargetGain
    {
        get => _targetGain;
        set => _targetGain = float.IsNaN(value) ? 0f : Math.Clamp(value, 0f, 16f);
    }

    /// <summary>Peak absolute sample of the last block (written by the audio thread, read by the game thread).</summary>
    public float Peak => Volatile.Read(ref _peak);

    public override void Process(Span<float> buffer, int channels)
    {
        var gain = _gain;
        var target = _targetGain;
        var peak = 0f;
        if (gain == target)
        {
            if (gain != 1f)
            {
                for (var i = 0; i < buffer.Length; i++)
                    buffer[i] *= gain;
            }

            for (var i = 0; i < buffer.Length; i++)
                peak = Math.Max(peak, Math.Abs(buffer[i]));
        }
        else
        {
            var frames = buffer.Length / Math.Max(channels, 1);
            for (var f = 0; f < frames; f++)
            {
                gain += (target - gain) * _ramp;
                for (var c = 0; c < channels; c++)
                {
                    ref var s = ref buffer[f * channels + c];
                    s *= gain;
                    peak = Math.Max(peak, Math.Abs(s));
                }
            }

            if (Math.Abs(gain - target) < 1e-5f)
                gain = target;
        }

        _gain = gain;
        Volatile.Write(ref _peak, peak);
    }

    public override float ProcessSample(float sample, int channel) => sample * _gain;
}
