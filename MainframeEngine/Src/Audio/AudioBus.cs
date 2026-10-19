namespace MainframeEngine;

/// <summary>
/// A live mixer bus (the runtime side of an <see cref="AudioBusInfo"/>): fader, mute, solo, voice pool and meter.
/// Changes apply on the next <see cref="AudioServer"/> flush with a ~10 ms ramp. Game thread only.
/// </summary>
public sealed class AudioBus
{
    private readonly AudioServer _server;
    private float _volumeDb;
    private float _userVolume = 1f;
    private bool _mute;
    private bool _solo;

    internal AudioBus(AudioServer server, int index, AudioBusInfo info, AudioBus? parent, int firstVoice, int firstMusicVoice)
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
        MusicVoicesSetting = info.MusicVoices;
        MusicVoices = info.ResolvedMusicVoices;
        FirstMusicVoice = firstMusicVoice;
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

    /// <summary>
    /// The player's volume for this bus (a settings slider position, 0..1, default 1; <see cref="UserSettings"/> sets it),
    /// a second gain stage on top of the designer's <see cref="VolumeDb"/>: gain = DbToLinear(VolumeDb) · UserVolume². The
    /// squared curve is perceptual (50 % ≈ −12 dB) and 0 is silent. Not part of <see cref="AudioServer.GetBusLayout"/>, so
    /// the editor never saves a player's volume into the project. Values are clamped to 0..1 (NaN = 1).
    /// </summary>
    public float UserVolume
    {
        get => _userVolume;
        set
        {
            var clamped = float.IsNaN(value) ? 1f : Math.Clamp(value, 0f, 1f);
            if (_userVolume == clamped)
                return;
            _userVolume = clamped;
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

    /// <summary>Adaptive-music voices on this bus (<see cref="AudioBusInfo.MusicVoices"/>, automatic resolved).</summary>
    public int MusicVoices { get; }

    /// <summary>The layout's <see cref="AudioBusInfo.MusicVoices"/> as written (−1: automatic).</summary>
    internal int MusicVoicesSetting { get; }

    internal int FirstMusicVoice { get; }

    /// <summary>Voices of the pool currently playing (or paused).</summary>
    public int ActiveVoices { get; internal set; }

    /// <summary>The bus's effect chain as applied (change it through <see cref="AudioServer.ApplyBusLayout"/>).</summary>
    public IReadOnlyList<AudioEffect> Effects { get; }

    /// <summary>Peak level of the bus output over the last audio block (linear, 0..1+), for meters.</summary>
    public float Peak { get; internal set; }

    /// <summary>Linear gain of the bus fader as sent to the mixer (volume × user volume² × mute × solo).</summary>
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
