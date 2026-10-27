using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// The stereo: <see cref="NeonMusic.Songs"/> on one <see cref="AdaptiveMusicPlayer"/> each, on the
/// <see cref="NeonAudio.MusicBus"/>. Adaptive music is not positional, so this node places it: every frame it sets the
/// playing song's level from the listener's distance to <see cref="StereoPosition"/> (inverse, full within
/// <see cref="UnitSize"/>) and its <see cref="NeonMusic.MuffleParameter"/> from <see cref="Muffle"/>, which
/// <see cref="NeonAudio"/> sets from the listener's room and the doors in between. The stems are rendered (or found in the
/// cache) when the node is ready, unless <see cref="PreloadMusic"/> is off; then at the first <see cref="Play"/>. Nothing
/// allocates per frame.
/// </summary>
public sealed class NeonMusicPlayer : Node
{
    private AdaptiveMusicPlayer?[] _players = [];
    private int _current = -1;
    private int _muffleIndex = -1;

    /// <summary>Where the stereo is (world space).</summary>
    public Vector3 StereoPosition { get; set; }

    /// <summary>0: the listener is in the room with the stereo; 1: far off behind closed doors.</summary>
    public float Muffle { get; set; }

    /// <summary>The music's level at the stereo (dB, on top of the Music bus).</summary>
    [Export(Range = "-40,12,0.5")] public float VolumeDb { get; set; } = 0f;

    /// <summary>Distance within which the music is at full level (m).</summary>
    [Export(Range = "0.5,20,0.1")] public float UnitSize { get; set; } = 3f;

    /// <summary>The quietest the distance makes it (dB below <see cref="VolumeDb"/>).</summary>
    [Export(Range = "0,60,1")] public float MaxDistanceDropDb { get; set; } = 24f;

    /// <summary>Render or load the stems when ready rather than at the first play.</summary>
    [Export] public bool PreloadMusic { get; set; } = true;

    /// <summary>The stems' folder (null: <see cref="NeonMusic.CacheFolder"/>).</summary>
    public string? StemFolder { get; set; }

    /// <summary>Load the songs into memory instead of streaming them (tests).</summary>
    public bool InMemory { get; set; }

    /// <summary>The playing song's index, −1 when stopped.</summary>
    public int Current => _current;

    public bool IsPlaying => _current >= 0;

    public int SongCount => NeonMusic.Songs.Count;

    /// <summary>The playing song, null when stopped.</summary>
    public NeonSong? CurrentSong => _current >= 0 ? NeonMusic.Songs[_current] : null;

    /// <summary>The player of song <paramref name="index"/> (null until created).</summary>
    public AdaptiveMusicPlayer? GetPlayer(int index) => index >= 0 && index < _players.Length ? _players[index] : null;

    /// <summary>Raised when a song starts (its index) or the music stops (−1).</summary>
    [Signal] public event Action<int>? SongChanged;

    protected override void OnReady()
    {
        base.OnReady();
        _players = new AdaptiveMusicPlayer?[NeonMusic.Songs.Count];
        if (PreloadMusic)
            for (var i = 0; i < _players.Length; i++)
                EnsurePlayer(i);
    }

    /// <summary>Plays song <paramref name="index"/> (−1: the current one, or the first), stopping any other.</summary>
    public void Play(int index = -1)
    {
        if (_players.Length == 0)
            return;
        if (index < 0)
            index = Math.Max(_current, 0);
        index %= _players.Length;
        if (index == _current)
            return;
        if (_current >= 0)
            _players[_current]?.Stop();
        var player = EnsurePlayer(index);
        _current = index;
        Apply(player, snap: true);
        player.Play();
        SongChanged?.Invoke(index);
    }

    /// <summary>The next song (the first when stopped).</summary>
    public void Next() => Play(_current < 0 ? 0 : (_current + 1) % Math.Max(_players.Length, 1));

    /// <summary>Stops the music with a half-beat fade.</summary>
    public void Stop()
    {
        if (_current < 0)
            return;
        _players[_current]?.Stop();
        _current = -1;
        SongChanged?.Invoke(-1);
    }

    /// <summary>Plays when stopped, stops when playing.</summary>
    public void Toggle()
    {
        if (IsPlaying)
            Stop();
        else
            Play();
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (_current >= 0 && _players[_current] is { } player)
            Apply(player, snap: false);
    }

    /// <summary>The level the distance from <paramref name="listener"/> gives (dB).</summary>
    public float LevelAt(Vector3 listener)
    {
        var d = Vector3.Distance(listener, StereoPosition);
        var unit = MathF.Max(UnitSize, 0.1f);
        var drop = d <= unit ? 0f : 20f * MathF.Log10(unit / d);
        return VolumeDb + MathF.Max(drop, -MaxDistanceDropDb);
    }

    private void Apply(AdaptiveMusicPlayer player, bool snap)
    {
        player.VolumeDb = LevelAt(NeonAudio.ListenerPosition(this));
        if (_muffleIndex < 0)
            _muffleIndex = player.GetParameterIndex(NeonMusic.MuffleParameter);
        if (_muffleIndex < 0)
            return;
        if (snap)
            player.SetParameterImmediate(_muffleIndex, Muffle);
        else
            player.SetParameter(_muffleIndex, Muffle);
    }

    private AdaptiveMusicPlayer EnsurePlayer(int index)
    {
        if (_players[index] is { } existing)
            return existing;
        var player = new AdaptiveMusicPlayer
        {
            Name = $"Song{index}",
            Music = NeonMusic.CreateMusic(index, folder: StemFolder, inMemory: InMemory),
            Bus = NeonAudio.MusicBus,
            VolumeDb = VolumeDb,
        };
        AddChild(player);
        _players[index] = player;
        return player;
    }
}
