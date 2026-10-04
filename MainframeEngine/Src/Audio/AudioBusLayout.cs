using MainframeEngine.Audio;
using SoundFlow.Abstracts;
using SoundFlow.Modifiers;
using SoundFlow.Structs;

namespace MainframeEngine;

/// <summary>
/// The mixer layout: a tree of buses rooted at <see cref="AudioBusLayout.MasterBus"/>, each with a fader (volume,
/// mute, solo), a voice pool and an effect chain. Stored as a regular <c>.mres</c> resource
/// (<see cref="DefaultPath"/>) so it diffs, merges and loads like every other M2 resource; the editor's project
/// settings (M10, <c>project.mfproj</c>) can embed the same resource inline instead.
/// </summary>
public class AudioBusLayout : Resource
{
    /// <summary>The root bus every other bus ultimately sends to; it always exists.</summary>
    public const string MasterBus = "Master";

    /// <summary>Where the engine looks for the project's layout (relative to the project root).</summary>
    public const string DefaultPath = "Content/Settings/AudioBusLayout.mres";

    /// <summary>Buses in mixer order. A bus's <see cref="AudioBusInfo.Send"/> must name a bus listed before it.</summary>
    [Export]
    public List<AudioBusInfo> Buses { get; set; } = [];

    /// <summary>Master → Music (4 voices), SFX (32), UI (8), Voice (8); Master itself has 16 voices.</summary>
    public static AudioBusLayout CreateDefault() => new()
    {
        ResourceName = "Default bus layout",
        Buses =
        [
            new AudioBusInfo { Name = MasterBus, Send = string.Empty, MaxVoices = 16 },
            new AudioBusInfo { Name = "Music", MaxVoices = 4 },
            new AudioBusInfo { Name = "SFX", MaxVoices = 32 },
            new AudioBusInfo { Name = "UI", MaxVoices = 8 },
            new AudioBusInfo { Name = "Voice", MaxVoices = 8 },
        ],
    };

    /// <summary>
    /// Checks names are unique and non-empty, the first bus is <see cref="MasterBus"/>, and every send names an
    /// earlier bus (so the layout is a tree). Returns null when valid, else the problem.
    /// </summary>
    public string? Validate()
    {
        if (Buses.Count == 0 || Buses[0].Name != MasterBus)
            return $"The first bus must be '{MasterBus}'.";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Buses.Count; i++)
        {
            var bus = Buses[i];
            if (bus is null || string.IsNullOrWhiteSpace(bus.Name))
                return $"Bus {i} has no name.";
            if (!seen.Add(bus.Name))
                return $"Bus name '{bus.Name}' is used twice.";
            if (i > 0 && !seen.Contains(bus.Send))
                return $"Bus '{bus.Name}' sends to '{bus.Send}', which is not a bus listed before it.";
            if (i > 0 && bus.Send == bus.Name)
                return $"Bus '{bus.Name}' sends to itself.";
            if (bus.MaxVoices is < 0 or > 1024)
                return $"Bus '{bus.Name}' has {bus.MaxVoices} voices (0–1024).";
        }

        return null;
    }
}

/// <summary>One bus of an <see cref="AudioBusLayout"/>.</summary>
public class AudioBusInfo : Resource
{
    [Export]
    public string Name { get; set; } = "Bus";

    /// <summary>The bus this one mixes into (ignored for Master).</summary>
    [Export]
    public string Send { get; set; } = AudioBusLayout.MasterBus;

    [Export(Range = "-80,24,0.1")]
    public float VolumeDb { get; set; }

    [Export]
    public bool Mute { get; set; }

    /// <summary>When any bus is soloed, only soloed buses (and the buses they send through) are heard.</summary>
    [Export]
    public bool Solo { get; set; }

    /// <summary>Voices pooled for this bus (its polyphony limit); created up front, so one-shots cause no graph churn.</summary>
    [Export(Range = "0,1024,1")]
    public int MaxVoices { get; set; } = 8;

