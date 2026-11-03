using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace MainframeEngine;

/// <summary>
/// ADR 0183: prepares on worker threads the CPU side of the textures created while an asynchronous scene load runs
/// (<see cref="SceneLoad"/>), so their first users — the first frames' GPU uploads, terrain layer packing, the tree
/// bakers — find the work done instead of doing it on the main thread: image files are decoded (PNG/JPEG; in the Forest
/// ~3 s of the first frame) and coverage-preserving mip chains are built (<see cref="TextureImportSettings.PreserveAlphaCoverage"/>,
/// <see cref="MipChain.Build"/>; ~1.3 s for its leaf-cluster and impostor atlases). Decoded files are keyed by path and
/// decode settings, so every <see cref="Texture2D"/> of a file shares one decode. Everything is held until the load
/// finishes (<see cref="Dispose"/>).
/// </summary>
/// <remarks>
/// Textures register when they are created (<see cref="Texture2D.FromFile"/>, <see cref="Texture2D.FromPixels"/>) on the
/// load's execution flow (its worker and the tasks it starts: an <see cref="AsyncLocal{T}"/>). Lookups come from any
/// thread while the prefetch is live; a lookup of work still running waits for it.
/// </remarks>
internal sealed class ImagePrefetch : IDisposable
{
    internal readonly record struct Decoded(byte[] Rgba, int Width, int Height);

    private readonly record struct FileKey(string Path, bool FixAlphaBorder);

    // A texture's mip chain for one colour space, valid for one version of it.
    private sealed record MipEntry(int Version, bool Srgb, Task<byte[]?> Chain);

    private static readonly AsyncLocal<ImagePrefetch?> Collecting = new();
    private static readonly Lock ActiveGate = new();
    private static readonly List<ImagePrefetch> Active = [];
    private static int s_acceptingLate; // live prefetches that take late textures (Defer's no-allocation early out)

    private readonly ConcurrentDictionary<FileKey, Lazy<Task<Decoded?>>> _files = new();
    private readonly ConditionalWeakTable<Texture2D, List<MipEntry>> _mips = [];
    private readonly ConcurrentBag<Task> _jobs = [];
    private int _queued;
    private int _completed;
    private volatile bool _disposed;
    private bool _acceptsLate;

    private ImagePrefetch()
    {
    }

    /// <summary>Starts a prefetch (lookups find its work until <see cref="Dispose"/>); call <see cref="CollectOnCurrentFlow"/> where the load runs.</summary>
    public static ImagePrefetch Begin()
    {
        var prefetch = new ImagePrefetch();
        lock (ActiveGate)
            Active.Add(prefetch);
        return prefetch;
    }

    /// <summary>Textures created from now on by the calling thread's execution flow (and the tasks it starts) are prepared ahead.</summary>
    public void CollectOnCurrentFlow() => Collecting.Value = this;

    /// <summary>Jobs (file decodes and mip chains) queued so far.</summary>
    public int Count => Volatile.Read(ref _queued);

    /// <summary>Jobs finished so far (failed ones included).</summary>
    public int Completed => Volatile.Read(ref _completed);

    /// <summary>The share of queued jobs finished (1 when none were queued).</summary>
    public float Fraction => Count == 0 ? 1f : Math.Min(1f, Completed / (float)Count);

    /// <summary>Completes when every job queued so far has finished.</summary>
    public Task WhenAll() => Task.WhenAll(_jobs.ToArray());

    /// <summary>
    /// Called when a texture is created: on the flow of a live prefetch, queues its file's decode (once per file and decode
    /// settings) and, for a coverage-preserving mipmapped texture, its mip chains on the thread pool.
    /// </summary>
    internal static void OnTextureCreated(Texture2D texture)
    {
        if (Collecting.Value is not { _disposed: false } prefetch)
            return;
        prefetch.Queue(texture);
    }

    /// <summary>
    /// From now until <see cref="Dispose"/>, textures reach this prefetch when their GPU upload first needs them
    /// (<see cref="Defer"/>), not only when created on the load's flow: a scene warming up uses textures that process-wide
    /// caches created earlier (model imports, tree materials, the editor's second open of a scene).
    /// </summary>
    public void AcceptLateTextures()
    {
        lock (ActiveGate)
        {
            if (_acceptsLate || _disposed)
                return;
            _acceptsLate = true;
            s_acceptingLate++;
        }
    }

    /// <summary>
    /// Called before a file texture's GPU upload: true when a live prefetch is still preparing it — or, accepting late
    /// textures (<see cref="AcceptLateTextures"/>), has just queued it — so the upload waits for a later frame (the
    /// material keeps its fallback) instead of decoding on the main thread. False (upload now) when no prefetch takes late
    /// textures, the texture has no image file, or its pixels (and coverage mips) are ready. Allocates nothing when no
    /// prefetch takes late textures.
    /// </summary>
    internal static bool Defer(Texture2D texture)
    {
        if (Volatile.Read(ref s_acceptingLate) == 0 || texture.SourceFilePath is not { } path || IsSvg(path))
            return false;
        var settings = texture.ImportSettings;
        var key = new FileKey(path, settings.FixAlphaBorder);
        var mips = settings is { Mipmaps: true, PreserveAlphaCoverage: true };
        ImagePrefetch? accepting = null;
        foreach (var prefetch in Live())
        {
            if (prefetch._disposed)
                continue;
            if (prefetch._files.TryGetValue(key, out var entry) && (!mips || prefetch._mips.TryGetValue(texture, out _)))
                return !entry.Value.IsCompleted || (mips && prefetch.MipsPending(texture));
            if (prefetch._acceptsLate)
                accepting ??= prefetch;
        }

        if (accepting is null)
            return false;
        accepting.Queue(texture);
        return true;
    }

