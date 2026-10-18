namespace MainframeEngine.Editor.Music;

/// <summary>A factory preset of the wavetable synth: built in code from the parameter registry, factory tables only.</summary>
public sealed record FactoryPreset(string Category, string Name, Func<SynthPatch> Make);

/// <summary>
/// The wavetable synth's factory presets (Bass, Lead, Pad, Pluck, Keys, FX). Written in C# so they always match the
/// current parameters; each <see cref="FactoryPreset.Make"/> returns a fresh patch.
/// </summary>
public static class FactoryPresets
{
    public static readonly string[] Categories = ["Bass", "Lead", "Pad", "Pluck", "Keys", "FX"];

    private const float Mono = 1, Legato = 2;
    private const float Lp12 = (float)SynthFilterType.Lp12, Lp24 = (float)SynthFilterType.Lp24, Hp12 = (float)SynthFilterType.Hp12,
        Bp12 = (float)SynthFilterType.Bp12, Ladder = (float)SynthFilterType.Ladder, CombPlus = (float)SynthFilterType.CombPlus;
    private const float Fm = (float)SynthWarp.Fm;

    public static readonly FactoryPreset[] All =
    [
        // ── Bass ──
        P("Bass", "Sub Bass", "basic/sine", null, V(("f1.cutoff", 400), ("env1.decay", 0.3f), ("env1.sustain", 0.9f), ("env1.release", 0.1f),
            ("voice.mode", Mono), ("voice.glide", 0.03f), ("sub.on", 1), ("sub.level", 0.5f))),
        P("Bass", "Reese", "basic/saw", "basic/saw", V(("a.unison", 4), ("a.detune", 0.15f), ("a.width", 0.4f), ("b.on", 1), ("b.unison", 4),
            ("b.detune", 0.18f), ("f1.cutoff", 900), ("f1.reso", 0.2f), ("env2.decay", 0.4f), ("voice.mode", Mono)),
            R(("env2", "f1.cutoff", 0.2f))),
        P("Bass", "Saw Bass", "basic/saw", null, V(("f1.type", Ladder), ("f1.cutoff", 300), ("f1.reso", 0.4f), ("f1.drive", 6), ("env2.attack", 0.001f),
            ("env2.decay", 0.25f), ("voice.mode", Legato), ("voice.glide", 0.05f)), R(("env2", "f1.cutoff", 0.45f))),
        P("Bass", "Square Bass", "basic/square", null, V(("f1.cutoff", 600), ("env2.decay", 0.3f), ("sub.on", 1), ("voice.mode", Mono)),
            R(("env2", "f1.cutoff", 0.3f))),
        P("Bass", "FM Bass", "basic/sine", "basic/sine", V(("a.warp_mode", Fm), ("a.warp", 0.3f), ("b.on", 1), ("b.octave", 1), ("b.level", 0),
            ("f1.cutoff", 2000), ("env2.decay", 0.35f), ("voice.mode", Mono)), R(("env2", "a.warp", 0.4f))),
        P("Bass", "Wobble Bass", "basic/saw", null, V(("f1.cutoff", 200), ("f1.reso", 0.5f), ("lfo1.sync", 1), ("lfo1.division", 3),
            ("voice.mode", Mono)), R(("lfo1", "f1.cutoff", 0.55f))),
        P("Bass", "Pluck Bass", "basic/saw", null, V(("f1.cutoff", 250), ("f1.reso", 0.3f), ("env2.decay", 0.18f), ("env1.decay", 0.5f),
            ("env1.sustain", 0.2f), ("env1.release", 0.08f)), R(("env2", "f1.cutoff", 0.5f))),

        // ── Lead ──
        P("Lead", "Supersaw Lead", "basic/saw", "basic/saw", V(("a.unison", 7), ("a.detune", 0.3f), ("b.on", 1), ("b.unison", 5), ("b.octave", 1),
            ("b.level", 0.4f), ("f1.cutoff", 6000), ("voice.mode", Legato), ("voice.glide", 0.04f)), fx: [("reverb", V(("mix", 20)))]),
        P("Lead", "Sync Lead", "analog/sync-sweep", null, V(("a.wt_pos", 0.3f), ("env2.decay", 0.6f), ("env2.sustain", 0.2f), ("f1.cutoff", 8000)),
            R(("env2", "a.wt_pos", 0.5f))),
        P("Lead", "Square Lead", "basic/square", null, V(("a.unison", 2), ("a.detune", 0.1f), ("f1.type", Lp12), ("f1.cutoff", 5000), ("lfo1.rate", 5),
            ("lfo1.delay", 0.3f), ("lfo1.rise", 0.4f)), R(("lfo1", "a.fine", 0.03f, true)), fx: [("delay", V(("mix", 20)))]),
        P("Lead", "Vowel Lead", "formant/vowels", null, V(("lfo1.rate", 0.5f), ("f1.cutoff", 7000)), R(("lfo1", "a.wt_pos", 0.8f))),
        P("Lead", "FM Lead", "digital/fm", null, V(("a.wt_pos", 0.4f), ("env2.decay", 0.5f), ("voice.mode", Mono)), R(("env2", "a.wt_pos", 0.4f)),
            fx: [("delay", V(("mix", 18)))]),
        P("Lead", "Glide Lead", "basic/saw", null, V(("voice.mode", Legato), ("voice.glide", 0.12f), ("f1.type", Ladder), ("f1.cutoff", 3000), ("f1.reso", 0.2f))),
        P("Lead", "Bell Lead", "harmonic/bell", null, V(("env1.decay", 1.2f), ("env1.sustain", 0.3f), ("env1.release", 0.6f)), fx: [("reverb", V(("mix", 25)))]),

        // ── Pad ──
        P("Pad", "Warm Pad", "basic/saw", "basic/saw", V(("a.unison", 5), ("a.detune", 0.2f), ("b.on", 1), ("b.unison", 5), ("b.octave", -1),
            ("b.level", 0.5f), ("f1.cutoff", 1800), ("env1.attack", 0.8f), ("env1.release", 1.5f), ("env2.attack", 1.2f), ("env2.sustain", 0.6f)),
            R(("env2", "f1.cutoff", 0.2f)), fx: [("chorus", V()), ("reverb", V(("mix", 35)))]),
        P("Pad", "Strings Pad", "analog/pwm", null, V(("a.wt_pos", 0.5f), ("a.unison", 6), ("a.detune", 0.18f), ("lfo1.rate", 0.3f), ("f1.type", Lp12),
            ("f1.cutoff", 4000), ("env1.attack", 0.5f), ("env1.release", 1)), R(("lfo1", "a.wt_pos", 0.3f))),
        P("Pad", "Choir Pad", "formant/vowels", null, V(("a.wt_pos", 0.3f), ("a.unison", 4), ("a.detune", 0.1f), ("lfo1.rate", 0.15f),
            ("env1.attack", 0.7f), ("env1.release", 1.5f)), R(("lfo1", "a.wt_pos", 0.5f, true)), fx: [("reverb", V(("mix", 40)))]),
        P("Pad", "Glass Pad", "harmonic/bell", null, V(("a.unison", 3), ("a.detune", 0.05f), ("f1.type", Hp12), ("f1.cutoff", 300), ("env1.attack", 0.4f),
            ("env1.release", 2)), fx: [("delay", V(("mix", 25))), ("reverb", V(("mix", 35)))]),
        P("Pad", "Evolving Pad", "harmonic/build-up", "basic/shapes", V(("a.wt_pos", 0.2f), ("b.on", 1), ("b.level", 0.4f), ("lfo1.sync", 1),
            ("lfo1.division", 8), ("lfo1.global", 1), ("env1.attack", 1), ("env1.release", 2)), R(("lfo1", "a.wt_pos", 0.7f), ("lfo1", "b.wt_pos", 0.6f))),
        P("Pad", "Dark Pad", "basic/square", null, V(("a.unison", 4), ("a.detune", 0.15f), ("f1.cutoff", 600), ("f1.reso", 0.3f), ("env1.attack", 0.9f),
            ("env1.release", 1.5f)), fx: [("reverb", V(("mix", 30)))]),
        P("Pad", "Air Pad", "basic/sine", null, V(("a.unison", 3), ("noise.on", 1), ("noise.type", 1), ("noise.level", 0.2f), ("f1.cutoff", 3000),
            ("f2.on", 1), ("f2.type", Hp12), ("f2.cutoff", 200), ("env1.attack", 1.2f), ("env1.release", 2)), fx: [("reverb", V(("mix", 40)))]),

        // ── Pluck ──
        P("Pluck", "Saw Pluck", "basic/saw", null, V(("a.unison", 3), ("a.detune", 0.12f), ("f1.cutoff", 400), ("f1.reso", 0.2f), ("env2.decay", 0.2f),
            ("env1.decay", 0.4f), ("env1.sustain", 0), ("env1.release", 0.2f)), R(("env2", "f1.cutoff", 0.55f))),
        P("Pluck", "Bell Pluck", "harmonic/bell", null, V(("env1.decay", 0.6f), ("env1.sustain", 0), ("env1.release", 0.4f)), fx: [("reverb", V(("mix", 20)))]),
        P("Pluck", "Comb Pluck", "basic/sine", null, V(("a.on", 0), ("noise.on", 1), ("noise.level", 0), ("f1.type", CombPlus), ("f1.cutoff", 261.6f),
            ("f1.reso", 0.95f), ("f1.keytrack", 1), ("env2.decay", 0.02f), ("env1.decay", 1), ("env1.sustain", 0), ("env1.release", 0.3f)),
            R(("env2", "noise.level", 1))),
        P("Pluck", "Marimba", "basic/sine", "basic/sine", V(("b.on", 1), ("b.octave", 2), ("b.level", 0.3f), ("env1.decay", 0.35f), ("env1.sustain", 0),
            ("env1.release", 0.2f))),
        P("Pluck", "Digital Pluck", "digital/bitcrush", null, V(("a.wt_pos", 0.6f), ("env2.decay", 0.2f), ("env1.decay", 0.3f), ("env1.sustain", 0)),
            R(("env2", "a.wt_pos", -0.5f))),
        P("Pluck", "Square Pluck", "basic/square", null, V(("f1.cutoff", 1500), ("env2.decay", 0.12f), ("env1.decay", 0.25f), ("env1.sustain", 0)),
            R(("env2", "f1.cutoff", 0.4f))),

        // ── Keys ──
        P("Keys", "E-Piano", "basic/sine", "basic/sine", V(("a.warp_mode", Fm), ("a.warp", 0.15f), ("b.on", 1), ("b.octave", 2), ("b.level", 0),
            ("env2.decay", 0.8f), ("env1.decay", 2), ("env1.sustain", 0.3f), ("env1.release", 0.3f)),
            R(("env2", "a.warp", 0.3f), ("velocity", "a.warp", 0.3f)), fx: [("chorus", V(("mix", 30)))]),
        P("Keys", "Organ", "harmonic/build-up", null, V(("a.wt_pos", 0.25f), ("env1.attack", 0.005f), ("env1.release", 0.05f), ("lfo1.rate", 6)),
            R(("lfo1", "a.level", -0.1f)), fx: [("chorus", V())]),
        P("Keys", "Clav", "analog/pwm", null, V(("a.wt_pos", 0.8f), ("f1.type", Hp12), ("f1.cutoff", 400), ("env1.decay", 0.3f), ("env1.sustain", 0.2f),
            ("env1.release", 0.05f))),
        P("Keys", "Soft Keys", "basic/triangle", null, V(("a.unison", 2), ("a.detune", 0.05f), ("env1.decay", 1.5f), ("env1.sustain", 0.4f),
            ("env1.release", 0.4f), ("env1.level", 0.7f)), R(("velocity", "env1.level", 0.3f))),
        P("Keys", "Toy Piano", "harmonic/bell", "basic/triangle", V(("a.wt_pos", 0.6f), ("b.on", 1), ("b.octave", 1), ("b.level", 0.3f),
            ("env1.decay", 0.5f), ("env1.sustain", 0))),
        P("Keys", "Synth Brass", "basic/saw", null, V(("a.unison", 3), ("a.detune", 0.1f), ("f1.cutoff", 900), ("f1.reso", 0.15f), ("env2.attack", 0.08f),
            ("env2.decay", 0.4f), ("env2.sustain", 0.4f), ("env1.attack", 0.03f)), R(("env2", "f1.cutoff", 0.4f))),

        // ── FX ──
        P("FX", "Riser", "basic/saw", null, V(("a.unison", 7), ("noise.on", 1), ("noise.level", 0.5f), ("f1.cutoff", 800), ("lfo1.shape", 2),
            ("lfo1.mode", 2), ("lfo1.rate", 0.125f)), R(("lfo1", "a.coarse", 0.25f), ("lfo1", "f1.cutoff", 0.6f))),
        P("FX", "Laser Zap", "basic/square", null, V(("env2.decay", 0.25f), ("env1.decay", 0.3f), ("env1.sustain", 0), ("env1.release", 0.05f)),
            R(("env2", "a.coarse", 0.3f))),
        P("FX", "Wind", "basic/sine", null, V(("a.on", 0), ("noise.on", 1), ("noise.type", 1), ("noise.level", 0.8f), ("f1.type", Bp12), ("f1.cutoff", 800),
            ("f1.reso", 0.6f), ("lfo1.shape", 6), ("lfo1.rate", 0.3f), ("env1.attack", 1), ("env1.release", 1.5f)), R(("lfo1", "f1.cutoff", 0.3f, true))),
        P("FX", "Alarm", "basic/square", null, V(("lfo1.shape", 4), ("lfo1.rate", 4)), R(("lfo1", "a.coarse", 0.06f))),
        P("FX", "Bitcrush Drone", "digital/bitcrush", null, V(("a.unison", 3), ("lfo1.rate", 0.1f), ("env1.release", 1)), R(("lfo1", "a.wt_pos", 0.8f)),
            fx: [("bitcrusher", V(("bits", 6))), ("reverb", V(("mix", 30)))]),
        P("FX", "Sci-Fi Bleeps", "basic/sine", null, V(("lfo1.shape", 5), ("lfo1.rate", 8)), R(("lfo1", "a.coarse", 0.2f, true)), fx: [("delay", V(("mix", 30)))]),
    ];

