namespace MainframeEngine.Editor.Music;

/// <summary>A mono delay line (power-of-two ring, allocated once) with cubic Hermite fractional reads.</summary>
public sealed class DelayLine
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private int _write;

    /// <param name="maxFrames">The longest delay it can read.</param>
    public DelayLine(int maxFrames)
    {
        var size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(4, maxFrames + 4));
        _buffer = new float[size];
        _mask = size - 1;
    }

    /// <summary>The longest delay in frames.</summary>
    public int MaxDelay => _buffer.Length - 4;

    /// <summary>Appends a sample (denormals flushed: feedback loops decay through them).</summary>
    public void Write(float value)
    {
        Dsp.Flush(ref value);
        _buffer[_write] = value;
        _write = (_write + 1) & _mask;
    }

    /// <summary>The sample written <paramref name="delay"/> frames before the last <see cref="Write"/> (≥ 1; fractional).</summary>
    public float Read(float delay)
    {
        delay = Math.Clamp(delay, 1f, MaxDelay);
        var position = _write - delay;
        var i = (int)MathF.Floor(position);
        var t = position - i;
        var xm1 = _buffer[(i - 1) & _mask];
        var x0 = _buffer[i & _mask];
        var x1 = _buffer[(i + 1) & _mask];
        var x2 = _buffer[(i + 2) & _mask];
        var c1 = 0.5f * (x1 - xm1);
        var c2 = xm1 - 2.5f * x0 + 2f * x1 - 0.5f * x2;
        var c3 = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
        return ((c3 * t + c2) * t + c1) * t + x0;
    }

    /// <summary>The sample exactly <paramref name="delay"/> whole frames back.</summary>
    public float ReadInt(int delay) => _buffer[(_write - Math.Clamp(delay, 1, MaxDelay)) & _mask];

    public void Clear() => Array.Clear(_buffer);
}

/// <summary>LFO waveforms (the order is a file contract: the shape parameters index it).</summary>
public enum LfoShape
{
    Sine,
    Triangle,
    Square,
    SawUp,
    SawDown,
    Random,
}

/// <summary>
/// A low-frequency oscillator, free-running in Hz or locked to the song: while the transport plays, its phase at each
/// block start is derived from the song position, so two renders (or a seek) line up on the beat.
/// </summary>
public struct Lfo
{
    public static readonly string[] ShapeNames = ["Sine", "Triangle", "Square", "Saw Up", "Saw Down", "Random"];

    private double _phase;
    private double _increment;
    private float _held;
    private uint _seed;

    public LfoShape Shape;

    /// <summary>Starts a block: <paramref name="cycleBeats"/> &gt; 0 syncs to the song (one cycle per that many beats), else free at <paramref name="hz"/>.</summary>
    public void Begin(in EffectContext context, double cycleBeats, double hz, double phaseOffset = 0)
    {
        if (cycleBeats > 0)
        {
            var framesPerCycle = cycleBeats * context.FramesPerBeat;
            _increment = 1.0 / framesPerCycle;
            if (context.Playing)
            {
                var phase = context.PositionBeats / cycleBeats + phaseOffset;
                SetPhase(phase - Math.Floor(phase));
            }
        }
        else
        {
            _increment = Math.Max(0, hz) / context.SampleRate;
        }
    }

    private void SetPhase(double phase)
    {
        if (phase < _phase && Shape == LfoShape.Random)
            _held = NextRandom();
        _phase = phase;
    }

    /// <summary>The value (−1..1) at the current phase plus <paramref name="offset"/> cycles (no advance).</summary>
    public readonly float At(double offset = 0)
    {
        var p = _phase + offset;
        p -= Math.Floor(p);
        return Shape switch
        {
            LfoShape.Triangle => (float)(p < 0.5 ? 4 * p - 1 : 3 - 4 * p),
            LfoShape.Square => p < 0.5 ? 1f : -1f,
            LfoShape.SawUp => (float)(2 * p - 1),
            LfoShape.SawDown => (float)(1 - 2 * p),
            LfoShape.Random => _held,
            _ => MathF.Sin((float)(2 * Math.PI * p)),
        };
    }

    /// <summary>Moves one sample on.</summary>
    public void Advance()
    {
        _phase += _increment;
        if (_phase < 1)
            return;
        _phase -= Math.Floor(_phase);
        if (Shape == LfoShape.Random)
            _held = NextRandom();
    }

    /// <summary>The value at the current phase, then advances one sample.</summary>
    public float Next()
    {
        var value = At();
        Advance();
        return value;
    }

    public void Reset() => _phase = 0;

    private float NextRandom()
    {
        _seed = _seed * 1664525u + 1013904223u;
        return (_seed >> 8) / (float)(1 << 23) - 1f;
    }
}

/// <summary>Note values for tempo-synced times (choice lists shared by the delay and the LFO effects).</summary>
public static class NoteDivision
{
    /// <summary>1/1 … 1/64 (the choice order is a file contract).</summary>
    public static readonly string[] Names = ["1/1", "1/2", "1/4", "1/8", "1/16", "1/32", "1/64", "2 bars", "4 bars"];

    private static readonly double[] Beats = [4, 2, 1, 0.5, 0.25, 0.125, 0.0625, 8, 16];

    /// <summary>Straight, dotted (×1.5) or triplet (×⅔).</summary>
    public static readonly string[] Feels = ["Straight", "Dotted", "Triplet"];

    /// <summary>The length in beats of a division (index into <see cref="Names"/>) with a feel (index into <see cref="Feels"/>).</summary>
    public static double ToBeats(int division, int feel)
    {
        var beats = Beats[Math.Clamp(division, 0, Beats.Length - 1)];
        return feel switch
        {
            1 => beats * 1.5,
            2 => beats * 2.0 / 3.0,
            _ => beats,
        };
    }

    /// <summary>"1/8 dotted", "1/4", "1/16 triplet".</summary>
    public static string Label(int division, int feel)
    {
        var name = Names[Math.Clamp(division, 0, Names.Length - 1)];
        return feel switch
        {
            1 => name + " dotted",
            2 => name + " triplet",
            _ => name,
        };
    }
}
