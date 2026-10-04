namespace MainframeEngine;

/// <summary>
/// A live mixer bus (the runtime side of an <see cref="AudioBusInfo"/>): fader, mute, solo, voice pool and meter.
/// Changes apply on the next <see cref="AudioServer"/> flush with a ~10 ms ramp. Game thread only.
/// </summary>
public sealed class AudioBus
{
    private readonly AudioServer _server;
    private float _volumeDb;
    private bool _mute;
    private bool _solo;

    internal AudioBus(AudioServer server, int index, AudioBusInfo info, AudioBus? parent, int firstVoice)
    {
        _server = server;
        Index = index;
        Name = info.Name;
        Send = parent?.Name ?? string.Empty;
        Parent = parent;
        _volumeDb = info.VolumeDb;
        _mute = info.Mute;
        _solo = info.Solo;
        MaxVoices = Math.Max(0, info.MaxVoices);
        FirstVoice = firstVoice;
        Effects = [.. info.Effects];
    }

    public string Name { get; }

    /// <summary>Position in <see cref="AudioServer.Buses"/> (Master is 0).</summary>
    public int Index { get; }

    /// <summary>Name of the bus this one mixes into (empty for Master).</summary>
    public string Send { get; }

    public AudioBus? Parent { get; }

    /// <summary>Fader level in dB (−80 = silent).</summary>
    public float VolumeDb
    {
        get => _volumeDb;
        set
        {
            var clamped = float.IsNaN(value) ? 0f : Math.Clamp(value, AudioMath.SilenceDb, 24f);
            if (_volumeDb == clamped)
                return;
            _volumeDb = clamped;
            _server.OnBusChanged();
        }
    }

    public bool Mute
    {
        get => _mute;
        set
        {
            if (_mute == value)
                return;
            _mute = value;
            _server.OnBusChanged();
        }
    }

    /// <summary>When any bus is soloed only soloed buses — with their sub-buses and the buses they send through — are heard.</summary>
    public bool Solo
    {
        get => _solo;
        set
        {
            if (_solo == value)
                return;
            _solo = value;
            _server.OnBusChanged();
        }
    }

    /// <summary>Size of this bus's voice pool (its polyphony limit).</summary>
    public int MaxVoices { get; }

    /// <summary>Voices of the pool currently playing (or paused).</summary>
    public int ActiveVoices { get; internal set; }

    /// <summary>The bus's effect chain as applied (change it through <see cref="AudioServer.ApplyBusLayout"/>).</summary>
    public IReadOnlyList<AudioEffect> Effects { get; }

    /// <summary>Peak level of the bus output over the last audio block (linear, 0..1+), for meters.</summary>
    public float Peak { get; internal set; }

    /// <summary>Linear gain of the bus fader as sent to the mixer (volume × mute × solo).</summary>
    public float EffectiveGain { get; internal set; } = 1f;

    /// <summary>False when solo silences the voices playing directly on this bus (it only passes a soloed sub-bus through).</summary>
    public bool DirectAudible { get; internal set; } = true;

    internal int FirstVoice { get; }

    /// <summary>True if <paramref name="other"/> is this bus or one of its ancestors.</summary>
    public bool IsDescendantOf(AudioBus other)
    {
        for (var bus = this; bus is not null; bus = bus.Parent)
        {
            if (ReferenceEquals(bus, other))
                return true;
        }

        return false;
    }

    public override string ToString() => $"{Name} ({VolumeDb:0.#} dB{(Mute ? ", muted" : "")}{(Solo ? ", solo" : "")})";
}