    private static (string Id, float Value)[] V(params (string Id, float Value)[] values) => values;

    private static (string Source, string Target, float Amount, bool Bipolar)[] R(params (string Source, string Target, float Amount)[] routes) =>
        [.. routes.Select(static r => (r.Source, r.Target, r.Amount, false))];

    private static (string Source, string Target, float Amount, bool Bipolar)[] R(params (string Source, string Target, float Amount, bool Bipolar)[] routes) => routes;

    private static FactoryPreset P(string category, string name, string tableA, string? tableB, (string Id, float Value)[] values,
        (string Source, string Target, float Amount, bool Bipolar)[]? routes = null, (string Id, (string Id, float Value)[] Values)[]? fx = null) =>
        new(category, name, () =>
        {
            var patch = SynthPatch.Init();
            patch.Tables!["a"] = new SynthTableRef { Source = FactoryWavetables.Prefix + tableA };
            if (tableB is not null)
                patch.Tables["b"] = new SynthTableRef { Source = FactoryWavetables.Prefix + tableB };
            foreach (var (id, value) in values)
                patch.Params![id] = value;
            if (routes is { Length: > 0 })
                patch.Matrix = [.. routes.Select(static r => new SynthModRoute { Source = r.Source, Target = r.Target, Amount = r.Amount, Bipolar = r.Bipolar })];
            if (fx is { Length: > 0 })
            {
                patch.Fx = [.. fx.Select(static e =>
                {
                    var insert = PluginInsert.ForBuiltin(BuiltinEffects.Find(e.Id)!);
                    foreach (var (id, value) in e.Values)
                        insert.Params![id] = value;
                    return insert;
                })];
            }

            return patch;
        });
}
