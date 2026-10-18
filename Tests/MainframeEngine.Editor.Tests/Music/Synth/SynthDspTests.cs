using MainframeEngine.Editor.Music;
using static MainframeEngine.Editor.Tests.Music.SynthTestUtil;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>The wavetable synth's DSP: mip-maps, aliasing, warps, unison, envelopes, LFOs, filters, controllers.</summary>
public sealed class SynthDspTests
{
    private static double[] Magnitudes(ReadOnlySpan<float> stereo, int fromFrame, int size)
    {
        var re = new double[size];
        var im = new double[size];
        for (var i = 0; i < size; i++)
        {
            // Blackman-Harris window: side lobes under −92 dB.
            var w = 0.35875 - 0.48829 * Math.Cos(2 * Math.PI * i / size) + 0.14128 * Math.Cos(4 * Math.PI * i / size) - 0.01168 * Math.Cos(6 * Math.PI * i / size);
            re[i] = stereo[2 * (fromFrame + i)] * w;
        }

        Fft.Transform(re, im, inverse: false);
        var mags = new double[size / 2];
        for (var k = 0; k < size / 2; k++)
            mags[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
        return mags;
    }

    private static double Frequency(ReadOnlySpan<float> stereo, int fromFrame, int toFrame)
    {
        // Rising zero crossings with linear interpolation.
        double first = -1, last = -1;
        var count = 0;
        for (var f = fromFrame + 1; f < toFrame; f++)
        {
            var a = stereo[2 * (f - 1)];
            var b = stereo[2 * f];
            if (a >= 0 || b < 0)
                continue;
            var at = f - 1 + a / (a - b);
            if (first < 0)
                first = at;
            last = at;
            count++;
        }

        return count < 2 ? 0 : (count - 1) * Rate / (last - first);
    }

    [Theory]
    [InlineData("factory:basic/saw")]
    [InlineData("factory:digital/fm")]
    public void EachMipLevelKeepsOnlyItsHarmonics(string source)
    {
        var table = Wavetable.Build(source, FactoryWavetables.Generate(source)!);
        for (var level = 0; level < Wavetable.LevelCount; level++)
        {
            var length = table.LevelLength(level);
            var top = Math.Min(1024 >> level, length / 2 - 1);
            var frame = table.Frame(level, table.Frames - 1);
            var re = frame.ToArray().Select(static v => (double)v).ToArray();
            var im = new double[length];
            Fft.Transform(re, im, inverse: false);
            var total = 0.0;
            var above = 0.0;
            for (var k = 1; k < length / 2; k++)
            {
                var e = re[k] * re[k] + im[k] * im[k];
                total += e;
                if (k > top)
                    above += e;
            }

            Assert.True(above <= total * 1e-12, $"level {level}: {10 * Math.Log10(above / total + 1e-30):0} dB above harmonic {top}");
        }
    }

    [Theory]
    [InlineData(440f, 48000f, 5)]
    [InlineData(27.5f, 48000f, 1)]
    [InlineData(20f, 48000f, 0)]
    [InlineData(4186f, 48000f, 8)]
    [InlineData(440f, 44100f, 5)]
    public void TheLevelKeepsHarmonicsUnderTheAudibleLimit(float frequency, float rate, int expected)
    {
        var level = Wavetable.LevelFor(frequency, rate);
        Assert.Equal(expected, level);
        Assert.True((1024 >> level) * frequency <= Wavetable.MaxHarmonic(rate));
    }

    [Theory]
    [InlineData(21)] // A0
    [InlineData(57)] // A3
    [InlineData(84)] // C6
    [InlineData(108)] // C8
    public void ASawNeverAliasesAudibly(int pitch)
    {
        var synth = Create(new() { ["f1.on"] = 0, ["a.random"] = 0 });
        var output = Note(synth, pitch, 0.5);
        const int size = 16384;
        var mags = Magnitudes(output, Rate / 10, size);
        var f0 = 440 * Math.Pow(2, (pitch - 69) / 12.0);
        var binHz = (double)Rate / size;
        var fundamental = 0.0;
        for (var k = 1; k < size / 2; k++)
        {
            var hz = k * binHz;
            if (Math.Abs(hz - f0) <= 3 * binHz)
                fundamental = Math.Max(fundamental, mags[k]);
        }

        var worst = 0.0;
        var worstHz = 0.0;
        for (var k = 4; k < size / 2; k++)
        {
            var hz = k * binHz;
            if (hz > 20000)
                break;
            var harmonic = Math.Round(hz / f0);
            if (harmonic >= 1 && Math.Abs(hz - harmonic * f0) <= 4 * binHz)
                continue;
            if (mags[k] > worst)
                (worst, worstHz) = (mags[k], hz);
        }

        Assert.True(Db(worst / fundamental) <= -70, $"{Db(worst / fundamental):0.0} dB at {worstHz:0} Hz (f0 {f0:0.0})");
    }

    [Theory]
    [InlineData(SynthWarp.Sync)]
    [InlineData(SynthWarp.BendPlus)]
    [InlineData(SynthWarp.BendMinus)]
    [InlineData(SynthWarp.Pwm)]
    [InlineData(SynthWarp.Mirror)]
    [InlineData(SynthWarp.Fm)]
    [InlineData(SynthWarp.Am)]
    [InlineData(SynthWarp.Rm)]
    public void AWarpAtZeroIsTheIdentity(SynthWarp mode)
    {
        var plain = Note(Create(new() { ["a.random"] = 0, ["b.on"] = 1 }), 60, 0.2);
        var warped = Note(Create(new() { ["a.random"] = 0, ["b.on"] = 1, ["a.warp_mode"] = (float)mode, ["a.warp"] = 0 }), 60, 0.2);
        for (var i = 0; i < plain.Length; i++)
            Assert.Equal(plain[i], warped[i], 4);
    }

    [Fact]
    public void UnisonKeepsItsLoudnessAndWidthZeroIsMono()
    {
        static double Power(float[] stereo)
        {
            double sum = 0;
            for (var i = Rate / 2; i < stereo.Length; i++)
                sum += stereo[i] * (double)stereo[i];
            return Db(Math.Sqrt(sum / (stereo.Length - Rate / 2)));
        }

        var reference = Power(Note(Create(new() { ["f1.on"] = 0 }), 57, 1));
        foreach (var lanes in new[] { 2, 4, 7, 16 })
        {
            var output = Note(Create(new() { ["f1.on"] = 0, ["a.unison"] = lanes, ["a.detune"] = 0.4f }), 57, 1);
            var level = Power(output) - reference;
            Assert.True(Math.Abs(level) <= 1.5, $"{lanes} lanes: {level:0.00} dB");
        }

        var mono = Note(Create(new() { ["a.unison"] = 7, ["a.width"] = 0 }), 57, 0.3);
        for (var f = 0; f < mono.Length / 2; f++)
            Assert.Equal(mono[2 * f], mono[2 * f + 1], 5);
    }

    [Fact]
    public void AttackTakesItsTimeAndAReleaseDuringItStartsFromTheCurrentLevel()
    {
        var env = new SynthEnvelope();
        var shape = new EnvelopeShape(0.0005f, 0.1f, 0.0005f, 1, 1, 0.2f, 0, -0.6f, 0);
        env.Trigger();
        var dt = WavetableSynth.ControlFrames / (float)Rate;
        var steps = 0;
        while (env.Advance(dt, shape) < 0.999f)
            steps++;
        Assert.InRange(steps * dt, 0.1 - dt, 0.1 + dt); // the attack length, to a control step

        env.Kill();
        env.Trigger();
        for (var i = 0; i < 150; i++) // half way up
            env.Advance(dt, shape);
        var level = env.Value;
        env.Release();
        Assert.Equal(level, env.Advance(1e-6f, shape), 2); // no jump
    }

    [Fact]
    public void ASyncedGlobalLfoFollowsTheSongPosition()
    {
        // LFO 1 (global, synced to 1/4 at 120 BPM: 0.5 s, square) on oscillator A's level: two renders that start half a
        // cycle apart are in antiphase on the same song frames.
        Dictionary<string, float> values = new() { ["lfo1.global"] = 1, ["lfo1.sync"] = 1, ["lfo1.division"] = 2, ["lfo1.shape"] = 4, ["f1.on"] = 0 };
        void Route(SynthPatch p) => p.Matrix = [new SynthModRoute { Source = "lfo1", Target = "a.level", Amount = -1 }];
        var a = Render(Create(values, Route), 1, startFrame: 0, notes: [(0, 69, 100)]);
        var b = Render(Create(values, Route), 1, startFrame: Rate / 4, notes: [(0, 69, 100)]);
        var loudA = Rms(a, Rate / 2 + 1000, Rate / 2 + 5000); // song 0.52 s: A's first half (square high → level 0)
        var loudB = Rms(b, Rate / 4 + 1000, Rate / 4 + 5000); // song 0.52 s for B too
        Assert.True(Math.Abs(loudA - loudB) < 0.02, $"{loudA} vs {loudB}");
        Assert.True(Math.Abs(Rms(a, 1000, 5000) - Rms(a, Rate / 4 + 1000, Rate / 4 + 5000)) > 0.05, "the square moves the level");
    }

    [Fact]
    public void TheLowPassIsThreeDbDownAtItsCutoff()
    {
        // A sine table through a 1 kHz LP 12 (Q 0.707): its gain at 1 kHz is −3 dB.
        double Level(bool filtered) => Db(Rms(Note(Create(new() { ["f1.on"] = filtered ? 1 : 0, ["f1.type"] = (float)SynthFilterType.Lp12, ["f1.cutoff"] = 1000, ["f1.reso"] = 0 },
            p => p.Tables!["a"] = new SynthTableRef { Source = "factory:basic/sine" }), 83, 0.5), Rate / 4, Rate / 2)); // B5 ≈ 988 Hz
        Assert.Equal(-3, Level(true) - Level(false), 0.6);
    }

    [Theory]
    [InlineData(SynthFilterType.Ladder)]
    [InlineData(SynthFilterType.CombPlus)]
    [InlineData(SynthFilterType.CombMinus)]
    [InlineData(SynthFilterType.Formant)]
    [InlineData(SynthFilterType.Lp24)]
    public void FiltersStayBoundedAtFullResonanceAndDrive(SynthFilterType type)
    {
        var synth = Create(new() { ["f1.type"] = (float)type, ["f1.reso"] = 1, ["f1.drive"] = 24, ["f1.cutoff"] = 800, ["a.unison"] = 4 });
        var output = Render(synth, 2, notes: [(0, 48, 127), (Rate / 2, 48, 0)]);
        Assert.All(output, v => Assert.True(float.IsFinite(v) && Math.Abs(v) < 16, $"{type}: {v}"));
        var silence = Render(synth, 20);
        foreach (var v in silence.AsSpan(silence.Length - Rate))
            Assert.False(float.IsSubnormal(v));
    }

    [Fact]
    public void PitchBendMovesThePitchByItsRange()
    {
        var synth = Create(new() { ["f1.on"] = 0, ["voice.bend_range"] = 2 }, p => p.Tables!["a"] = new SynthTableRef { Source = "factory:basic/sine" });
        var track = new TrackSnapshot("t", 0, 1);
        var block = new float[SongEngine.BlockFrames * 2];
        var output = new float[Rate * 2];
        for (var b = 0; b < Rate / SongEngine.BlockFrames; b++)
        {
            NoteEvent[] events = b switch
            {
                0 => [new NoteEvent(0, 69, 100, true)],
                90 => [new NoteEvent(0, 0, 16383, false, NoteEventKind.PitchBend)],
                _ => [],
            };
            Array.Clear(block);
            synth.Process(track, events, block, new EffectContext(Rate, 120, true, b * SongEngine.BlockFrames));
            block.CopyTo(output, b * SongEngine.BlockFrames * 2);
        }

        Assert.Equal(440, Frequency(output, 2000, 20000), 0);
        Assert.Equal(440 * Math.Pow(2, 2 * 8191 / 8192.0 / 12), Frequency(output, 26000, 46000), 0);
    }

    [Fact]
    public void AMacroRouteInNormalizedSpaceSilencesALevel()
    {
        void Route(SynthPatch p) => p.Matrix = [new SynthModRoute { Source = "macro1", Target = "a.level", Amount = -1 }];
        Assert.True(Rms(Note(Create(new() { ["macro1"] = 0 }, Route), 60, 0.3), 2000, 10000) > 0.05);
        Assert.True(Rms(Note(Create(new() { ["macro1"] = 1 }, Route), 60, 0.3), 2000, 10000) < 1e-4);
        // An unknown source or target is kept in the patch and ignored.
        var unknown = Create(edit: p => p.Matrix = [new SynthModRoute { Source = "from-the-future", Target = "a.level", Amount = -1 }, new SynthModRoute { Source = "macro1", Target = "nope", Amount = 1 }]);
        Assert.True(Rms(Note(unknown, 60, 0.3), 2000, 10000) > 0.05);
    }

    [Fact]
    public void PolyphonyStealsTheOldestVoiceAndMonoKeepsOne()
    {
        var poly = Create(new() { ["voice.polyphony"] = 2 });
        Render(poly, 0.2, notes: [(0, 60, 100), (100, 64, 100), (200, 67, 100)]);
        Assert.Equal(2, poly.ActiveVoices); // the stolen voice faded out in its tail slot

        var mono = Create(new() { ["voice.mode"] = 2 });
        Render(mono, 0.2, notes: [(0, 60, 100), (2000, 64, 100), (4000, 60, 0)]);
        Assert.Equal(1, mono.ActiveVoices);
    }

    [Fact]
    public void RendersAreBitIdentical()
    {
        Dictionary<string, float> values = new() { ["a.unison"] = 7, ["noise.on"] = 1, ["lfo1.shape"] = 5, ["b.on"] = 1, ["b.warp_mode"] = 6, ["b.warp"] = 0.3f };
        void Route(SynthPatch p) => p.Matrix = [new SynthModRoute { Source = "lfo1", Target = "f1.cutoff", Amount = -0.4f }, new SynthModRoute { Source = "random", Target = "a.wt_pos", Amount = 0.5f }];
        (long, int, int)[] notes = [(0, 60, 90), (3000, 64, 70), (9000, 60, 0), (20000, 64, 0)];
        var a = Render(Create(values, Route), 0.6, notes: notes);
        var b = Render(Create(values, Route), 0.6, notes: notes);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ProcessingAllocatesNothingWithSixteenVoicesOfSixteenLanes()
    {
        var values = new Dictionary<string, float>
        {
            ["a.unison"] = 16,
            ["b.on"] = 1,
            ["b.unison"] = 16,
            ["b.warp_mode"] = 6,
            ["b.warp"] = 0.2f,
            ["sub.on"] = 1,
            ["noise.on"] = 1,
            ["f2.on"] = 1,
            ["f2.type"] = (float)SynthFilterType.Ladder,
            ["voice.polyphony"] = 16,
            ["lfo2.global"] = 1,
            ["lfo2.sync"] = 1,
        };
        var patch = SynthPatch.Init();
        foreach (var (k, v) in values)
            patch.Params![k] = v;
        patch.Matrix = [new SynthModRoute { Source = "env2", Target = "f1.cutoff", Amount = 0.3f }, new SynthModRoute { Source = "lfo2", Target = "a.wt_pos", Amount = 0.5f }];
        patch.Fx = [PluginInsert.ForBuiltin(BuiltinEffects.Find("chorus")!), PluginInsert.ForBuiltin(BuiltinEffects.Find("reverb")!)];
        var synth = new WavetableSynth(Rate);
        WavetableCache.WaitForPending();
        var descriptor = InstrumentDescriptor.Wavetable(patch);
        synth.Apply(descriptor);
        var track = new TrackSnapshot("t", 0, 1);
        var block = new float[SongEngine.BlockFrames * 2];
        var on = new NoteEvent[16];
        var off = new NoteEvent[16];
        for (var i = 0; i < 16; i++)
        {
            on[i] = new NoteEvent(i * 3, 40 + i * 2, 100, true);
            off[i] = new NoteEvent(i * 3, 40 + i * 2, 0, false);
        }

        void Run(int blocks)
        {
            for (var b = 0; b < blocks; b++)
            {
                ReadOnlySpan<NoteEvent> events = (b % 40) switch { 0 => on, 30 => off, _ => [] };
                if (b % 25 == 0)
                    synth.Apply(descriptor); // UI-thread work, here on this thread: excluded below by warming it up
                Array.Clear(block);
                synth.Process(track, events, block, new EffectContext(Rate, 120, true, b * SongEngine.BlockFrames));
            }
        }

        Run(400);
        var allocated = long.MaxValue;
        var track2 = track;
        for (var attempt = 0; attempt < 3 && allocated != 0; attempt++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var b = 0; b < 1000; b++)
            {
                ReadOnlySpan<NoteEvent> events = (b % 40) switch { 0 => on, 30 => off, _ => [] };
                Array.Clear(block);
                synth.Process(track2, events, block, new EffectContext(Rate, 120, true, b * SongEngine.BlockFrames));
            }

            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ASupersawPadIsNotGrosslySlow()
    {
        var synth = Create(new() { ["a.unison"] = 7, ["b.on"] = 1, ["b.unison"] = 7, ["voice.polyphony"] = 8, ["f1.type"] = (float)SynthFilterType.Lp24, ["f1.cutoff"] = 3000 });
        Render(synth, 0.2, notes: [(0, 48, 90)]); // warm up
        // A gross-regression guard only (Debug builds on shared CI runners vary 2–3×): the best of three runs of 1 s must
        // take under 2 s. Precise costs are SynthBenchmarks' (Release, compared against the baseline).
        var best = double.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Render(synth, 1, notes: [(0, 48, 90), (10, 52, 90), (20, 55, 90), (30, 59, 90), (40, 60, 90), (50, 64, 90), (60, 67, 90), (70, 71, 90)]);
            best = Math.Min(best, watch.Elapsed.TotalSeconds);
        }

        Assert.True(best < 2, $"1 s of an 8-voice 2 × 7 pad took {best:0.000} s at best");
    }
}
