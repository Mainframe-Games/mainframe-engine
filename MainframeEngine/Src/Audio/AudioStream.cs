using System.Text.Json;
using MainframeEngine.Audio;

namespace MainframeEngine;

/// <summary>How an <see cref="AudioStream"/> is held while it plays.</summary>
public enum AudioLoadMode
{
    /// <summary><see cref="Memory"/> for files under 1 MB, <see cref="Stream"/> above (music, ambience beds).</summary>
    Auto,

    /// <summary>Decoded once into memory and shared by every voice playing it (short sounds, one-shots).</summary>
    Memory,

    /// <summary>Decoded while it plays by the streaming thread, one decoder per playing voice (long sounds).</summary>
    Stream,
}

/// <summary>
/// A sound file — WAV, OGG Vorbis, MP3 or FLAC — and how to play it (Godot's <c>AudioStream</c>). Assign it to an
/// <see cref="AudioPlayer"/> / <see cref="AudioPlayer2D"/> / <see cref="AudioPlayer3D"/> or pass it to
/// <see cref="AudioServer.PlayOneShot"/>. The file is decoded (memory mode) or probed (stream mode) on first use;
/// <see cref="Preload"/> does it ahead of time (players do in <c>OnEnterTree</c>).
/// </summary>
/// <remarks>
/// <see cref="Load"/> applies the file's import settings from its <c>.meta</c> sidecar
/// (<c>{"importer": "audio", "settings": {"loadMode": "Stream", "loop": true, "loopStart": 1.5, "loopEnd": 0}}</c>).
/// Decoded clips are cached by file, so several streams over one file share their samples.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Godot's name for the resource (ADR 0010); it is not a System.IO.Stream.")]
[EditorIcon("music")]
public class AudioStream : Resource
{
    /// <summary>Files at or above this size default to <see cref="AudioLoadMode.Stream"/> under <see cref="AudioLoadMode.Auto"/>.</summary>
    public const long StreamThresholdBytes = 1024 * 1024;

    private string? _file;
    private AudioLoadMode _loadMode;
    private AudioSource? _source;
    private bool _loadFailed;

    /// <summary>Path (project-relative, e.g. <c>Content/Audio/hit.wav</c>) or asset UID (<c>aud_…</c>) of the sound file.</summary>
    [Export(File = "*.wav,*.ogg,*.mp3,*.flac")]
    public string? File
    {
        get => _file;
        set
        {
            if (_file == value)
                return;
            _file = value;
            Invalidate();
        }
    }

    [Export]
    public AudioLoadMode LoadMode
    {
        get => _loadMode;
        set
        {
            if (_loadMode == value)
                return;
            _loadMode = value;
            Invalidate();
        }
    }

    /// <summary>Loops by default (a player's own <c>Loop</c> also enables it).</summary>
    [Export]
    public bool Loop { get; set; }

    /// <summary>Where a loop restarts, in seconds.</summary>
    [Export(Range = "0,3600,0.001")]
    public float LoopStart { get; set; }

    /// <summary>Where a loop wraps, in seconds; 0 = the end of the sound.</summary>
    [Export(Range = "0,3600,0.001")]
    public float LoopEnd { get; set; }

    /// <summary>The error from the last failed load, or null.</summary>
    public string? LoadError { get; private set; }

    /// <summary>True once the sound is decoded (memory) or probed (stream) successfully.</summary>
    public bool IsLoaded => _source is not null;

    /// <summary>Length in seconds (0 until loaded, or when the file does not say).</summary>
    public double Length => _source?.DurationSeconds ?? 0;

    /// <summary>Channels after loading (1 or 2; wider files are downmixed).</summary>
    public int Channels => _source?.Channels ?? 0;

    /// <summary>Sample rate of the file (0 until loaded).</summary>
    public int SampleRate => _source?.SampleRate ?? 0;

    /// <summary>True when the stream plays from the streaming thread rather than memory.</summary>
    public bool IsStreamed => _source is AudioStreamSource;

