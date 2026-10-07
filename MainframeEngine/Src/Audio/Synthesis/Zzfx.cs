// A port of ZzFX's ZZFX.buildSamples (ZzFX 1.4.0, https://github.com/KilledByAPixel/ZzFX, ZzFX.js at commit
// 751f17139d689b1320f0c4173031b038959e4bf8).
//
// ZzFX MIT License
//
// Copyright (c) 2019 - Frank Force
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Globalization;

namespace MainframeEngine;

/// <summary>
/// ZzFX (Frank Force's tiny sound synth, version 1.4.0): turns <see cref="ZzfxParameters"/> into mono samples, exactly
/// as the web designer at <c>killedbyapixel.github.io/ZzFX</c> plays them. <see cref="ZzfxStream"/> is the resource
/// form; this is the synthesiser.
/// </summary>
public static class Zzfx
{
    /// <summary>ZzFX's sample rate (<c>zzfxR</c>).</summary>
    public const int SampleRate = 44100;

    /// <summary>ZzFX's master volume (<c>zzfxV</c>): baked into the samples so pasted sounds match the browser's loudness.</summary>
    public const float MasterVolume = 0.3f;

    /// <summary>Longest sound <see cref="Generate"/> accepts, in seconds.</summary>
    public const float MaxSeconds = 10f;

    private const double PI2 = Math.PI * 2;
    private static int s_nonFiniteWarned;

    /// <summary>
    /// Length in seconds of the sound (attack + decay + sustain + release + delay, with ZzFX's 9-sample minimum
    /// attack), or 0 when it is empty.
    /// </summary>
    public static double Duration(in ZzfxParameters p)
    {
        var attack = Widen(p.Attack) * SampleRate;
        if (attack == 0 || double.IsNaN(attack))
            attack = 9;
        var length = attack + Widen(p.Decay) * SampleRate + Widen(p.Sustain) * SampleRate + Widen(p.Release) * SampleRate +
                     Widen(p.Delay) * SampleRate;
        return double.IsFinite(length) && length > 0 ? length / SampleRate : 0;
    }

