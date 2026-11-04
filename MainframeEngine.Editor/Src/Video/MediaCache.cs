using System.Collections.Concurrent;
using MainframeEngine.Video.Media;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// Per-media pictures and sounds for the timeline and the media pool, keyed by the file's hash: waveforms (kept in
/// memory), filmstrips and posters (PNGs in <c>&lt;project&gt;/.mainframe/cache/video/&lt;hash&gt;/</c>). Each is made
/// once, in the background with ffmpeg; until then the timeline draws plain clips. Main thread, except the jobs.
/// </summary>
public sealed class MediaCache : IDisposable
{
    /// <summary>Thumbnails per filmstrip; the timeline repeats them along a clip.</summary>
    public const int FilmstripFrames = 24;

    /// <summary>Filmstrip and poster height and width in pixels.</summary>
    public const int FilmstripHeight = 64;

    public const int PosterWidth = 160;

    private readonly Dictionary<string, Waveform> _waveforms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Texture2D> _filmstrips = new(StringComparer.Ordinal);
    // The hashes whose waveform, filmstrip or poster job was started: checked before anything is built, so a miss
    // (still being made, or no ffmpeg) allocates nothing on the frames that ask again.
    private readonly HashSet<string> _startedWaves = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startedStrips = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startedPosters = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Action> _done = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly SemaphoreSlim _workers = new(2, 2);
    private bool _disposed;

    public MediaCache(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        CacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    public string CacheDirectory { get; }

    /// <summary>
    /// A cache key for a file the pool shows without hashing its contents (posters): its path, size and modification time,
    /// FNV-1a, stable across runs.
    /// </summary>
    public static string KeyFor(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        long length = 0, ticks = 0;
        try
        {
            var info = new FileInfo(path); // disk-io: the editor's own cache key
            length = info.Length;
            ticks = info.LastWriteTimeUtc.Ticks;
        }
        catch (IOException)
        {
        }

        var hash = 14695981039346656037UL;
        foreach (var c in path)
            hash = (hash ^ c) * 1099511628211UL;
        hash = (hash ^ (ulong)length) * 1099511628211UL;
        hash = (hash ^ (ulong)ticks) * 1099511628211UL;
        return "p" + hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The ffmpeg jobs use; null makes nothing (tests, no ffmpeg).</summary>
    public FfmpegLocation? Ffmpeg { get; set; }

    /// <summary>Bumped when a waveform, filmstrip or poster arrives: views redraw.</summary>
    public int Version { get; private set; }

    /// <summary>The waveform of the file with <paramref name="hash"/>, or null (queued the first time).</summary>
    public Waveform? WaveformOf(string hash, string path, double duration)
    {
        if (_waveforms.TryGetValue(hash, out var waveform))
            return waveform;
        if (CanStart(_startedWaves, hash))
            StartWaveform(hash, path, duration);
        return null;
    }

    // Apart from the lookups: a lambda capturing the parameters there would allocate its closure on every call, hit or miss.
    private void StartWaveform(string hash, string path, double duration) => Start("waveform", async ct =>
    {
        var result = await new WaveformExtractor(Ffmpeg!).ExtractAsync(path, duration, null, ct).ConfigureAwait(false);
        return () => _waveforms[hash] = result;
    });

    /// <summary>The filmstrip texture of the video with <paramref name="hash"/>, or null (made the first time).</summary>
    public Texture2D? FilmstripOf(string hash, string path, double duration)
    {
        if (_filmstrips.TryGetValue(hash, out var texture))
            return texture;
        if (CanStart(_startedStrips, hash))
            StartFilmstrip(hash, path, duration);
        return null;
    }

    private void StartFilmstrip(string hash, string path, double duration)
    {
        var png = Path.Combine(CacheDirectory, hash, $"filmstrip-{FilmstripHeight}.png");
        Start("filmstrip", async ct =>
        {
            if (!File.Exists(png)) // disk-io: the editor's own cache
            {
                Directory.CreateDirectory(Path.GetDirectoryName(png)!);
                await FilmstripExtractor.ExtractAsync(Ffmpeg!, path, duration, FilmstripFrames, FilmstripHeight, png + ".tmp.png", ct).ConfigureAwait(false);
                File.Move(png + ".tmp.png", png, overwrite: true);
            }

            var encoded = await File.ReadAllBytesAsync(png, ct).ConfigureAwait(false); // disk-io: the editor's own cache
            return () => _filmstrips[hash] = Texture2D.FromEncoded(encoded);
        });
    }

    /// <summary>The poster PNG of the video with <paramref name="hash"/>, or null (made the first time).</summary>
    public string? PosterOf(string hash, string path, double duration)
    {
        var png = Path.Combine(CacheDirectory, hash, "poster.png");
        if (File.Exists(png)) // disk-io: the editor's own cache
            return png;
        if (CanStart(_startedPosters, hash))
            StartPoster(png, path, duration);
        return null;
    }

    private void StartPoster(string png, string path, double duration) => Start("poster", async ct =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        await PosterExtractor.ExtractAsync(Ffmpeg!, path, duration, PosterWidth, png + ".tmp.png", ct).ConfigureAwait(false);
        File.Move(png + ".tmp.png", png, overwrite: true);
        return static () => { };
    });

    /// <summary>Installs a waveform made elsewhere (the render golden's synthetic one, tests).</summary>
    internal void SetWaveform(string hash, Waveform waveform)
    {
        _waveforms[hash] = waveform;
        _startedWaves.Add(hash);
        Version++;
    }

    /// <summary>Main thread, every frame: installs what finished. True when something did.</summary>
    public bool Pump()
    {
        var any = false;
        while (_done.TryDequeue(out var install))
        {
            install();
            any = true;
        }

        if (any)
            Version++;
        return any;
    }

    // One background job per key per session: a failure is logged once and not retried until the editor restarts.
    // Marks the hash started; false when it was already, or nothing can run (disposed, no ffmpeg yet: asked again later).
    private bool CanStart(HashSet<string> started, string hash) => !_disposed && Ffmpeg is not null && started.Add(hash);

    private void Start(string what, Func<CancellationToken, Task<Action>> work)
    {
        var token = _cancel.Token;
        _ = Task.Run(async () =>
        {
            await _workers.WaitAsync(token).ConfigureAwait(false);
            try
            {
                _done.Enqueue(await work(token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Warning($"[Video] Could not make the {what} of a media file: {e.Message}");
            }
            finally
            {
                _workers.Release();
            }
        }, token);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cancel.Cancel();
        _cancel.Dispose();
    }
}
