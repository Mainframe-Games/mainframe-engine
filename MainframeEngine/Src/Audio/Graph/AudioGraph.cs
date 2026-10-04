using SoundFlow.Abstracts;
using SoundFlow.Components;
using SoundFlow.Structs;

namespace MainframeEngine.Audio;

/// <summary>
/// The SoundFlow component graph for one bus layout: a nested <see cref="Mixer"/> per bus (effects, then a
/// <see cref="BusProcessor"/> fader) and every bus's pooled <see cref="AudioVoice"/>s, all created up front on the
/// game thread. The audio thread mixes it from the master bus down; replacing the layout builds a new graph and
/// swaps it in (<see cref="AudioCommandType.SwapGraph"/>).
/// </summary>
internal sealed class AudioGraph : IDisposable
{
    /// <summary>
    /// Volume giving unity gain on a stereo SoundFlow component: SoundFlow applies its constant-power pan law at the
    /// centre (0.5 → √0.5 per channel) on every component, so each mixer and player in the chain is set to √2.
    /// </summary>
    public const float UnityVolume = 1.4142135f;

    private AudioGraph(BusNode[] buses, AudioVoice[] voices)
    {
        Buses = buses;
        Voices = voices;
    }

    public BusNode[] Buses { get; }
    public AudioVoice[] Voices { get; }

    /// <summary>Builds the graph for a validated <paramref name="layout"/>; <paramref name="busGains"/> are the initial fader gains.</summary>
    public static AudioGraph Build(AudioEngine engine, AudioFormat format, AudioBusLayout layout, ReadOnlySpan<float> busGains)
    {
        var count = layout.Buses.Count;
        var buses = new BusNode[count];
        var voiceCount = 0;
        foreach (var info in layout.Buses)
            voiceCount += Math.Max(0, info.MaxVoices);
        var voices = new AudioVoice[voiceCount];

        var nextVoice = 0;
        for (var b = 0; b < count; b++)
        {
            var info = layout.Buses[b];
            var mixer = new Mixer(engine, format) { Name = "Bus " + info.Name, Volume = UnityVolume };
            foreach (var effect in info.Effects)
            {
                if (effect is not null)
                    mixer.AddModifier(effect.CreateModifier(format));
            }

            var fader = new BusProcessor(format.SampleRate, busGains[b]);
            mixer.AddModifier(fader);

            var parent = -1;
            if (b > 0)
            {
                for (var p = 0; p < b; p++)
                {
                    if (layout.Buses[p].Name == info.Send)
                        parent = p;
                }

                buses[parent].Mixer.AddComponent(mixer);
            }

            var first = nextVoice;
            for (var v = 0; v < info.MaxVoices; v++)
            {
                var voice = new AudioVoice(engine, format, nextVoice, b);
                voices[nextVoice++] = voice;
                mixer.AddComponent(voice.Player);
            }

            buses[b] = new BusNode(mixer, fader, parent, first, info.MaxVoices);
        }

        return new AudioGraph(buses, voices);
    }

    /// <summary>Audio thread: mixes every bus into <paramref name="buffer"/>.</summary>
    public void Mix(Span<float> buffer, int channels) => Buses[0].Mixer.Process(buffer, channels);

    /// <summary>Audio thread: stops every voice (the graph is being retired).</summary>
    public void HaltAll()
    {
        foreach (var voice in Voices)
            voice.Halt();
    }

    /// <summary>Game thread, once the audio thread no longer uses the graph.</summary>
    public void Dispose()
    {
        // The master mixer disposes its components recursively (bus mixers, players, their sources).
        Buses[0].Mixer.Dispose();
    }

    internal sealed record BusNode(Mixer Mixer, BusProcessor Fader, int Parent, int FirstVoice, int VoiceCount);
}
