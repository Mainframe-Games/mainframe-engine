using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StbImageSharp;

namespace MainframeEngine.Editor;

/// <summary>
/// Image thumbnails for the FileSystem panel, cached as PNGs in a folder (the editor uses
/// <c>&lt;project&gt;/.mainframe/cache/thumbnails</c>). <see cref="TryGet"/> returns a valid cached thumbnail or queues
/// one generation (at most two run at a time, on the thread pool); <see cref="Pump"/> (main thread) reports finished
/// jobs through <see cref="Ready"/> and <see cref="Failed"/>. A thumbnail is valid while its source keeps the size and
/// modification time recorded next to it (<c>&lt;key&gt;.src</c>).
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    private readonly ConcurrentQueue<(string Source, string? Thumbnail, string? Error)> _finished = new();
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal); // source → stamp
    private readonly Dictionary<string, string> _failed = new(StringComparer.Ordinal); // source → stamp
    private readonly SemaphoreSlim _workers = new(2, 2);
    private readonly CancellationTokenSource _cancel = new();
    private int _disposed;

    /// <param name="cacheDirectory">Folder for the cached PNGs (created on first write).</param>
    /// <param name="size">Longest side of a thumbnail, in pixels (images are never enlarged).</param>
    public ThumbnailCache(string cacheDirectory, int size = 96)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        CacheDirectory = Path.GetFullPath(cacheDirectory);
        Size = size;
    }

    public string CacheDirectory { get; }

    public int Size { get; }

    /// <summary>Raised by <see cref="Pump"/>: (source image, thumbnail PNG).</summary>
    public event Action<string, string>? Ready;

    /// <summary>Raised by <see cref="Pump"/>: (source image, message) for images that could not be decoded or written.</summary>
    public event Action<string, string>? Failed;

    /// <summary>
    /// True with the thumbnail's path when a valid one is cached. Otherwise queues its generation (once per source
    /// version; failed versions are not retried until the file changes) and returns false.
    /// </summary>
    public bool TryGet(string imagePath, out string? thumbnailPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        thumbnailPath = null;
        if (Volatile.Read(ref _disposed) != 0)
            return false;

        var source = Path.GetFullPath(imagePath);
        var info = new FileInfo(source);
        if (!info.Exists)
            return false;
        var stamp = Stamp(info);
        var (png, sidecar) = PathsFor(source);
        if (IsValid(png, sidecar, stamp))
        {
            thumbnailPath = png;
            return true;
        }

        if (_failed.TryGetValue(source, out var failedStamp) && failedStamp == stamp)
            return false;
        if (_pending.TryGetValue(source, out var pendingStamp) && pendingStamp == stamp)
            return false;
        _pending[source] = stamp;
        var token = _cancel.Token;
        _ = Task.Run(() => GenerateAsync(source, stamp, png, sidecar, token), token);
        return false;
    }

    /// <summary>Main thread: raises <see cref="Ready"/>/<see cref="Failed"/> for finished jobs; returns how many.</summary>
    public int Pump()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return 0;
        var count = 0;
        while (_finished.TryDequeue(out var job))
        {
            count++;
            var stamp = _pending.GetValueOrDefault(job.Source);
            _pending.Remove(job.Source);
            if (job.Thumbnail is not null)
            {
                _failed.Remove(job.Source);
                Ready?.Invoke(job.Source, job.Thumbnail);
            }
            else
            {
                if (stamp is not null)
                    _failed[job.Source] = stamp;
                Failed?.Invoke(job.Source, job.Error ?? $"Could not read '{Path.GetFileName(job.Source)}'.");
            }
        }

        return count;
    }

    /// <summary>Cancels queued work; running jobs finish but are not reported.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cancel.Cancel();
        _cancel.Dispose();
        // The semaphore is not disposed: a job may still be releasing it.
    }

    private async Task GenerateAsync(string source, string stamp, string png, string sidecar, CancellationToken token)
    {
        try
        {
            await _workers.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (token.IsCancellationRequested)
                return;
            var image = Decode(source);
            var (width, height, pixels) = Downscale(image.Width, image.Height, image.Data, Size);
            if (token.IsCancellationRequested)
                return;
            AtomicFile.WriteAllBytes(png, Png.EncodeRgba8(width, height, pixels));
            AtomicFile.WriteAllBytes(sidecar, Encoding.UTF8.GetBytes($"{stamp}\n{source}\n"));
            _finished.Enqueue((source, png, null));
        }
        catch (Exception e)
        {
            // Corrupt or unsupported images (StbImageSharp throws InvalidOperationException and others), I/O errors.
            if (!token.IsCancellationRequested)
                _finished.Enqueue((source, null, $"Cannot read the image '{Path.GetFileName(source)}': {e.Message}"));
        }
        finally
        {
            _workers.Release();
        }
    }

    private static ImageResult Decode(string source)
    {
        using var stream = File.OpenRead(source);
        return ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha)
               ?? throw new InvalidDataException("the image could not be decoded.");
    }

    /// <summary>Box-filters RGBA8 down to fit <paramref name="size"/>² (aspect kept, never enlarged), weighting colour by alpha.</summary>
    internal static (int Width, int Height, byte[] Pixels) Downscale(int width, int height, byte[] rgba, int size)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new InvalidDataException("the image has no pixels.");
        var scale = Math.Min(1.0, Math.Min((double)size / width, (double)size / height));
        var outWidth = Math.Max(1, (int)Math.Round(width * scale));
        var outHeight = Math.Max(1, (int)Math.Round(height * scale));
        if (outWidth == width && outHeight == height)
            return (width, height, rgba.AsSpan(0, width * height * 4).ToArray());

        var result = new byte[outWidth * outHeight * 4];
        for (var y = 0; y < outHeight; y++)
        {
            var y0 = y * height / outHeight;
            var y1 = Math.Max(y0 + 1, (y + 1) * height / outHeight);
            for (var x = 0; x < outWidth; x++)
            {
                var x0 = x * width / outWidth;
                var x1 = Math.Max(x0 + 1, (x + 1) * width / outWidth);
                long r = 0, g = 0, b = 0, a = 0, count = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = sy * width * 4;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var i = row + sx * 4;
                        var alpha = rgba[i + 3];
                        r += rgba[i] * alpha;
                        g += rgba[i + 1] * alpha;
                        b += rgba[i + 2] * alpha;
                        a += alpha;
                        count++;
                    }
                }

                var o = (y * outWidth + x) * 4;
                if (a > 0)
                {
                    result[o] = (byte)((r + a / 2) / a);
                    result[o + 1] = (byte)((g + a / 2) / a);
                    result[o + 2] = (byte)((b + a / 2) / a);
                }

                result[o + 3] = (byte)((a + count / 2) / count);
            }
        }

        return (outWidth, outHeight, result);
    }

    private (string Png, string Sidecar) PathsFor(string source)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{source}|{Size}")).AsSpan(0, 20));
        return (Path.Combine(CacheDirectory, key + ".png"), Path.Combine(CacheDirectory, key + ".src"));
    }

    private static string Stamp(FileInfo info) =>
        string.Create(CultureInfo.InvariantCulture, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}");

    private static bool IsValid(string png, string sidecar, string stamp)
    {
        try
        {
            if (!File.Exists(png) || !File.Exists(sidecar))
                return false;
            using var reader = new StreamReader(sidecar, Encoding.UTF8);
            return reader.ReadLine() == stamp;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