    /// <summary>A stream for <paramref name="path"/> with its <c>.meta</c> import settings applied.</summary>
    public static AudioStream Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var stream = new AudioStream { File = path };
        stream.ApplyImportSettings();
        return stream;
    }

    /// <summary>
    /// An in-memory stream over generated samples (tones, procedural audio). <paramref name="samples"/> is
    /// interleaved and used without copying; do not modify it while the stream may be playing.
    /// </summary>
    public static AudioStream FromSamples(float[] samples, int channels, int sampleRate, string name = "Generated")
    {
        var stream = new AudioStream { ResourceName = name };
        stream._source = AudioClipData.FromSamples(name, samples, channels, sampleRate);
        return stream;
    }

    /// <summary>Decodes or probes the file now; returns false (and sets <see cref="LoadError"/>) on failure.</summary>
    public bool Preload() => GetSource() is not null;

    /// <summary>The source to play, loading it on first use; null when the file is missing or unreadable (logged once).</summary>
    internal AudioSource? GetSource()
    {
        if (_source is not null || _loadFailed)
            return _source;
        if (string.IsNullOrEmpty(_file))
        {
            Fail("no file set");
            return null;
        }

        try
        {
            var path = ResolvePath(_file);
            var mode = _loadMode;
            if (mode == AudioLoadMode.Auto)
                mode = new FileInfo(path).Length >= StreamThresholdBytes ? AudioLoadMode.Stream : AudioLoadMode.Memory;
            _source = mode == AudioLoadMode.Stream ? AudioStreamSource.Probe(path) : AudioClipCache.GetOrDecode(path);
            LoadError = null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(e.Message); // a corrupt file must never take the game down: the stream just stays silent
        }

        return _source;
    }

    /// <summary>Loop start/end in source frames for the loaded source (end = −1 for "to the end").</summary>
    internal void GetLoopFrames(AudioSource source, out long start, out long end)
    {
        start = (long)Math.Max(0, LoopStart * (double)source.SampleRate);
        end = LoopEnd > LoopStart ? (long)(LoopEnd * (double)source.SampleRate) : -1;
    }

    private void Fail(string error)
    {
        _loadFailed = true;
        LoadError = error;
        Log.Error($"[Audio] Cannot load audio stream '{_file ?? ResourceName}': {error}");
    }

    private void Invalidate()
    {
        _source = null;
        _loadFailed = false;
        LoadError = null;
    }

    private static string ResolvePath(string file)
    {
        var assets = AssetDatabase.Current;
        var path = file;
        if (AssetUid.IsUid(file))
            path = assets.GetPath(file) ?? throw new FileNotFoundException($"Unknown audio asset UID '{file}'.");
        var full = assets.ToAbsolutePath(path);
        if (!System.IO.File.Exists(full))
            throw new FileNotFoundException($"Audio file not found: '{path}'.", full);
        return full;
    }

    // Import settings from the .meta sidecar ("importer": "audio").
    private void ApplyImportSettings()
    {
        if (string.IsNullOrEmpty(_file) || AssetUid.IsUid(_file))
            return;
        AssetMeta? meta;
        try
        {
            meta = AssetDatabase.Current.ReadOrCreateMeta(_file, create: false);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning($"[Audio] Ignoring unreadable import settings for '{_file}': {e.Message}");
            return;
        }

        if (meta?.Settings is not { } settings)
            return;
        if (settings.TryGetValue("loadMode", out var mode) && mode.ValueKind == JsonValueKind.String &&
            Enum.TryParse<AudioLoadMode>(mode.GetString(), ignoreCase: true, out var parsed))
            LoadMode = parsed;
        if (settings.TryGetValue("loop", out var loop) && loop.ValueKind is JsonValueKind.True or JsonValueKind.False)
            Loop = loop.GetBoolean();
        if (settings.TryGetValue("loopStart", out var start) && start.ValueKind == JsonValueKind.Number)
            LoopStart = start.GetSingle();
        if (settings.TryGetValue("loopEnd", out var end) && end.ValueKind == JsonValueKind.Number)
            LoopEnd = end.GetSingle();
    }
}

/// <summary>
/// Sound files → <see cref="AudioStream"/> with the <c>.meta</c> sidecar's import settings, so a resource property of type
/// <see cref="AudioStream"/> can reference a <c>.wav</c>/<c>.ogg</c> directly (Godot's imported <c>AudioStreamWAV</c>).
/// </summary>
public sealed class AudioImporter : IAssetImporter
{
    public string Name => "audio";

    public IReadOnlyList<string> Extensions { get; } = [".wav", ".ogg", ".mp3", ".flac"];

    public Resource Import(string fullPath, string projectPath, AssetMeta? meta) => AudioStream.Load(projectPath);
}

/// <summary>Decoded clips shared between streams over the same file (weakly held: unused clips are collected).</summary>
internal static class AudioClipCache
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, WeakReference<AudioClipData>> Clips = new(StringComparer.Ordinal);

    public static AudioClipData GetOrDecode(string fullPath)
    {
        lock (Gate)
        {
            if (Clips.TryGetValue(fullPath, out var weak) && weak.TryGetTarget(out var cached))
                return cached;
        }

        var clip = AudioClipData.Decode(fullPath);
        lock (Gate)
        {
            if (Clips.TryGetValue(fullPath, out var weak) && weak.TryGetTarget(out var raced))
                return raced;
            Clips[fullPath] = new WeakReference<AudioClipData>(clip);
        }

        return clip;
    }

    /// <summary>Forgets every cached clip (streams keep the ones they hold).</summary>
    public static void Clear()
    {
        lock (Gate)
            Clips.Clear();
    }
}