    /// <summary>Effects applied to the bus's mix, in order, before its fader.</summary>
    [Export]
    public List<AudioEffect> Effects { get; set; } = [];
}

/// <summary>
/// A bus effect: a resource that creates the SoundFlow <see cref="SoundModifier"/> processing the bus's mix.
/// Parameters are read when the layout is applied (<see cref="AudioServer.ApplyBusLayout"/>).
/// </summary>
public abstract class AudioEffect : Resource
{
    [Export]
    public bool Enabled { get; set; } = true;

    /// <summary>Creates the modifier for a mix in <paramref name="format"/>.</summary>
    internal abstract SoundModifier CreateModifier(AudioFormat format);
}

/// <summary>One-pole low-pass (SoundFlow <c>LowPassModifier</c>).</summary>
public class AudioEffectLowPass : AudioEffect
{
    [Export(Range = "10,20000,1")]
    public float CutoffHz { get; set; } = 2000f;

    internal override SoundModifier CreateModifier(AudioFormat format) =>
        new LowPassModifier(format, Math.Clamp(CutoffHz, 10f, format.SampleRate * 0.45f)) { Enabled = Enabled };
}

/// <summary>One-pole high-pass (SoundFlow <c>HighPassModifier</c>).</summary>
public class AudioEffectHighPass : AudioEffect
{
    [Export(Range = "10,20000,1")]
    public float CutoffHz { get; set; } = 200f;

    internal override SoundModifier CreateModifier(AudioFormat format) =>
        new HighPassModifier(format, Math.Clamp(CutoffHz, 10f, format.SampleRate * 0.45f)) { Enabled = Enabled };
}

/// <summary>
/// Freeverb reverb (the engine's allocation-free <c>ReverbProcessor</c>: 8 damped combs + 4 all-passes per channel).
/// </summary>
public class AudioEffectReverb : AudioEffect
{
    /// <summary>Tail length (0..1).</summary>
    [Export(Range = "0,1,0.01")]
    public float RoomSize { get; set; } = 0.5f;

    /// <summary>High-frequency absorption (0..1).</summary>
    [Export(Range = "0,1,0.01")]
    public float Damp { get; set; } = 0.5f;

    /// <summary>Reverb level.</summary>
    [Export(Range = "0,1,0.01")]
    public float Wet { get; set; } = 0.3f;

    /// <summary>Direct (unprocessed) level.</summary>
    [Export(Range = "0,1,0.01")]
    public float Dry { get; set; } = 1f;

    /// <summary>Stereo width of the reverb (0 = mono).</summary>
    [Export(Range = "0,1,0.01")]
    public float Width { get; set; } = 1f;

    internal override SoundModifier CreateModifier(AudioFormat format) =>
        new ReverbProcessor(format.SampleRate, RoomSize, Damp, Wet, Dry, Width) { Enabled = Enabled };
}

/// <summary>Feed-forward compressor (SoundFlow <c>CompressorModifier</c>).</summary>
public class AudioEffectCompressor : AudioEffect
{
    [Export(Range = "-60,0,0.1")]
    public float ThresholdDb { get; set; } = -12f;

    [Export(Range = "1,20,0.1")]
    public float Ratio { get; set; } = 4f;

    [Export(Range = "0.1,200,0.1")]
    public float AttackMs { get; set; } = 10f;

    [Export(Range = "1,2000,1")]
    public float ReleaseMs { get; set; } = 100f;

    [Export(Range = "0,24,0.1")]
    public float KneeDb { get; set; }

    [Export(Range = "0,24,0.1")]
    public float MakeupGainDb { get; set; }

    internal override SoundModifier CreateModifier(AudioFormat format) =>
        new CompressorModifier(format, ThresholdDb, Math.Max(1f, Ratio), Math.Max(0.1f, AttackMs), Math.Max(1f, ReleaseMs),
            Math.Max(0f, KneeDb), MakeupGainDb)
        { Enabled = Enabled };
}
