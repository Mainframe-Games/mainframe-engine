namespace MainframeEngine.Editor.Music;

/// <summary>Indices of one wavetable oscillator's parameters in <see cref="SynthParams.All"/>.</summary>
public readonly record struct OscParams(int On, int Level, int Pan, int Octave, int Semi, int Fine, int Coarse, int Position, int FrameMode,
    int WarpMode, int Warp, int Unison, int Detune, int Range, int Blend, int Width, int Phase, int Random, int Retrigger, int ToF1, int ToF2, int Hq);

public readonly record struct SubParams(int On, int Shape, int Octave, int Level, int Pan, int ToF1, int ToF2);

public readonly record struct NoiseParams(int On, int Type, int Color, int Rate, int Level, int Pan, int ToF1, int ToF2);

public readonly record struct FilterParams(int On, int Type, int Cutoff, int Reso, int Drive, int KeyTrack, int Mix);

public readonly record struct EnvParams(int Delay, int Attack, int Hold, int Decay, int Sustain, int Release, int AttackCurve, int DecayCurve,
    int ReleaseCurve);

public readonly record struct LfoParams(int Shape, int Rate, int Sync, int Division, int Feel, int Mode, int Global, int Delay, int Rise, int Phase,
    int Smooth);

public enum SynthWarp
{
    Off,
    Sync,
    BendPlus,
    BendMinus,
    Pwm,
    Mirror,
    Fm,
    Am,
    Rm,
}

public enum SynthFilterType
{
    Lp12,
    Lp24,
    Hp12,
    Hp24,
    Bp12,
    Bp24,
    Notch,
    Peak,
    Ladder,
    CombPlus,
    CombMinus,
    Formant,
}

public enum SynthLfoWave
{
    Sine,
    Triangle,
    SawUp,
    SawDown,
    Square,
    SampleHold,
    SmoothRandom,
    Drawn,
}

/// <summary>
/// The wavetable synth's parameters: every id, range, default and curve as <see cref="BuiltinParam"/>s (the file contract:
/// ids are never renamed, choices only appended), with typed index sets for the DSP, the modulation sources, and a
/// <see cref="BuiltinEffectInfo"/> view the plugin window shows them through.
/// </summary>
public static class SynthParams
{
    public const string ProductName = "Mainframe Wavetable";

    public static readonly string[] WarpModes = ["Off", "Sync", "Bend +", "Bend −", "PWM", "Mirror", "FM (from other)", "AM (from other)", "RM (from other)"];
    public static readonly string[] FilterTypes = ["LP 12", "LP 24", "HP 12", "HP 24", "BP 12", "BP 24", "Notch", "Peak", "Ladder LP 24", "Comb +", "Comb −", "Formant"];
    public static readonly string[] LfoShapes = ["Sine", "Triangle", "Saw Up", "Saw Down", "Square", "Sample & Hold", "Smooth Random", "Drawn"];

    /// <summary>The modulation sources, in source-index order (a route's <see cref="SynthModRoute.Source"/>).</summary>
    public static readonly string[] Sources =
    [
        "env1", "env2", "env3", "env4", "lfo1", "lfo2", "lfo3", "lfo4", "velocity", "note", "random",
        "macro1", "macro2", "macro3", "macro4", "modwheel", "aftertouch", "pitchbend",
    ];

    public const int SourceEnv1 = 0, SourceLfo1 = 4, SourceVelocity = 8, SourceNote = 9, SourceRandom = 10, SourceMacro1 = 11,
        SourceModWheel = 15, SourceAftertouch = 16, SourcePitchBend = 17;

    public static readonly BuiltinParam[] All;
    public static readonly OscParams A, B;
    public static readonly SubParams Sub;
    public static readonly NoiseParams Noise;
    public static readonly FilterParams F1, F2;
    public static readonly int FilterRouting;
    public static readonly EnvParams[] Env;
    public static readonly int AmpLevel;
    public static readonly LfoParams[] Lfo;
    public static readonly int Polyphony, VoiceMode, Glide, GlideMode, BendRange;
    public static readonly int[] Macro;
    public static readonly int MasterVolume, MasterPan;

