using System.Runtime.CompilerServices;
using System.Text;

namespace MainframeEngine.Editor.Music;

/// <summary>A compiled mod-matrix route (indices into <see cref="SynthParams.Sources"/> and <see cref="SynthParams.All"/>).</summary>
internal readonly record struct SynthRoute(int Source, int Target, int Aux, float Amount, bool Bipolar);

/// <summary>
/// What the render thread plays besides parameter values: wavetables, compiled routes, drawn LFO tables and the FX rack.
/// Immutable; built on the UI thread by <see cref="WavetableSynth.Apply"/> and swapped in at a block boundary.
/// </summary>
internal sealed class SynthProgram
{
    public required Wavetable TableA { get; init; }
    public required Wavetable TableB { get; init; }
    public required Wavetable[] SubTables { get; init; }
    public required SynthRoute[] Routes { get; init; }
    public required int[] Targets { get; init; }
    public required bool[] Modulated { get; init; }
    public required float[]?[] LfoTables { get; init; }
    public required BuiltinEffect[] Fx { get; init; }
}

/// <summary>
/// The built-in wavetable synth (<c>builtin: "wavetable"</c>): two band-limited wavetable oscillators with warps and
/// 1–16-voice unison, a sub and a noise oscillator, two filters (serial or parallel), four DAHDSR envelopes, four LFOs, a
/// mod matrix evaluated every 16 frames with audio-rate ramps, up to 32 voices (poly, mono, legato, glide) and an FX
/// rack of built-in effects. Design: <c>docs/design/future/wavetable-synth.md</c>.
/// </summary>
/// <remarks>
/// Threads: <see cref="Apply"/> (UI) writes parameter targets lock-free and publishes a new <see cref="SynthProgram"/>
/// when the structure changed; <see cref="Process"/> (render) picks both up at the next block, smooths base values over
/// ~20 ms and allocates nothing. Offline renders are bit-identical: phases are 32-bit integers and every random value
/// comes from a hash of the note's song frame, pitch and lane.
/// </remarks>
public sealed class WavetableSynth : IBuiltinInstrument
{
    public const int MaxVoices = 32;
    public const int TailVoices = 4;
    public const int ControlFrames = 16;

    private const float StealSeconds = 0.003f;
    private const float PanicSeconds = 0.005f;

    private static readonly int ParamCount = SynthParams.All.Length;
    private static readonly string[] SubSources = ["factory:basic/sine", "factory:basic/triangle", "factory:basic/saw", "factory:basic/square"];

    private readonly int _sampleRate;
    private readonly float _dt;
    private readonly float _baseCoef;
    private readonly SynthVoice[] _voices = new SynthVoice[MaxVoices + TailVoices];
    private readonly float[] _targets = new float[ParamCount];
    private readonly float[] _base = new float[ParamCount];
    private readonly float[] _modScratch = new float[ParamCount];
    private readonly float[] _bus = new float[SongEngine.BlockFrames * 2];
    private readonly SynthLfo[] _globalLfo = new SynthLfo[4];
    private readonly float[] _globalSources = new float[SynthParams.Sources.Length];
    private readonly int[] _held = new int[128];
    private int _heldCount;
    private int _version = 1;
    private int _seenVersion;
    private bool _started;
    private SynthProgram? _program;
    private SynthProgram? _active;
    private int _stepPos;
    private long _age;
    private float _lastPitch = float.NaN;
    private float _modWheel;
    private float _aftertouch;
    private float _pitchBend;
    private Ramp _masterL;
    private Ramp _masterR;
    private int _activeVoices;
    private readonly float[] _live = new float[ParamCount];
    private int _loadBits;
    private float _load;

    // UI thread only.
    private readonly Dictionary<PluginInsert, BuiltinEffect> _fx = new(ReferenceEqualityComparer.Instance);
    private string? _signature;
    private SynthProgram? _published;

    public WavetableSynth(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        _sampleRate = sampleRate;
        _dt = ControlFrames / (float)sampleRate;
        _baseCoef = 1 - MathF.Exp(-ControlFrames / (0.02f * sampleRate));
        var down1 = BiquadCoefficients.Design(BiquadShape.LowPass, 0.45 * sampleRate, 0.54119610, 0, 2.0 * sampleRate);
        var down2 = BiquadCoefficients.Design(BiquadShape.LowPass, 0.45 * sampleRate, 1.30656296, 0, 2.0 * sampleRate);
        for (var i = 0; i < _voices.Length; i++)
        {
            var v = _voices[i] = new SynthVoice();
            foreach (var o in (ReadOnlySpan<SynthOscillator>)[v.A, v.B])
            {
                o.Down1.Coefficients = down1;
                o.Down2.Coefficients = down2;
            }
        }
        for (var i = 0; i < ParamCount; i++)
            _targets[i] = _base[i] = SynthParams.All[i].Default;
        Apply(InstrumentDescriptor.Wavetable());
    }

    public int LatencyFrames => 0;

    public string? Status { get; private set; }

    /// <summary>Voices sounding (tests, the window's meter).</summary>
    public int ActiveVoices => Volatile.Read(ref _activeVoices);

    /// <summary>UI thread: the render thread's share of real time (0…1+, smoothed), for the window's CPU meter.</summary>
    public float Load => BitConverter.Int32BitsToSingle(Volatile.Read(ref _loadBits));

    /// <summary>
    /// UI thread: parameter <paramref name="index"/> as the newest sounding voice hears it (its modulated value; the base
    /// value without voices or routes): the window's live modulation dots and the wavetable display's frame.
    /// </summary>
    public float LiveValue(int index) => Volatile.Read(ref _live[index]);

    // ── UI thread ────────────────────────────────────────────────────────────────────────────────────────────────

