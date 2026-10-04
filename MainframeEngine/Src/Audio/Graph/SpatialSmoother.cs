using SoundFlow.Abstracts;

namespace MainframeEngine.Audio;

/// <summary>
/// The last stage of every voice (a SoundFlow <see cref="SoundModifier"/>): applies gain, stereo pan and the
/// distance low-pass, ramping each towards its target with a ~10 ms one-pole smoother per sample so per-frame
/// parameter updates (attenuation, panning, volume changes) never step audibly ("zipper noise"). Positional
/// voices are folded to mono before panning. Stops fade out over ~3 ms.
/// </summary>
/// <remarks>Runs on the audio thread only; the game thread reaches it through voice commands. No allocation, no locks.</remarks>
internal sealed class SpatialSmoother : SoundModifier
{
    /// <summary>Smoothing time constant for parameter changes.</summary>
    public const float RampSeconds = 0.010f;

    /// <summary>Fade-out time constant on stop.</summary>
    public const float FadeSeconds = 0.003f;

    private readonly int _sampleRate;
    private readonly float _ramp;
    private readonly float _fade;

    private float _gain, _targetGain;
    private float _left, _targetLeft;
    private float _right, _targetRight;
    private float _lowPass = 1f, _targetLowPass = 1f; // one-pole coefficient: 1 = open
    private float _filterLeft, _filterRight;
    private bool _positional;
    private bool _fading;

    public SpatialSmoother(int sampleRate)
    {
        _sampleRate = sampleRate;
        _ramp = 1f - MathF.Exp(-1f / (RampSeconds * sampleRate));
        _fade = 1f - MathF.Exp(-1f / (FadeSeconds * sampleRate));
        Name = "Spatial Smoother";
    }

    /// <summary>True once a fade-out has reached silence.</summary>
    public bool FadedOut { get; private set; }

    /// <summary>False until the first block after <see cref="Reset"/> has been processed (nothing audible yet).</summary>
    public bool HasRendered { get; private set; }

    /// <summary>Current (smoothed) gain, for tests and meters.</summary>
    public float CurrentGain => _gain;

    /// <summary>Jumps straight to <paramref name="parameters"/> (a new sound starts at its level, not from silence).</summary>
    public void Reset(in VoiceParams parameters, bool positional)
    {
        SetTarget(parameters);
        _gain = _targetGain;
        _left = _targetLeft;
        _right = _targetRight;
        _lowPass = _targetLowPass;
        _filterLeft = _filterRight = 0f;
        _positional = positional;
        _fading = false;
        FadedOut = false;
        HasRendered = false;
    }

    public void SetTarget(in VoiceParams parameters)
    {
        if (_fading)
            return;
        _targetGain = Sanitize(parameters.Gain, 0f, 16f);
        _targetLeft = Sanitize(parameters.PanLeft, 0f, 1f);
        _targetRight = Sanitize(parameters.PanRight, 0f, 1f);
        _targetLowPass = AudioMath.LowPassCoefficient(parameters.LowPassHz, _sampleRate);
    }

    public void FadeOut()
    {
        _fading = true;
        _targetGain = 0f;
        FadedOut = _gain <= 1e-4f;
    }

    public override void Process(Span<float> buffer, int channels)
    {
        HasRendered = true;
        if (channels < 2)
        {
            ProcessMono(buffer);
            return;
        }

        var k = _fading ? _fade : _ramp;
        float gain = _gain, left = _left, right = _right, lowPass = _lowPass;
        float fl = _filterLeft, fr = _filterRight;
        float tg = _targetGain, tl = _targetLeft, tr = _targetRight, tlp = _targetLowPass;
        var positional = _positional;

        for (var i = 0; i + 1 < buffer.Length; i += channels)
        {
            gain += (tg - gain) * k;
            left += (tl - left) * _ramp;
            right += (tr - right) * _ramp;
            lowPass += (tlp - lowPass) * _ramp;

            float l = buffer[i], r = buffer[i + 1];
            if (positional)
                l = r = (l + r) * 0.5f;
            if (lowPass < 0.9999f)
            {
                fl += (l - fl) * lowPass;
                fr += (r - fr) * lowPass;
                l = fl;
                r = fr;
            }
            else
            {
                fl = l;
                fr = r;
            }

            buffer[i] = l * gain * left;
            buffer[i + 1] = r * gain * right;
            for (var c = 2; c < channels; c++)
                buffer[i + c] *= gain;
        }

        _gain = gain;
        _left = left;
        _right = right;
        _lowPass = lowPass;
        _filterLeft = fl;
        _filterRight = fr;
        if (_fading && gain <= 1e-4f)
            FadedOut = true;
    }

    private void ProcessMono(Span<float> buffer)
    {
        var k = _fading ? _fade : _ramp;
        var gain = _gain;
        for (var i = 0; i < buffer.Length; i++)
        {
            gain += (_targetGain - gain) * k;
            buffer[i] *= gain;
        }

        _gain = gain;
        if (_fading && gain <= 1e-4f)
            FadedOut = true;
    }

    public override float ProcessSample(float sample, int channel) => sample * _gain;

    private static float Sanitize(float value, float min, float max) => float.IsNaN(value) ? min : Math.Clamp(value, min, max);
}
