using MainframeEngine.Audio.Music;

namespace MainframeEngine.Audio;

/// <summary>
/// What the audio thread plays for one <see cref="AdaptiveMusic"/>: the compiled plan and every section's stem sources,
/// loaded on the game thread and immutable once built, so the audio thread reads it without synchronisation (it travels
/// as <see cref="AudioCommand.Ref"/> of <see cref="AudioCommandType.MusicPlay"/>).
/// </summary>
internal sealed class MusicRuntime
{
    public MusicRuntime(MusicPlan plan, MusicSectionStems[] sections)
    {
        Plan = plan;
        Sections = sections;
        LayerCount = plan.Layers.Length;
        Filters = new MusicLayerFilter[LayerCount];
        for (var i = 0; i < LayerCount; i++)
            Filters[i] = plan.Layers[i].Filter;
        foreach (var section in sections)
        {
            HasStreams |= section.Streamed;
            for (var l = 0; l < LayerCount; l++)
            {
                if (section.Bodies[l] is AudioStreamSource)
                    StreamedLayers |= 1 << l;
            }
        }
    }

    public MusicPlan Plan { get; }

    public int SampleRate => Plan.Grid.SampleRate;

    public int LayerCount { get; }

    /// <summary>Each layer's live filter.</summary>
    public MusicLayerFilter[] Filters { get; }

    /// <summary>By section index.</summary>
    public MusicSectionStems[] Sections { get; }

    /// <summary>Some section body streams (scheduling then allows for the stream prefill).</summary>
    public bool HasStreams { get; }

    /// <summary>A bit per layer whose body streams in some section (those lanes get stream channels at play).</summary>
    public int StreamedLayers { get; }
}

/// <summary>One section's sources by layer index; null plays as silence.</summary>
internal sealed class MusicSectionStems
{
    public MusicSectionStems(int layers)
    {
        Bodies = new AudioSource?[layers];
        Tails = new AudioClipData?[layers];
    }

    /// <summary>Bodies (exactly the section's length): <see cref="AudioClipData"/> in memory or <see cref="AudioStreamSource"/> streamed.</summary>
    public AudioSource?[] Bodies { get; }

    /// <summary>Tails (memory one-shots fired when the section ends at its end).</summary>
    public AudioClipData?[] Tails { get; }

    /// <summary>A stinger's single mixed stem (tail included).</summary>
    public AudioClipData? Mix { get; set; }

    /// <summary>Some body streams.</summary>
    public bool Streamed { get; set; }
}