    /// <summary>Late textures still decoding: a load warming up waits for them (<see cref="SceneLoad"/>).</summary>
    public bool HasPendingWork => Completed < Count;

    private bool MipsPending(Texture2D texture)
    {
        if (!_mips.TryGetValue(texture, out var list))
            return false;
        lock (list)
            foreach (var entry in list)
                if (!entry.Chain.IsCompleted)
                    return true;
        return false;
    }

    // Queues the texture's file decode (once per file and decode settings) and, for a coverage-preserving mipmapped
    // texture, its mip chains on the thread pool.
    private void Queue(Texture2D texture)
    {
        var prefetch = this;
        var settings = texture.ImportSettings;
        Task<Decoded?>? pixels = null;
        if (texture.SourceFilePath is { } path)
        {
            if (IsSvg(path))
                return;
            var entry = prefetch._files.GetOrAdd(new FileKey(path, settings.FixAlphaBorder), static (k, p) => new Lazy<Task<Decoded?>>(() =>
            {
                Interlocked.Increment(ref p._queued);
                var task = Task.Run(() => p.Decode(k));
                p._jobs.Add(task);
                return task;
            }), prefetch);
            pixels = entry.Value;
        }
        else if (texture.CodePixels is { } code)
        {
            pixels = Task.FromResult<Decoded?>(new Decoded(code.Rgba, code.Width, code.Height));
        }

        if (pixels is null || settings is not { Mipmaps: true, PreserveAlphaCoverage: true })
            return;
        var version = texture.Version;
        var cutoff = settings.AlphaCoverageCutoff;
        var srgbForColour = settings.ResolveColorSpace(colorUsage: true) == TextureColorSpace.Srgb;
        var srgbForData = settings.ResolveColorSpace(colorUsage: false) == TextureColorSpace.Srgb;
        var list = prefetch._mips.GetOrCreateValue(texture);
        lock (list)
        {
            prefetch.QueueMips(list, pixels, version, srgbForColour, cutoff);
            if (srgbForData != srgbForColour)
                prefetch.QueueMips(list, pixels, version, srgbForData, cutoff);
        }
    }

    private void QueueMips(List<MipEntry> list, Task<Decoded?> pixels, int version, bool srgb, float cutoff)
    {
        Interlocked.Increment(ref _queued);
        var chain = pixels.ContinueWith(p =>
        {
            try
            {
                return !_disposed && p.Result is { } d ? MipChain.Build(d.Rgba, d.Width, d.Height, srgb, cutoff) : null;
            }
            finally
            {
                Interlocked.Increment(ref _completed);
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        list.Add(new MipEntry(version, srgb, chain));
        _jobs.Add(chain);
    }

    /// <summary>
    /// The pixels of <paramref name="fullPath"/> decoded by a live prefetch (waits for a decode still running), or false
    /// when no prefetch holds the file. The array is shared: callers must not modify it.
    /// </summary>
    internal static bool TryGet(string fullPath, bool fixAlphaBorder, out Decoded decoded)
    {
        decoded = default;
        var key = new FileKey(fullPath, fixAlphaBorder);
        foreach (var prefetch in Live())
        {
            if (!prefetch._files.TryGetValue(key, out var entry))
                continue;
            if (entry.Value.Result is not { } result)
                return false; // the decode failed: let the caller decode and report it
            decoded = result;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The coverage-preserving mip chain (<see cref="MipChain.Build"/>) of <paramref name="texture"/>'s current version in
    /// the given colour space built by a live prefetch (waits for one still building), or false. Shared: do not modify it.
    /// </summary>
    internal static bool TryGetMips(Texture2D texture, bool srgb, out byte[] chain)
    {
        chain = [];
        var version = texture.Version;
        foreach (var prefetch in Live())
        {
            if (!prefetch._mips.TryGetValue(texture, out var list))
                continue;
            Task<byte[]?>? task = null;
            lock (list)
                foreach (var entry in list)
                    if (entry.Version == version && entry.Srgb == srgb)
                        task = entry.Chain;
            if (task?.Result is not { } built)
                continue;
            chain = built;
            return true;
        }

        return false;
    }

    // The live prefetches, newest first (usually none or one).
    private static ImagePrefetch[] Live()
    {
        lock (ActiveGate)
        {
            if (Active.Count == 0)
                return [];
            var live = new ImagePrefetch[Active.Count];
            for (var i = 0; i < live.Length; i++)
                live[i] = Active[Active.Count - 1 - i];
            return live;
        }
    }

    private Decoded? Decode(FileKey key)
    {
        try
        {
            if (_disposed)
                return null;
            var (rgba, width, height) = Texture2D.DecodeImageFile(key.Path, key.FixAlphaBorder);
            return new Decoded(rgba, width, height);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
                                      or ArgumentException)
        {
            return null; // the texture decodes again where it is used, and that reports the error
        }
        finally
        {
            Interlocked.Increment(ref _completed);
        }
    }

    private static bool IsSvg(string path) => path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Stops collecting and drops every prepared image (textures decode and build on demand again).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (ActiveGate)
        {
            Active.Remove(this);
            if (_acceptsLate)
                s_acceptingLate--;
        }

        if (ReferenceEquals(Collecting.Value, this))
            Collecting.Value = null;
        _files.Clear();
        _mips.Clear();
    }
}
