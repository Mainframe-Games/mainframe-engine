using MainframeEngine.Audio;

namespace MainframeEngine.Editor;

/// <summary>
/// The editor's sound preview (owned by <see cref="EditorWorkspace"/>): one voice at a time, a non-positional one-shot on
/// the Master bus that ignores pause, so the edit camera and listener do not matter. The FileSystem panel, the
/// inspector's <see cref="AudioStream"/> rows and the sound designer play through it; running the game stops it.
/// Without an <see cref="AudioServer"/> (or on the null device) it "plays" silently.
/// </summary>
public sealed class AudioPreview
{
    private readonly Func<AudioServer?> _server;
    private AudioVoiceHandle _voice;
    private AudioStream? _stream;
    private string? _file;
    private Resource? _loaded;

    public AudioPreview(Func<AudioServer?> server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
    }

    /// <summary>Raised when a preview starts or ends (the Play/Stop icons refresh).</summary>
    public event Action? Changed;

    /// <summary>The stream being previewed, or null.</summary>
    public AudioStream? Stream => _stream;

    /// <summary>The audio file being previewed (full path), or null.</summary>
    public string? FilePath => _file;

    public bool IsPlaying(AudioStream? stream) => stream is not null && ReferenceEquals(_stream, stream);

    /// <summary>True while the file at <paramref name="fullPath"/> is previewed.</summary>
    public bool IsPlayingFile(string? fullPath) =>
        _file is not null && fullPath is not null && string.Equals(Path.GetFullPath(fullPath), _file, StringComparison.Ordinal);

    /// <summary>Stops the current preview and plays <paramref name="stream"/>; false when it cannot load (reported).</summary>
    public bool Play(AudioStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Start(stream, file: null, loaded: null);
    }

    /// <summary>Stops <paramref name="stream"/> when it is the preview, else plays it.</summary>
    public void Toggle(AudioStream stream)
    {
        if (IsPlaying(stream))
            Stop();
        else
            Play(stream);
    }

    /// <summary>
    /// Previews an audio file (<c>.wav</c>/<c>.ogg</c>/…, or a <c>.mres</c> holding an <see cref="AudioStream"/>), loaded
    /// through <see cref="ResourceLoader"/> (import settings applied, shared cache: a long file streams).
    /// </summary>
    public bool PlayFile(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        var full = Path.GetFullPath(fullPath);
        AudioStream stream;
        try
        {
            stream = ResourceLoader.Load<AudioStream>(AssetDatabase.Current.ToProjectPath(full));
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e) || e is InvalidCastException)
        {
            Stop();
            Log.Error($"[Audio] Cannot preview {Path.GetFileName(full)}: {e.Message}");
            return false;
        }

        return Start(stream, full, stream);
    }

    /// <summary>Stops the file's preview when it plays, else plays it.</summary>
    public void ToggleFile(string fullPath)
    {
        if (IsPlayingFile(fullPath))
            Stop();
        else
            PlayFile(fullPath);
    }

    public void Stop()
    {
        if (_stream is null)
            return;
        if (_voice.IsValid && _server() is { } server)
            server.Stop(_voice);
        Clear();
        Changed?.Invoke();
    }

    /// <summary>Once per frame: notices a preview that ended (no events, no allocation).</summary>
    public void Tick()
    {
        if (_stream is null || (_voice.IsValid && _server() is { } server && server.IsPlaying(_voice)))
            return;
        Clear();
        Changed?.Invoke();
    }

    private bool Start(AudioStream stream, string? file, Resource? loaded)
    {
        if (_voice.IsValid && _server() is { } current)
            current.Stop(_voice);
        Clear();
        _loaded = loaded;
        var server = _server();
        var voice = server?.PlayOneShot(stream, bus: AudioBusLayout.MasterBus, processMode: ProcessMode.Always) ?? default;
        if (server is not null && !voice.IsValid)
        {
            // Logged once by the stream itself; repeated here so every click explains the silence.
            Log.Warning($"[Audio] Cannot preview {file ?? stream.ResourceName}: {stream.LoadError ?? "no free voice"}");
            Clear();
            Changed?.Invoke();
            return false;
        }

        _voice = voice;
        _stream = stream;
        _file = file;
        Changed?.Invoke();
        return true;
    }

    private void Clear()
    {
        _voice = default;
        _stream = null;
        _file = null;
        if (_loaded is { } loaded)
        {
            _loaded = null;
            if (loaded.ReferenceCount > 0)
                loaded.Release();
        }
    }
}
