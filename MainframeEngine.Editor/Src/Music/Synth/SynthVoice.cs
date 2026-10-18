using System.Runtime.CompilerServices;

namespace MainframeEngine.Editor.Music;

/// <summary>A value that ramps linearly to its target across one control step (audio-rate consumers: no zipper).</summary>
internal struct Ramp
{
    public float Value;
    public float Step;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void To(float target, int frames) => Step = (target - Value) / frames;

    public void Snap(float target)
    {
        Value = target;
        Step = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Next() => Value += Step;
}

/// <summary>One wavetable oscillator of a voice: up to 16 unison lanes (32-bit phases, ramped increments and pans).</summary>
internal sealed class SynthOscillator
{
    public const int MaxLanes = 16;

    public readonly uint[] Phase = new uint[MaxLanes];
    public readonly double[] Inc = new double[MaxLanes];
    public readonly double[] IncStep = new double[MaxLanes];
    public readonly float[] GainL = new float[MaxLanes];
    public readonly float[] GainR = new float[MaxLanes];
    public readonly float[] GainLStep = new float[MaxLanes];
    public readonly float[] GainRStep = new float[MaxLanes];
    public readonly float[] Last = new float[MaxLanes];
    public int Lanes = 1;
    public bool On;
    public Ramp Position;
    public float Warp;
    public SynthWarp WarpMode;
    public bool Step;
    public int Level;
    public int PreviousLevel = -1;
    public ulong SyncFixed = 1 << 16;
    public float BendGamma = 1;
    public bool Hq;
    public StereoBiquad Down1 = new(), Down2 = new();

    public void Reset()
    {
        Array.Clear(Inc);
        Array.Clear(IncStep);
        Array.Clear(Last);
        Down1.Reset();
        Down2.Reset();
        PreviousLevel = -1;
    }
}

/// <summary>A synth voice: two oscillators, sub, noise, two filters, four envelopes and LFOs, and its modulated values.</summary>
internal sealed class SynthVoice
{
    public readonly SynthOscillator A = new();
    public readonly SynthOscillator B = new();
    public readonly SynthFilter F1 = new();
    public readonly SynthFilter F2 = new();
    public readonly SynthEnvelope[] Env = new SynthEnvelope[4];
    public readonly SynthLfo[] Lfo = new SynthLfo[4];
    public readonly float[] Mod = new float[SynthParams.All.Length];
    public readonly float[] Sources = new float[SynthParams.Sources.Length];
    public SynthNoise Noise;

    public bool Active;
    public bool Gate;
    public bool Tail;
    public int FadeLeft;
    public int FadeTotal;
    public int Note;
    public float Velocity;
    public float RandomValue;
    public long Age;
    public float Pitch;
    public float PitchTarget;
    public float GlideRate;

    public uint SubPhase;
    public double SubInc;
    public double SubIncStep;
    public bool SubOn;
    public int SubShape;
    public int SubLevel;
    public Ramp SubL;
    public Ramp SubR;

    public bool NoiseOn;
    public int NoiseType;
    public float NoiseColor;
    public float NoiseHold;
    public Ramp NoiseL;
    public Ramp NoiseR;

    public Ramp Amp;
    public bool F1On;
    public bool F2On;
    public float F1Mix;
    public float F2Mix;

    // Where each source goes: bit 0 = filter 1, bit 1 = filter 2.
    public int RouteA;
    public int RouteB;
    public int RouteSub;
    public int RouteNoise;
    public bool Parallel;

    public void Start(int note, float velocity, long age, uint seed)
    {
        Active = true;
        Gate = true;
        Tail = false;
        FadeLeft = 0;
        Note = note;
        Velocity = velocity;
        Age = age;
        Noise.Seed(seed);
        RandomValue = (Hash(seed, 7) >> 8) * (1f / (1 << 24));
    }

    public void Kill()
    {
        Active = false;
        Gate = false;
        Tail = false;
        for (var i = 0; i < Env.Length; i++)
            Env[i].Kill();
        A.Reset();
        B.Reset();
        F1.Reset();
        F2.Reset();
        Amp.Snap(0);
    }

    /// <summary>A deterministic per-voice hash (note start, pitch, lane): the same render twice is bit-identical.</summary>
    public static uint Hash(uint a, uint b)
    {
        var h = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u + (a << 6) + (a >> 2));
        h ^= h >> 16;
        h *= 0x85EBCA6Bu;
        h ^= h >> 13;
        h *= 0xC2B2AE35u;
        h ^= h >> 16;
        return h == 0 ? 1u : h;
    }
}