    public void Apply(InstrumentDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var patch = descriptor.Patch ?? SynthPatch.Init();

        // A comb filter needs its delay lines before the render thread can see the type.
        if (IsComb(SynthParams.Value(patch.Params, SynthParams.F1.Type)) || IsComb(SynthParams.Value(patch.Params, SynthParams.F2.Type)))
        {
            foreach (var v in _voices)
            {
                v.F1.EnsureComb();
                v.F2.EnsureComb();
            }
        }

        var changed = false;
        for (var i = 0; i < ParamCount; i++)
        {
            var value = SynthParams.Value(patch.Params, i);
            if (Volatile.Read(ref _targets[i]) == value)
                continue;
            Volatile.Write(ref _targets[i], value);
            changed = true;
        }

        if (changed)
            Interlocked.Increment(ref _version);

        // The FX rack's instances live as long as their inserts; their parameters are pushed on every build.
        var fx = patch.Fx ?? [];
        var seen = new HashSet<PluginInsert>(ReferenceEqualityComparer.Instance);
        var effects = new List<BuiltinEffect>(fx.Count);
        foreach (var insert in fx)
        {
            if (insert.Builtin is not { } id || BuiltinEffects.Find(id) is not { } info || !seen.Add(insert))
                continue;
            if (!_fx.TryGetValue(insert, out var effect) || effect.Info != info)
                _fx[insert] = effect = info.Create(_sampleRate);
            effect.Apply(insert.Params, insert.Bypass);
            effects.Add(effect);
        }

        foreach (var gone in _fx.Keys.Where(k => !seen.Contains(k)).ToList())
            _fx.Remove(gone);

        // Structure: rebuilt only when tables, routes, drawn shapes or the FX list changed (or a table finished building).
        var tableA = Table(patch, "a", out var errorA);
        var tableB = Table(patch, "b", out var errorB);
        Status = errorA ?? errorB;
        var signature = Signature(patch, tableA, tableB, effects);
        if (signature == _signature && _published is not null)
            return;
        _signature = signature;
        var program = Compile(patch, tableA ?? _published?.TableA ?? WavetableCache.Default, tableB ?? _published?.TableB ?? WavetableCache.Default, effects);
        _published = program;
        Volatile.Write(ref _program, program);
    }

    private static bool IsComb(float type) => (int)type is (int)SynthFilterType.CombPlus or (int)SynthFilterType.CombMinus;

    private static Wavetable? Table(SynthPatch patch, string oscillator, out string? error)
    {
        var reference = patch.Tables is not null && patch.Tables.TryGetValue(oscillator, out var r) ? r : null;
        return WavetableCache.Get(reference?.Source ?? FactoryWavetables.DefaultSource, out error, reference?.FrameSize ?? 0);
    }

