using MainframeEngine.Audio.Music;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// The Adaptive panel's Baked mode (A5): plays the song's last bake, its <c>.mmusic</c> and stems, through a real
/// <see cref="AdaptiveMusicInstance"/> on the editor's <see cref="AudioServer"/> (the Music bus), exactly what a game
/// plays. The manifest is read again when its file changed (a new bake). Parameters, stingers and Stop go to the instance;
/// the panel and the Sections lane read its state. Without an audio server or a bake, it is unavailable.
/// </summary>
public sealed class BakedAudition : IDisposable
{
    private readonly Func<AudioServer?> _server;
    private AdaptiveMusicInstance? _instance;
    private AudioServer? _instanceServer;
    private AdaptiveMusic? _music;
    private string? _loadedPath;
    private DateTime _loadedTime;

    public BakedAudition(Func<AudioServer?> server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
    }

    /// <summary>The instance playing the bake (null before the first Play).</summary>
    public AdaptiveMusicInstance? Instance => _instance;

    /// <summary>The bake being played, or null.</summary>
    public AdaptiveMusic? Music => _music;

    public bool Playing => _instance?.Playing == true;

    /// <summary>The song's bake exists and an audio server can play it.</summary>
    public bool Available(SongDocument document) => _server() is not null && ManifestPath(document) is { } path && File.Exists(path);

    /// <summary>The absolute path of the song's manifest, or null.</summary>
    public static string? ManifestPath(SongDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return AssetDatabase.Current.ToAbsolutePath(SongBaker.ManifestPathFor(document.Song, document.Name));
    }

    /// <summary>
    /// Plays the bake from <paramref name="section"/> (null: its start) with <paramref name="parameters"/> as the values;
    /// false (with the reason logged) when it cannot.
    /// </summary>
    public bool Play(SongDocument document, string? section, IReadOnlyDictionary<string, float> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (_server() is not { } server)
            return false;
        if (!Load(document))
            return false;
        if (_instance is null || !ReferenceEquals(_instanceServer, server))
        {
            _instance?.Dispose();
            _instance = new AdaptiveMusicInstance(server);
            _instanceServer = server;
        }

        if (!ReferenceEquals(_instance.Music, _music))
            _instance.Music = _music;
        foreach (var (name, value) in parameters)
            _instance.SetParameterImmediate(name, value);
        _instance.Play(section);
        return _instance.Playing;
    }

    // The manifest, read again when its file changed since the last load.
    private bool Load(SongDocument document)
    {
        if (ManifestPath(document) is not { } path || !File.Exists(path))
        {
            Log.Warning($"[Music] '{document.Name}' has no bake to play: Bake it first (Ctrl+B).");
            return false;
        }

        var time = File.GetLastWriteTimeUtc(path);
        if (_music is not null && string.Equals(path, _loadedPath, StringComparison.Ordinal) && time == _loadedTime)
            return true;
        try
        {
            Stop();
            _music = AdaptiveMusic.Load(path);
            _loadedPath = path;
            _loadedTime = time;
            if (!_music.Preload())
                Log.Warning($"[Music] The bake of '{document.Name}' misses {_music.MissingStems} stem(s): those lanes play silent. Bake again.");
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Log.Warning($"[Music] The bake of '{document.Name}' cannot be read: {e.Message}");
            _music = null;
            return false;
        }
    }

    public void SetParameter(string name, float value)
    {
        if (_instance is { Playing: true } instance && instance.GetParameterIndex(name) >= 0)
            instance.SetParameter(name, value);
    }

    public void TriggerStinger(string name)
    {
        if (_instance is { Playing: true } instance)
            instance.TriggerStinger(name);
    }

    /// <summary>Stops at once (the tails of what played are cut).</summary>
    public void Stop() => _instance?.StopImmediately();

    public void Dispose()
    {
        _instance?.Dispose();
        _instance = null;
        _music = null;
    }
}