    /// <summary>The parameters as an effect description, for the plugin window (the synth window's stopgap).</summary>
    public static readonly BuiltinEffectInfo Info;

    private static readonly Dictionary<string, int> Index;

    static SynthParams()
    {
        var list = new List<BuiltinParam>();
        int Add(BuiltinParam p)
        {
            list.Add(p);
            return list.Count - 1;
        }

        OscParams Osc(string x, string group, bool on)
        {
            return new OscParams(
                Add(BuiltinParam.Toggle(x + ".on", "On", on, group)),
                Add(new(x + ".level", "Level", "pct", 0, 1, 0.75f, Group: group)),
                Add(new(x + ".pan", "Pan", "pan1", -1, 1, 0, Group: group)),
                Add(new(x + ".octave", "Octave", "", -4, 4, 0, ParamCurve.Stepped, Group: group)),
                Add(new(x + ".semi", "Semi", "st", -12, 12, 0, ParamCurve.Stepped, Group: group)),
                Add(new(x + ".fine", "Fine", "ct", -100, 100, 0, Group: group)),
                Add(new(x + ".coarse", "Coarse", "st", -48, 48, 0, Group: group)),
                Add(new(x + ".wt_pos", "WT Pos", "pct", 0, 1, 0, Group: group)),
                Add(BuiltinParam.Choice(x + ".frame_mode", "Frames", ["Smooth", "Step"], 0, group)),
                Add(BuiltinParam.Choice(x + ".warp_mode", "Warp", WarpModes, 0, group)),
                Add(new(x + ".warp", "Warp Amt", "pct", 0, 1, 0, Group: group)),
                Add(new(x + ".unison", "Unison", "", 1, 16, 1, ParamCurve.Stepped, Group: group)),
                Add(new(x + ".detune", "Detune", "pct", 0, 1, 0.25f, Group: group)),
                Add(new(x + ".range", "Range", "st", 0, 48, 2, Group: group)),
                Add(new(x + ".blend", "Blend", "pct", 0, 1, 0.75f, Group: group)),
                Add(new(x + ".width", "Width", "pct", 0, 1, 1, Group: group)),
                Add(new(x + ".phase", "Phase", "°", 0, 360, 0, Group: group)),
                Add(new(x + ".random", "Rand Phase", "pct", 0, 1, 1, Group: group)),
                Add(BuiltinParam.Toggle(x + ".retrigger", "Retrig", true, group)),
                Add(BuiltinParam.Toggle(x + ".to_f1", "To F1", true, group)),
                Add(BuiltinParam.Toggle(x + ".to_f2", "To F2", false, group)),
                Add(BuiltinParam.Toggle(x + ".hq", "HQ", false, group)));
        }

        A = Osc("a", "Osc A", true);
        B = Osc("b", "Osc B", false);
        Sub = new SubParams(
            Add(BuiltinParam.Toggle("sub.on", "On", false, "Sub")),
            Add(BuiltinParam.Choice("sub.shape", "Shape", ["Sine", "Triangle", "Saw", "Square"], 0, "Sub")),
            Add(new("sub.octave", "Octave", "", -2, 0, -1, ParamCurve.Stepped, Group: "Sub")),
            Add(new("sub.level", "Level", "pct", 0, 1, 0.5f, Group: "Sub")),
            Add(new("sub.pan", "Pan", "pan1", -1, 1, 0, Group: "Sub")),
            Add(BuiltinParam.Toggle("sub.to_f1", "To F1", true, "Sub")),
            Add(BuiltinParam.Toggle("sub.to_f2", "To F2", false, "Sub")));
        Noise = new NoiseParams(
            Add(BuiltinParam.Toggle("noise.on", "On", false, "Noise")),
            Add(BuiltinParam.Choice("noise.type", "Type", ["White", "Pink", "Brown", "Crackle", "Sample & Hold"], 0, "Noise")),
            Add(new("noise.color", "Color", "", -1, 1, 0, Group: "Noise")),
            Add(new("noise.rate", "S&H Rate", "Hz", 20, 20000, 2000, ParamCurve.Log, Group: "Noise")),
            Add(new("noise.level", "Level", "pct", 0, 1, 0.5f, Group: "Noise")),
            Add(new("noise.pan", "Pan", "pan1", -1, 1, 0, Group: "Noise")),
            Add(BuiltinParam.Toggle("noise.to_f1", "To F1", true, "Noise")),
            Add(BuiltinParam.Toggle("noise.to_f2", "To F2", false, "Noise")));

        FilterParams Filter(string f, string group, bool on, int type, float cutoff) => new(
            Add(BuiltinParam.Toggle(f + ".on", "On", on, group)),
            Add(BuiltinParam.Choice(f + ".type", "Type", FilterTypes, type, group)),
            Add(new(f + ".cutoff", "Cutoff", "Hz", 20, 20000, cutoff, ParamCurve.Log, Group: group)),
            Add(new(f + ".reso", "Reso", "pct", 0, 1, 0.1f, Group: group)),
            Add(new(f + ".drive", "Drive", "dB", 0, 24, 0, Group: group)),
            Add(new(f + ".keytrack", "Key Track", "pct", 0, 1, 0, Group: group)),
            Add(new(f + ".mix", "Mix", "pct", 0, 1, 1, Group: group)));

        F1 = Filter("f1", "Filter 1", true, (int)SynthFilterType.Lp24, 20000);
        F2 = Filter("f2", "Filter 2", false, (int)SynthFilterType.Hp12, 40);
        FilterRouting = Add(BuiltinParam.Choice("filter.routing", "Routing", ["Serial", "Parallel"], 0, "Filter 2"));

        Env = new EnvParams[4];
        for (var n = 1; n <= 4; n++)
        {
            var g = "Env " + n;
            var amp = n == 1;
            Env[n - 1] = new EnvParams(
                Add(new($"env{n}.delay", "Delay", "s", 0.0005f, 30, 0.0005f, ParamCurve.Log, Group: g)),
                Add(new($"env{n}.attack", "Attack", "s", 0.0005f, 30, 0.002f, ParamCurve.Log, Group: g)),
                Add(new($"env{n}.hold", "Hold", "s", 0.0005f, 30, 0.0005f, ParamCurve.Log, Group: g)),
                Add(new($"env{n}.decay", "Decay", "s", 0.0005f, 30, amp ? 1f : 0.6f, ParamCurve.Log, Group: g)),
                Add(new($"env{n}.sustain", "Sustain", "pct", 0, 1, amp ? 1f : 0f, Group: g)),
                Add(new($"env{n}.release", "Release", "s", 0.0005f, 30, amp ? 0.05f : 0.3f, ParamCurve.Log, Group: g)),
                Add(new($"env{n}.attack_curve", "A Curve", "", -1, 1, 0.3f, Group: g)),
                Add(new($"env{n}.decay_curve", "D Curve", "", -1, 1, -0.6f, Group: g)),
                Add(new($"env{n}.release_curve", "R Curve", "", -1, 1, -0.6f, Group: g)));
            if (amp)
                AmpLevel = Add(new("env1.level", "Amp", "pct", 0, 1, 1, Group: g));
        }

        Lfo = new LfoParams[4];
        for (var n = 1; n <= 4; n++)
        {
            var g = "LFO " + n;
            Lfo[n - 1] = new LfoParams(
                Add(BuiltinParam.Choice($"lfo{n}.shape", "Shape", LfoShapes, 0, g)),
                Add(new($"lfo{n}.rate", "Rate", "Hz", 0.01f, 100, 2, ParamCurve.Log, Group: g)),
                Add(BuiltinParam.Toggle($"lfo{n}.sync", "Sync", false, g)),
                Add(BuiltinParam.Choice($"lfo{n}.division", "Note", NoteDivision.Names, 2, g)),
                Add(BuiltinParam.Choice($"lfo{n}.feel", "Feel", NoteDivision.Feels, 0, g)),
                Add(BuiltinParam.Choice($"lfo{n}.mode", "Mode", ["Free", "Retrigger", "Envelope"], 1, g)),
                Add(BuiltinParam.Toggle($"lfo{n}.global", "Global", false, g)),
                Add(new($"lfo{n}.delay", "Delay", "s", 0.0005f, 30, 0.0005f, ParamCurve.Log, Group: g)),
                Add(new($"lfo{n}.rise", "Rise", "s", 0.0005f, 30, 0.0005f, ParamCurve.Log, Group: g)),
                Add(new($"lfo{n}.phase", "Phase", "°", 0, 360, 0, Group: g)),
                Add(new($"lfo{n}.smooth", "Smooth", "pct", 0, 1, 0, Group: g)));
        }

        Polyphony = Add(new("voice.polyphony", "Voices", "", 1, WavetableSynth.MaxVoices, 8, ParamCurve.Stepped, Group: "Voicing"));
        VoiceMode = Add(BuiltinParam.Choice("voice.mode", "Mode", ["Poly", "Mono", "Legato"], 0, "Voicing"));
        Glide = Add(new("voice.glide", "Glide", "s", 0.0005f, 5, 0.0005f, ParamCurve.Log, Group: "Voicing"));
        GlideMode = Add(BuiltinParam.Choice("voice.glide_mode", "Glide Mode", ["Always", "Legato"], 0, "Voicing"));
        BendRange = Add(new("voice.bend_range", "Bend Range", "st", 0, 24, 2, ParamCurve.Stepped, Group: "Voicing"));
        Macro = new int[4];
        for (var n = 1; n <= 4; n++)
            Macro[n - 1] = Add(new($"macro{n}", $"Macro {n}", "pct", 0, 1, 0, Group: "Macros"));
        MasterVolume = Add(new("master.volume", "Volume", "dB", -60, 6, -3, Group: "Master"));
        MasterPan = Add(new("master.pan", "Pan", "pan1", -1, 1, 0, Group: "Master"));

        All = [.. list];
        Index = new Dictionary<string, int>(All.Length, StringComparer.Ordinal);
        for (var i = 0; i < All.Length; i++)
            Index[All[i].Id] = i;
        Info = new BuiltinEffectInfo(InstrumentDescriptor.WavetableBuiltin, ProductName, BuiltinCategory.Utility, "wave-sine",
            ProductName + " — two wavetable oscillators, sub, noise, two filters, four envelopes and LFOs, a mod matrix",
            All, _ => throw new NotSupportedException("The wavetable synth is an instrument."));
    }

    /// <summary>The index of parameter <paramref name="id"/>, or −1.</summary>
    public static int IndexOf(string id) => Index.GetValueOrDefault(id, -1);

    public static BuiltinParam? Find(string id) => Index.TryGetValue(id, out var i) ? All[i] : null;

    /// <summary>Whether a parameter can be a mod-matrix target (continuous: not a toggle, choice or whole number).</summary>
    public static bool IsModTarget(BuiltinParam p) => p.Curve is ParamCurve.Linear or ParamCurve.Log;

    public static int SourceIndex(string? source) => source is null ? -1 : Array.IndexOf(Sources, source);

    /// <summary>The value of <paramref name="index"/> in <paramref name="values"/> (clamped), or its default.</summary>
    public static float Value(IReadOnlyDictionary<string, float>? values, int index)
    {
        var p = All[index];
        return values is not null && values.TryGetValue(p.Id, out var v) ? p.Clamp(v) : p.Default;
    }
}