    private static string Signature(SynthPatch patch, Wavetable? a, Wavetable? b, List<BuiltinEffect> effects)
    {
        var sb = new StringBuilder();
        // Tables by identity: a re-read file or another frame size is a new table with the same source.
        sb.Append(a is null ? 0 : RuntimeHelpers.GetHashCode(a)).Append('|').Append(b is null ? 0 : RuntimeHelpers.GetHashCode(b)).Append('|');
        foreach (var route in patch.Matrix ?? [])
            sb.Append(route.Source).Append('>').Append(route.Target).Append(':').Append(route.Amount.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .Append(route.Bipolar ? 'b' : 'u').Append(route.Aux).Append(';');
        foreach (var shape in patch.LfoShapes ?? [])
        {
            sb.Append('[');
            foreach (var p in shape?.Points ?? [])
                sb.Append(string.Join(',', p)).Append(' ');
            sb.Append(']');
        }

        foreach (var effect in effects)
            sb.Append(RuntimeHelpers.GetHashCode(effect)).Append(',');
        return sb.ToString();
    }

    private static SynthProgram Compile(SynthPatch patch, Wavetable a, Wavetable b, List<BuiltinEffect> effects)
    {
        var routes = new List<SynthRoute>();
        foreach (var route in (patch.Matrix ?? []).Take(SynthPatch.MaxRoutes))
        {
            var source = SynthParams.SourceIndex(route.Source);
            var target = SynthParams.IndexOf(route.Target);
            if (source < 0 || target < 0 || !SynthParams.IsModTarget(SynthParams.All[target]) || !float.IsFinite(route.Amount))
                continue; // unknown source or target: kept in the file, not played
            routes.Add(new SynthRoute(source, target, SynthParams.SourceIndex(route.Aux), Math.Clamp(route.Amount, -1, 1), route.Bipolar));
        }

        routes.Sort(static (x, y) => x.Target.CompareTo(y.Target));
        var modulated = new bool[ParamCount];
        foreach (var r in routes)
            modulated[r.Target] = true;

        var lfoTables = new float[]?[4];
        for (var n = 0; n < 4; n++)
        {
            if (patch.LfoShapes is { } shapes && n < shapes.Count && shapes[n] is { Points.Count: > 0 } shape)
                lfoTables[n] = BakeShape(shape);
        }

        return new SynthProgram
        {
            TableA = a,
            TableB = b,
            SubTables = [.. SubSources.Select(WavetableCache.GetNow)],
            Routes = [.. routes],
            Targets = [.. Enumerable.Range(0, ParamCount).Where(i => modulated[i])],
            Modulated = modulated,
            LfoTables = lfoTables,
            Fx = [.. effects],
        };
    }

    /// <summary>A drawn LFO shape as a 1024-entry table (points sorted by x; each segment bent by its end point's curve).</summary>
    public static float[] BakeShape(SynthLfoShape shape)
    {
        var points = shape.Points.Where(static p => p is { Length: >= 2 } && float.IsFinite(p[0]) && float.IsFinite(p[1]))
            .Select(static p => (X: Math.Clamp(p[0], 0, 1), Y: Math.Clamp(p[1], 0, 1), C: p.Length > 2 ? Math.Clamp(p[2], -1, 1) : 0))
            .OrderBy(static p => p.X).ToList();
        var table = new float[1024];
        if (points.Count == 0)
            return table;
        for (var i = 0; i < table.Length; i++)
        {
            var x = i / (float)table.Length;
            var next = points.FindIndex(p => p.X >= x);
            if (next < 0)
            {
                table[i] = points[^1].Y;
                continue;
            }

            if (next == 0)
            {
                table[i] = points[0].Y;
                continue;
            }

            var (x0, y0, _) = points[next - 1];
            var (x1, y1, c) = points[next];
            var t = x1 > x0 ? (x - x0) / (x1 - x0) : 1;
            table[i] = y0 + (y1 - y0) * SynthEnvelope.Curve(t, c);
        }

        return table;
    }

    // ── Render thread ────────────────────────────────────────────────────────────────────────────────────────────

    public void Process(TrackSnapshot track, ReadOnlySpan<NoteEvent> events, Span<float> stereo, in EffectContext context)
    {
        var program = Volatile.Read(ref _program);
        if (program is null)
            return;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _active = program;
        var version = Volatile.Read(ref _version);
        if (version != _seenVersion)
        {
            _seenVersion = version;
            if (!_started)
            {
                for (var i = 0; i < ParamCount; i++)
                    _base[i] = Volatile.Read(ref _targets[i]);
                _started = true;
            }
        }

        var frames = stereo.Length / 2;
        if (frames == 0)
            return;
        var bus = _bus.AsSpan(0, frames * 2);
        bus.Clear();
        var cursor = 0;
        foreach (var e in events)
        {
            var at = Math.Clamp(e.Offset, cursor, frames);
            Render(bus, cursor, at, context);
            cursor = at;
            switch (e.Kind)
            {
                case NoteEventKind.PitchBend:
                    _pitchBend = Math.Clamp((e.Velocity - 8192) / 8192f, -1, 1);
                    break;
                case NoteEventKind.ModWheel:
                    _modWheel = Math.Clamp(e.Velocity / 127f, 0, 1);
                    break;
                case NoteEventKind.Aftertouch:
                    _aftertouch = Math.Clamp(e.Velocity / 127f, 0, 1);
                    break;
                default:
                    if (e.On)
                        NoteOn(e.Pitch, e.Velocity, context.PositionFrames + at, context);
                    else
                        NoteOff(e.Pitch);
                    break;
            }
        }

        Render(bus, cursor, frames, context);

        foreach (var effect in program.Fx)
            effect.Process(bus, context);

        // Master volume and pan (a route from a voice source uses the newest voice's value).
        var newest = Newest();
        var volume = Value(newest, SynthParams.MasterVolume);
        AudioMath.PanGains(Value(newest, SynthParams.MasterPan), out var pl, out var pr);
        var gain = volume <= -59.9f ? 0 : Dsp.DbToGain(volume);
        _masterL.To(gain * pl, frames);
        _masterR.To(gain * pr, frames);
        for (var f = 0; f < frames; f++)
        {
            stereo[2 * f] += bus[2 * f] * _masterL.Next();
            stereo[2 * f + 1] += bus[2 * f + 1] * _masterR.Next();
        }

        var count = 0;
        foreach (var v in _voices)
        {
            if (v.Active)
                count++;
        }

        Volatile.Write(ref _activeVoices, count);

        // What the window shows: the newest voice's modulated values, and the load (an exponential average over ~0.2 s).
        foreach (var t in program.Targets)
            Volatile.Write(ref _live[t], Value(newest, t));
        var seconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
        _load += ((float)(seconds * _sampleRate / frames) - _load) * 0.03f;
        Volatile.Write(ref _loadBits, BitConverter.SingleToInt32Bits(_load));
    }

    public void AllNotesOff()
    {
        // A fast fade on every voice (stop, seek), as the ZzFX instrument does.
        var frames = Math.Max(1, (int)(PanicSeconds * _sampleRate));
        foreach (var v in _voices)
        {
            if (!v.Active)
                continue;
            v.Gate = false;
            v.Tail = true;
            if (v.FadeLeft == 0 || v.FadeLeft > frames)
            {
                v.FadeLeft = frames;
                v.FadeTotal = frames;
            }
        }

        _heldCount = 0;
    }

    private SynthVoice? Newest()
    {
        SynthVoice? newest = null;
        foreach (var v in _voices)
        {
            if (v.Active && !v.Tail && (newest is null || v.Age > newest.Age))
                newest = v;
        }

        return newest;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Value(SynthVoice? voice, int index) => voice is not null && _active!.Modulated[index] ? voice.Mod[index] : _base[index];

    private void Render(Span<float> bus, int from, int to, in EffectContext context)
    {
        while (from < to)
        {
            if (_stepPos == 0)
                ControlStep(context, from);
            var n = Math.Min(to - from, ControlFrames - _stepPos);
            foreach (var v in _voices)
            {
                if (v.Active)
                    RenderVoice(v, bus, from, n);
            }

            from += n;
            _stepPos = (_stepPos + n) % ControlFrames;
        }
    }

    // ── Control rate ─────────────────────────────────────────────────────────────────────────────────────────────

    private void ControlStep(in EffectContext context, int offset)
    {
        // Base values glide to their targets (~20 ms); choices, toggles and whole numbers switch at once.
        var all = SynthParams.All;
        for (var i = 0; i < ParamCount; i++)
        {
            if (!_active!.Modulated[i])
                Volatile.Write(ref _live[i], _base[i]);
            var target = Volatile.Read(ref _targets[i]);
            if (all[i].Curve is ParamCurve.Linear or ParamCurve.Log)
            {
                var b = _base[i];
                var next = b + (target - b) * _baseCoef;
                _base[i] = MathF.Abs(next - target) <= 1e-6f * MathF.Max(1, MathF.Abs(target)) ? target : next;
            }
            else
            {
                _base[i] = target;
            }
        }

        // Global sources: macros, controllers and global LFOs.
        for (var n = 0; n < 4; n++)
            _globalSources[SynthParams.SourceMacro1 + n] = _base[SynthParams.Macro[n]];
        _globalSources[SynthParams.SourceModWheel] = _modWheel;
        _globalSources[SynthParams.SourceAftertouch] = _aftertouch;
        _globalSources[SynthParams.SourcePitchBend] = _pitchBend;
        for (var n = 0; n < 4; n++)
        {
            var p = SynthParams.Lfo[n];
            if (_base[p.Global] < 0.5f)
                continue;
            var hz = LfoRate(null, n, context, out var cycleBeats);
            if (cycleBeats > 0 && context.Playing)
                _globalLfo[n].Lock((context.PositionFrames + offset) / context.FramesPerBeat / cycleBeats + _base[p.Phase] / 360);
            _globalSources[SynthParams.SourceLfo1 + n] = _globalLfo[n].Advance(_dt, hz, (int)_base[p.Shape], 0, 0, 0, _base[p.Smooth],
                _active!.LfoTables[n]);
        }

        var bend = _pitchBend * _base[SynthParams.BendRange];
        foreach (var v in _voices)
        {
            if (v.Active)
                VoiceStep(v, context, bend);
        }
    }

    private float LfoRate(SynthVoice? voice, int n, in EffectContext context, out double cycleBeats)
    {
        var p = SynthParams.Lfo[n];
        cycleBeats = 0;
        if (_base[p.Sync] >= 0.5f)
        {
            cycleBeats = NoteDivision.ToBeats((int)_base[p.Division], (int)_base[p.Feel]);
            return (float)(context.Tempo / 60 / cycleBeats);
        }

        return Value(voice, p.Rate);
    }

    private void VoiceStep(SynthVoice v, in EffectContext context, float bend)
    {
        var program = _active!;

        // Envelopes and per-voice LFOs advance to the step's end.
        for (var n = 0; n < 4; n++)
        {
            var e = SynthParams.Env[n];
            var shape = new EnvelopeShape(Value(v, e.Delay), Value(v, e.Attack), Value(v, e.Hold), Value(v, e.Decay), Value(v, e.Sustain),
                Value(v, e.Release), Value(v, e.AttackCurve), Value(v, e.DecayCurve), Value(v, e.ReleaseCurve));
            v.Sources[SynthParams.SourceEnv1 + n] = v.Env[n].Advance(_dt, shape);
        }

        for (var n = 0; n < 4; n++)
        {
            var p = SynthParams.Lfo[n];
            if (_base[p.Global] >= 0.5f)
            {
                v.Sources[SynthParams.SourceLfo1 + n] = _globalSources[SynthParams.SourceLfo1 + n];
                continue;
            }

            var hz = LfoRate(v, n, context, out _);
            v.Sources[SynthParams.SourceLfo1 + n] = v.Lfo[n].Advance(_dt, hz, (int)_base[p.Shape], (int)_base[p.Mode], Value(v, p.Delay),
                Value(v, p.Rise), Value(v, p.Smooth), program.LfoTables[n]);
        }

        v.Sources[SynthParams.SourceVelocity] = v.Velocity;
        v.Sources[SynthParams.SourceNote] = v.Note / 127f;
        v.Sources[SynthParams.SourceRandom] = v.RandomValue;
        for (var s = SynthParams.SourceMacro1; s < v.Sources.Length; s++)
            v.Sources[s] = _globalSources[s];

        // The mod matrix, in normalized (knob) space: a cutoff route moves in octaves, a level route in gain.
        if (program.Routes.Length > 0)
        {
            foreach (var t in program.Targets)
                _modScratch[t] = 0;
            foreach (var r in program.Routes)
            {
                var s = v.Sources[r.Source];
                if (r.Bipolar)
                    s = 2 * s - 1;
                if (r.Aux >= 0)
                    s *= v.Sources[r.Aux];
                _modScratch[r.Target] += r.Amount * s;
            }

            var all = SynthParams.All;
            foreach (var t in program.Targets)
            {
                var p = all[t];
                v.Mod[t] = p.FromNormalized(p.ToNormalized(_base[t]) + _modScratch[t]);
            }
        }

        // Glide (constant time) toward the note.
        if (v.Pitch != v.PitchTarget)
        {
            var step = v.GlideRate * _dt;
            v.Pitch = MathF.Abs(v.PitchTarget - v.Pitch) <= step ? v.PitchTarget : v.Pitch + MathF.Sign(v.PitchTarget - v.Pitch) * step;
        }

        Oscillator(v, v.A, SynthParams.A, program.TableA, bend, ControlFrames);
        Oscillator(v, v.B, SynthParams.B, program.TableB, bend, ControlFrames);
        Sources(v, bend);
        Filters(v);

        var amp = v.Env[0].Value * Value(v, SynthParams.AmpLevel);
        v.Amp.To(amp, ControlFrames);
        if (!v.Env[0].Active && !v.Tail)
        {
            v.Tail = true; // the release ended: one more step fades to the (zero) envelope, then the voice is free
            v.FadeLeft = ControlFrames;
            v.FadeTotal = ControlFrames;
        }

        v.F1.Flush();
        v.F2.Flush();
        v.Noise.Flush();
    }

    private void Oscillator(SynthVoice v, SynthOscillator o, in OscParams p, Wavetable table, float bend, int frames)
    {
        o.On = _base[p.On] >= 0.5f;
        if (!o.On)
            return;
        var pitch = v.Pitch + 12 * _base[p.Octave] + _base[p.Semi] + Value(v, p.Fine) / 100 + Value(v, p.Coarse) + bend;
        var frequency = 440f * MathF.Pow(2, (pitch - 69) / 12);
        var lanes = o.Lanes;
        var spread = Value(v, p.Detune) * Value(v, p.Range); // semitones at the outer lanes
        var width = Value(v, p.Width);
        var blend = Value(v, p.Blend);
        var level = Value(v, p.Level);
        AudioMath.PanGains(Value(v, p.Pan), out var oscL, out var oscR);
        o.WarpMode = (SynthWarp)(int)_base[p.WarpMode];
        o.Warp = Value(v, p.Warp);
        o.Step = _base[p.FrameMode] >= 0.5f;
        o.Hq = _base[p.Hq] >= 0.5f;
        o.Position.To(Value(v, p.Position), frames);
        var sync = o.WarpMode == SynthWarp.Sync ? MathF.Pow(2, 4 * o.Warp) : 1;
        o.SyncFixed = (ulong)(sync * 65536);
        o.BendGamma = o.WarpMode switch
        {
            SynthWarp.BendPlus => MathF.Pow(2, -3 * o.Warp),
            SynthWarp.BendMinus => MathF.Pow(2, 3 * o.Warp),
            _ => 1,
        };

        var rate = o.Hq ? 2f * _sampleRate : _sampleRate;
        var top = frequency * MathF.Pow(2, spread / 12) * sync;
        var mip = Wavetable.LevelFor(top, rate);
        o.PreviousLevel = o.Level == mip || o.PreviousLevel < 0 ? -1 : o.Level;
        o.Level = mip;

        // Unison: detune across the lanes, the centre lane(s) at 1 − blend and the others at blend; normalized over both
        // channels to the power of one centred lane, so the stack is as loud for any count, blend or width.
        var power = 0f;
        for (var i = 0; i < lanes; i++)
        {
            var g = Centre(i, lanes) ? 1 - blend : blend;
            AudioMath.PanGains(Lane(i, lanes) * width, out var l, out var r);
            power += g * g * (l * l + r * r);
        }

        var norm = power > 1e-9f ? level * MathF.Sqrt(2 / power) : 0;
        for (var i = 0; i < lanes; i++)
        {
            var u = Lane(i, lanes);
            var cents = u * spread * 100;
            var inc = frequency * MathF.Pow(2, cents / 1200) / rate * 4294967296.0;
            o.IncStep[i] = (inc - o.Inc[i]) / frames;
            AudioMath.PanGains(u * width, out var l, out var r);
            var g = (Centre(i, lanes) ? 1 - blend : blend) * norm;
            o.GainLStep[i] = (g * l * oscL - o.GainL[i]) / frames;
            o.GainRStep[i] = (g * r * oscR - o.GainR[i]) / frames;
        }
    }

    // A lane's place in the stack: −1 (first) … 1 (last), 0 for a single lane.
    private static float Lane(int i, int lanes) => lanes == 1 ? 0 : -1 + 2f * i / (lanes - 1);

    private static bool Centre(int i, int lanes) => lanes == 1 || (lanes % 2 == 1 ? i == lanes / 2 : i == lanes / 2 || i == lanes / 2 - 1);

    private void Sources(SynthVoice v, float bend)
    {
        var sub = SynthParams.Sub;
        v.SubOn = _base[sub.On] >= 0.5f;
        if (v.SubOn)
        {
            var a = SynthParams.A;
            var pitch = v.Pitch + 12 * (_base[a.Octave] + _base[sub.Octave]) + _base[a.Semi] + bend;
            var frequency = 440f * MathF.Pow(2, (pitch - 69) / 12);
            var inc = frequency / _sampleRate * 4294967296.0;
            v.SubIncStep = (inc - v.SubInc) / ControlFrames;
            v.SubShape = (int)_base[sub.Shape];
            v.SubLevel = Wavetable.LevelFor(frequency, _sampleRate);
            AudioMath.PanGains(Value(v, sub.Pan), out var l, out var r);
            var level = Value(v, sub.Level);
            v.SubL.To(level * l, ControlFrames);
            v.SubR.To(level * r, ControlFrames);
        }

        var noise = SynthParams.Noise;
        v.NoiseOn = _base[noise.On] >= 0.5f;
        if (v.NoiseOn)
        {
            v.NoiseType = (int)_base[noise.Type];
            v.NoiseColor = Value(v, noise.Color);
            v.NoiseHold = Value(v, noise.Rate) * MathF.Pow(2, (v.Note - 60) / 12f) / _sampleRate;
            AudioMath.PanGains(Value(v, noise.Pan), out var l, out var r);
            var level = Value(v, noise.Level);
            v.NoiseL.To(level * l, ControlFrames);
            v.NoiseR.To(level * r, ControlFrames);
        }

        int Route(int toF1, int toF2) => (_base[toF1] >= 0.5f ? 1 : 0) | (_base[toF2] >= 0.5f ? 2 : 0);
        v.RouteA = Route(SynthParams.A.ToF1, SynthParams.A.ToF2);
        v.RouteB = Route(SynthParams.B.ToF1, SynthParams.B.ToF2);
        v.RouteSub = Route(sub.ToF1, sub.ToF2);
        v.RouteNoise = Route(noise.ToF1, noise.ToF2);
    }

    private void Filters(SynthVoice v)
    {
        v.Parallel = _base[SynthParams.FilterRouting] >= 0.5f;
        v.F1On = Filter(v, v.F1, SynthParams.F1, out v.F1Mix);
        v.F2On = Filter(v, v.F2, SynthParams.F2, out v.F2Mix);
    }

    private bool Filter(SynthVoice v, SynthFilter filter, in FilterParams p, out float mix)
    {
        mix = Value(v, p.Mix);
        if (_base[p.On] < 0.5f)
            return false;
        var cutoff = Value(v, p.Cutoff) * MathF.Pow(2, Value(v, p.KeyTrack) * (v.Note - 60) / 12f);
        filter.Set((SynthFilterType)(int)_base[p.Type], cutoff, Value(v, p.Reso), Value(v, p.Drive), _sampleRate);
        return true;
    }

    // ── Audio rate ───────────────────────────────────────────────────────────────────────────────────────────────

    private void RenderVoice(SynthVoice v, Span<float> bus, int from, int count)
    {
        var program = _active!;
        var a = v.A;
        var b = v.B;
        // B first when A reads it (FM/AM/RM); when both read each other, B reads A's previous sample.
        var aUsesB = a.On && b.On && a.WarpMode is SynthWarp.Fm or SynthWarp.Am or SynthWarp.Rm;
        for (var f = from; f < from + count; f++)
        {
            float f1L = 0, f1R = 0, f2L = 0, f2R = 0, dL = 0, dR = 0;
            var t = (_stepPos + (f - from) + 1) / (float)ControlFrames;
            if (aUsesB)
            {
                if (b.On)
                    Add(v, v.RouteB, OscSample(b, program.TableB, a, t), ref f1L, ref f1R, ref f2L, ref f2R, ref dL, ref dR);
                if (a.On)
                    Add(v, v.RouteA, OscSample(a, program.TableA, b, t), ref f1L, ref f1R, ref f2L, ref f2R, ref dL, ref dR);
            }
            else
            {
                if (a.On)
                    Add(v, v.RouteA, OscSample(a, program.TableA, b, t), ref f1L, ref f1R, ref f2L, ref f2R, ref dL, ref dR);
                if (b.On)
                    Add(v, v.RouteB, OscSample(b, program.TableB, a, t), ref f1L, ref f1R, ref f2L, ref f2R, ref dL, ref dR);
            }

            if (v.SubOn)
            {
                v.SubInc += v.SubIncStep;
                var phase = v.SubPhase;
                v.SubPhase = phase + (uint)v.SubInc;
                var s = program.SubTables[Math.Clamp(v.SubShape, 0, 3)].Read(v.SubLevel, 0, phase);
                Add(v, v.RouteSub, (s * v.SubL.Next(), s * v.SubR.Next()), ref f1L, ref f1R, ref f2L, ref f2R, ref dL, ref dR);
            }

            if (v.NoiseOn)
            {
                v.Noise.Next(v.NoiseType, v.NoiseColor, v.NoiseHold, out var nl, out var nr);
                Add(v, v.RouteNoise, (nl * v.NoiseL.Next(), nr * v.NoiseR.Next()), ref f1L, ref f1R, ref f2L, ref f2R, ref dL, ref dR);
            }

            // Filters: serial (F1 into F2; sources sent only to F2 join after F1) or parallel (summed).
            float outL, outR;
            var x1L = f1L;
            var x1R = f1R;
            if (v.F1On)
            {
                v.F1.Process(ref x1L, ref x1R);
                x1L = f1L + (x1L - f1L) * v.F1Mix;
                x1R = f1R + (x1R - f1R) * v.F1Mix;
            }

            if (v.Parallel)
            {
                var x2L = f2L;
                var x2R = f2R;
                if (v.F2On)
                {
                    v.F2.Process(ref x2L, ref x2R);
                    x2L = f2L + (x2L - f2L) * v.F2Mix;
                    x2R = f2R + (x2R - f2R) * v.F2Mix;
                }

                outL = x1L + x2L + dL;
                outR = x1R + x2R + dR;
            }
            else
            {
                var inL = x1L + f2L;
                var inR = x1R + f2R;
                var x2L = inL;
                var x2R = inR;
                if (v.F2On)
                {
                    v.F2.Process(ref x2L, ref x2R);
                    x2L = inL + (x2L - inL) * v.F2Mix;
                    x2R = inR + (x2R - inR) * v.F2Mix;
                }

                outL = x2L + dL;
                outR = x2R + dR;
            }

            var amp = v.Amp.Next();
            if (v.Tail && v.FadeTotal > 0)
            {
                amp *= v.FadeLeft / (float)v.FadeTotal;
                if (--v.FadeLeft <= 0)
                {
                    v.Kill();
                    return;
                }
            }

            bus[2 * f] += outL * amp;
            bus[2 * f + 1] += outR * amp;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Add(SynthVoice v, int route, (float L, float R) s, ref float f1L, ref float f1R, ref float f2L, ref float f2R, ref float dL, ref float dR)
    {
        if ((route & 1) != 0)
        {
            f1L += s.L;
            f1R += s.R;
            if ((route & 2) != 0 && v.Parallel)
            {
                f2L += s.L;
                f2R += s.R;
            }
        }
        else if ((route & 2) != 0)
        {
            f2L += s.L;
            f2R += s.R;
        }
        else
        {
            dL += s.L;
            dR += s.R;
        }
    }

    // One stereo sample of an oscillator (all its lanes); 2× oversampled through a low-pass when HQ is on.
    private static (float L, float R) OscSample(SynthOscillator o, Wavetable table, SynthOscillator other, float t)
    {
        var position = o.Position.Next();
        if (!o.Hq)
            return Lanes(o, table, other, position, t, 1, ramp: true);
        var (l1, r1) = Lanes(o, table, other, position, t, 0.5, ramp: true);
        var (l2, r2) = Lanes(o, table, other, position, t, 0.5, ramp: false);
        o.Down1.Process(ref l1, ref r1);
        o.Down2.Process(ref l1, ref r1);
        o.Down1.Process(ref l2, ref r2);
        o.Down2.Process(ref l2, ref r2);
        return (l2, r2);
    }

    private static (float L, float R) Lanes(SynthOscillator o, Wavetable table, SynthOscillator other, float position, float t, double incScale, bool ramp)
    {
        float sumL = 0, sumR = 0;
        var otherLanes = Math.Max(1, other.Lanes);
        for (var i = 0; i < o.Lanes; i++)
        {
            if (ramp)
            {
                // The increment and gains ramp once per output sample.
                o.Inc[i] += o.IncStep[i];
                o.GainL[i] += o.GainLStep[i];
                o.GainR[i] += o.GainRStep[i];
            }

            var phase = o.Phase[i];
            o.Phase[i] = phase + (uint)(o.Inc[i] * incScale);
            var y = Warped(o, table, phase, position, other.On ? other.Last[i % otherLanes] : 0);
            if (o.PreviousLevel >= 0)
            {
                // The mip level changed this step: crossfade from the old one.
                var level = o.Level;
                o.Level = o.PreviousLevel;
                var old = Warped(o, table, phase, position, other.On ? other.Last[i % otherLanes] : 0);
                o.Level = level;
                y = old + (y - old) * t;
            }

            o.Last[i] = y;
            sumL += y * o.GainL[i];
            sumR += y * o.GainR[i];
        }

        return (sumL, sumR);
    }

    private static float Warped(SynthOscillator o, Wavetable table, uint phase, float position, float other)
    {
        var a = o.Warp;
        var level = o.Level;
        var step = o.Step;
        switch (o.WarpMode)
        {
            case SynthWarp.Sync:
                {
                    var y = table.Read(level, position, step, (uint)((phase * o.SyncFixed) >> 16));
                    var phi = phase * (1f / 4294967296f);
                    return y * ((1 - a) + a * MathF.Sqrt(MathF.Sin(MathF.PI * phi)));
                }

            case SynthWarp.BendPlus or SynthWarp.BendMinus:
                {
                    var phi = phase * (1.0 / 4294967296.0);
                    return table.Read(level, position, step, (uint)(Math.Pow(phi, o.BendGamma) * 4294967295.0));
                }

            case SynthWarp.Pwm:
                {
                    var w = 1 - 0.98f * a;
                    var phi = phase * (1.0 / 4294967296.0);
                    return phi < w ? table.Read(level, position, step, (uint)(phi / w * 4294967295.0)) : 0;
                }

            case SynthWarp.Mirror:
                {
                    var y = table.Read(level, position, step, phase);
                    var mirrored = phase < 0x80000000u ? phase << 1 : (uint)(0u - (phase << 1));
                    return (1 - a) * y + a * table.Read(level, position, step, mirrored);
                }

            case SynthWarp.Fm:
                return table.Read(level, position, step, phase + (uint)(long)(4 * a * other * 4294967296.0));
            case SynthWarp.Am:
                return table.Read(level, position, step, phase) * ((1 - a) + a * (0.5f + 0.5f * other));
            case SynthWarp.Rm:
                return table.Read(level, position, step, phase) * ((1 - a) + a * other);
            default:
                return table.Read(level, position, step, phase);
        }
    }

    // ── Notes ────────────────────────────────────────────────────────────────────────────────────────────────────

    private void NoteOn(int pitch, int velocity, long songFrame, in EffectContext context)
    {
        if ((uint)pitch > 127)
            return;
        var mode = (int)_base[SynthParams.VoiceMode];
        var overlapping = _heldCount > 0;
        Push(pitch);
        if (mode != 0)
        {
            // Mono / legato: one voice, last-note priority.
            var voice = _voices[0];
            if (voice.Active && voice.Gate && !voice.Tail)
            {
                Glide(voice, pitch, overlapping, voice.Pitch);
                voice.Note = pitch;
                voice.Velocity = velocity / 127f;
                if (mode == 1)
                    Retrigger(voice);
                return;
            }

            for (var i = 1; i < _voices.Length; i++)
            {
                if (_voices[i].Active)
                    Steal(_voices[i]);
            }

            if (voice.Active)
                voice.Kill();
            Begin(voice, pitch, velocity, songFrame, overlapping, context);
            return;
        }

        // Poly: a free voice, else steal (oldest released, then oldest held); the stolen one fades in a tail slot.
        var polyphony = Math.Clamp((int)_base[SynthParams.Polyphony], 1, MaxVoices);
        var sounding = 0;
        SynthVoice? free = null;
        SynthVoice? oldestReleased = null;
        SynthVoice? oldestHeld = null;
        SynthVoice? oldestTail = null;
        foreach (var v in _voices)
        {
            if (!v.Active)
            {
                free ??= v;
                continue;
            }

            if (v.Tail)
            {
                if (oldestTail is null || v.Age < oldestTail.Age)
                    oldestTail = v;
                continue;
            }

            sounding++;
            if (!v.Gate)
            {
                if (oldestReleased is null || v.Age < oldestReleased.Age)
                    oldestReleased = v;
            }
            else if (oldestHeld is null || v.Age < oldestHeld.Age)
            {
                oldestHeld = v;
            }
        }

        if (sounding >= polyphony && (oldestReleased ?? oldestHeld) is { } stolen)
            Steal(stolen);
        if (free is null)
        {
            if (oldestTail is null)
                return;
            oldestTail.Kill();
            free = oldestTail;
        }

        Begin(free, pitch, velocity, songFrame, overlapping, context);
    }

    private void Steal(SynthVoice v)
    {
        var frames = Math.Max(1, (int)(StealSeconds * _sampleRate));
        v.Gate = false;
        v.Tail = true;
        v.FadeLeft = frames;
        v.FadeTotal = frames;
    }

    private void Begin(SynthVoice v, int pitch, int velocity, long songFrame, bool overlapping, in EffectContext context)
    {
        var seed = SynthVoice.Hash((uint)songFrame ^ (uint)(songFrame >> 32), (uint)pitch);
        v.Start(pitch, velocity / 127f, ++_age, seed);
        v.Pitch = v.PitchTarget = pitch;
        Glide(v, pitch, overlapping, _lastPitch);
        StartOscillator(v.A, SynthParams.A, seed, 1);
        StartOscillator(v.B, SynthParams.B, seed, 2);
        v.SubPhase = 0;
        v.SubInc = 0;
        v.F1.Reset();
        v.F2.Reset();
        for (var n = 0; n < 4; n++)
        {
            v.Env[n].Kill();
            v.Env[n].Trigger();
            var p = SynthParams.Lfo[n];
            v.Lfo[n].NoteOn((int)_base[p.Mode], _base[p.Phase] / 360, SynthVoice.Hash(seed, (uint)(100 + n)));
        }

        // Compute this step's values at once and start the ramps there (no glide from a previous note's state).
        VoiceStep(v, context, _pitchBend * _base[SynthParams.BendRange]);
        Snap(v.A);
        Snap(v.B);
        v.SubInc += v.SubIncStep * ControlFrames;
        v.SubIncStep = 0;
        v.SubL.Snap(v.SubL.Value + v.SubL.Step * ControlFrames);
        v.SubR.Snap(v.SubR.Value + v.SubR.Step * ControlFrames);
        v.NoiseL.Snap(v.NoiseL.Value + v.NoiseL.Step * ControlFrames);
        v.NoiseR.Snap(v.NoiseR.Value + v.NoiseR.Step * ControlFrames);
        v.Amp.Snap(0);
        v.Amp.To(v.Env[0].Value * Value(v, SynthParams.AmpLevel), ControlFrames - _stepPos);
    }

    private static void Snap(SynthOscillator o)
    {
        for (var i = 0; i < SynthOscillator.MaxLanes; i++)
        {
            o.Inc[i] += o.IncStep[i] * ControlFrames;
            o.IncStep[i] = 0;
            o.GainL[i] += o.GainLStep[i] * ControlFrames;
            o.GainR[i] += o.GainRStep[i] * ControlFrames;
            o.GainLStep[i] = o.GainRStep[i] = 0;
        }

        o.Position.Snap(o.Position.Value + o.Position.Step * ControlFrames);
        o.PreviousLevel = -1;
    }

    private void StartOscillator(SynthOscillator o, in OscParams p, uint seed, uint salt)
    {
        o.Lanes = Math.Clamp((int)_base[p.Unison], 1, SynthOscillator.MaxLanes);
        var retrigger = _base[p.Retrigger] >= 0.5f;
        var start = _base[p.Phase] / 360;
        var random = _base[p.Random];
        o.Reset();
        for (var i = 0; i < o.Lanes; i++)
        {
            o.GainL[i] = o.GainR[i] = 0;
            if (!retrigger)
                continue;
            var rho = (SynthVoice.Hash(seed, salt * 64 + (uint)i) >> 8) * (1f / (1 << 24));
            var phase = start + random * rho;
            o.Phase[i] = (uint)((phase - MathF.Floor(phase)) * 4294967295.0);
        }
    }

    // Constant-time portamento from `from` (the previous note) to `pitch`: always, or only between overlapping notes.
    private void Glide(SynthVoice v, int pitch, bool overlapping, float from)
    {
        var time = _base[SynthParams.Glide];
        var legatoOnly = _base[SynthParams.GlideMode] >= 0.5f;
        v.PitchTarget = pitch;
        if (time > 0.001f && !float.IsNaN(from) && (!legatoOnly || overlapping))
        {
            v.Pitch = from;
            v.GlideRate = MathF.Abs(pitch - from) / time;
        }
        else
        {
            v.Pitch = pitch;
        }

        _lastPitch = pitch;
    }

    private static void Retrigger(SynthVoice v)
    {
        for (var n = 0; n < 4; n++)
            v.Env[n].Trigger();
    }

    private void NoteOff(int pitch)
    {
        var top = _heldCount > 0 ? _held[_heldCount - 1] : -1;
        Remove(pitch);
        var mode = (int)_base[SynthParams.VoiceMode];
        if (mode != 0)
        {
            var voice = _voices[0];
            if (!voice.Active || voice.Note != pitch && top != pitch)
                return;
            if (_heldCount > 0)
            {
                // Back to the most recent held note.
                var back = _held[_heldCount - 1];
                Glide(voice, back, true, voice.Pitch);
                voice.Note = back;
                if (mode == 1)
                    Retrigger(voice);
                return;
            }

            Release(voice);
            return;
        }

        foreach (var v in _voices)
        {
            if (v.Active && v.Gate && v.Note == pitch && !v.Tail)
                Release(v);
        }
    }

    private static void Release(SynthVoice v)
    {
        v.Gate = false;
        for (var n = 0; n < 4; n++)
            v.Env[n].Release();
    }

    private void Push(int pitch)
    {
        Remove(pitch);
        if (_heldCount < _held.Length)
            _held[_heldCount++] = pitch;
    }

    private void Remove(int pitch)
    {
        for (var i = 0; i < _heldCount; i++)
        {
            if (_held[i] != pitch)
                continue;
            Array.Copy(_held, i + 1, _held, i, _heldCount - i - 1);
            _heldCount--;
            return;
        }
    }
}