    /// <summary>
    /// Mono samples at <see cref="SampleRate"/> for <paramref name="p"/>, scaled by <see cref="MasterVolume"/>.
    /// Randomness is ignored (it is applied per play, like <c>ZZFXSound</c>); non-finite values take ZzFX's defaults.
    /// </summary>
    /// <exception cref="ArgumentException">The sound is longer than <see cref="MaxSeconds"/>.</exception>
    public static float[] Generate(in ZzfxParameters p)
    {
        // init parameters (floats widened to the double their shortest decimal form names, as JavaScript reads them)
        double sampleRate = SampleRate;
        double volume = Value(p, 0),
            frequency = Value(p, 2),
            attack = Value(p, 3),
            sustain = Value(p, 4),
            release = Value(p, 5),
            shapeCurve = Value(p, 7),
            slide = Value(p, 8),
            deltaSlide = Value(p, 9),
            pitchJump = Value(p, 10),
            pitchJumpTime = Value(p, 11),
            repeatTimeSeconds = Value(p, 12),
            noise = Value(p, 13),
            modulation = Value(p, 14),
            bitCrush = Value(p, 15),
            delay = Value(p, 16),
            sustainVolume = Value(p, 17),
            decay = Value(p, 18),
            tremolo = Value(p, 19),
            filter = Value(p, 20);
        var shape = (int)p.Shape;

        var startSlide = slide *= 500 * PI2 / sampleRate / sampleRate;
        var startFrequency = frequency *= PI2 / sampleRate; // (1 + randomness*2*random - randomness) with randomness 0
        double modOffset = 0; // modulation offset
        long repeat = 0; // repeat offset
        long crush = 0; // bit crush offset
        double jump = 1; // pitch jump timer
        double t = 0; // sample time
        double s = 0; // sample value

        // biquad LP/HP filter
        const double quality = 2;
        double w = PI2 * Math.Abs(filter) * 2 / sampleRate,
            cos = Math.Cos(w),
            alpha = Math.Sin(w) / 2 / quality,
            a0 = 1 + alpha,
            a1 = -2 * cos / a0,
            a2 = (1 - alpha) / a0,
            b0 = (1 + Sign(filter) * cos) / 2 / a0,
            b1 = -(Sign(filter) + cos) / a0,
            b2 = b0,
            x2 = 0,
            x1 = 0,
            y2 = 0,
            y1 = 0;

        // scale by sample rate
        const double minAttack = 9; // prevent pop if attack is 0
        attack *= sampleRate;
        if (attack == 0 || double.IsNaN(attack))
            attack = minAttack;
        decay *= sampleRate;
        sustain *= sampleRate;
        release *= sampleRate;
        delay *= sampleRate;
        deltaSlide *= 500 * PI2 / (sampleRate * sampleRate * sampleRate);
        modulation *= PI2 / sampleRate;
        pitchJump *= PI2 / sampleRate;
        pitchJumpTime *= sampleRate;
        var repeatTime = ToInt32(repeatTimeSeconds * sampleRate);

        var total = attack + decay + sustain + release + delay;
        if (total > MaxSeconds * sampleRate)
        {
            throw new ArgumentException(
                $"The ZzFX sound is {(total / sampleRate).ToString("0.##", CultureInfo.InvariantCulture)} s long; the limit is {MaxSeconds} s.",
                nameof(p));
        }

        var length = ToInt32(total);
        var b = new float[length > 0 ? length : 0];
        var crushPeriod = ToInt32(bitCrush * 100);

        // generate waveform
        for (var i = 0; i < length; i++)
        {
            if (crushPeriod == 0 || ++crush % crushPeriod == 0) // bit crush
            {
                s = shape switch // wave shape
                {
                    0 => Math.Sin(t), // 0 sin
                    1 => 1 - 4 * Math.Abs(JsRound(t / PI2) - t / PI2), // 1 triangle
                    2 => 1 - (2 * t / PI2 % 2 + 2) % 2, // 2 saw
                    3 => Math.Max(Math.Min(Math.Tan(t), 1), -1), // 3 tan
                    4 => Math.Sin(Math.Pow(t, 3)), // 4 noise
                    _ => t / PI2 % 1 < shapeCurve / 2 ? 1 : -1, // 5 square duty
                };

                s = (repeatTime != 0 ? 1 - tremolo + tremolo * Math.Sin(PI2 * i / repeatTime) : 1) * // tremolo
                    (shape > 4 ? s : Sign(s) * Math.Pow(Math.Abs(s), shapeCurve)) * // shape curve
                    (i < attack ? i / attack : // attack
                        i < attack + decay ? 1 - ((i - attack) / decay) * (1 - sustainVolume) : // decay falloff
                        i < attack + decay + sustain ? sustainVolume : // sustain volume
                        i < length - delay ? (length - i - delay) / release * sustainVolume : // release falloff
                        0); // post release

                if (delay != 0) // delay
                {
                    s = s / 2 + (delay > i ? 0 :
                        (i < length - delay ? 1 : (length - i) / delay) * // release delay
                        b[ToInt32(i - delay)] / 2 / volume); // sample delay (reads the unscaled float32 sample)
                }

                if (filter != 0) // apply filter
                {
                    var y = b2 * x2 + b1 * x1 + b0 * s - a2 * y2 - a1 * y1;
                    x2 = x1;
                    x1 = s;
                    y2 = y1;
                    y1 = y;
                    s = y;
                }
            }

            slide += deltaSlide;
            frequency += slide;
            var f = frequency * Math.Cos(modulation * modOffset++); // frequency, modulation
            t += f + f * noise * ((double)i * i * PI2 % 2 - 1); // noise

            if (jump != 0 && ++jump > pitchJumpTime) // pitch jump
            {
                frequency += pitchJump; // apply pitch jump
                startFrequency += pitchJump; // also apply to start
                jump = 0; // stop pitch jump time
            }

            if (repeatTime != 0 && ++repeat % repeatTime == 0) // repeat
            {
                frequency = startFrequency; // reset frequency
                slide = startSlide; // reset slide
                if (jump == 0)
                    jump = 1; // reset pitch jump time
            }

            b[i] = (float)(s * volume); // sample
        }

        for (var i = 0; i < b.Length; i++)
            b[i] *= MasterVolume;
        return b;
    }

    private static double Sign(double v) => v < 0 ? -1 : 1;

    // JavaScript's Math.round: nearest, halves toward +∞.
    private static double JsRound(double v)
    {
        var floor = Math.Floor(v);
        return v - floor >= 0.5 ? floor + 1 : floor;
    }

    // JavaScript's ToInt32 (the `| 0` idiom): truncate, wrap modulo 2^32, NaN/∞ → 0.
    private static int ToInt32(double v)
    {
        if (!double.IsFinite(v))
            return 0;
        var truncated = Math.Truncate(v);
        if (truncated is >= int.MinValue and <= int.MaxValue)
            return (int)truncated;
        var wrapped = truncated % 4294967296.0;
        if (wrapped < 0)
            wrapped += 4294967296.0;
        return unchecked((int)(uint)wrapped);
    }

    private static double Value(in ZzfxParameters p, int index)
    {
        var value = Widen(p[index]);
        if (double.IsFinite(value))
            return value;
        if (Interlocked.Exchange(ref s_nonFiniteWarned, 1) == 0)
            Log.Warning($"[Audio] ZzFX parameter {index + 1} is {value}; using ZzFX's default ({ZzfxParameters.DefaultAt(index)}).");
        return Widen(ZzfxParameters.DefaultAt(index));
    }

    // The double a JavaScript number literal for this float's shortest form would be (0.04f → 0.04, not 0.03999…), so
    // sample counts and envelope edges match the browser exactly.
    private static double Widen(float value)
    {
        if (!float.IsFinite(value))
            return value;
        Span<char> text = stackalloc char[32];
        value.TryFormat(text, out var written, default, CultureInfo.InvariantCulture);
        return double.Parse(text[..written], NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
