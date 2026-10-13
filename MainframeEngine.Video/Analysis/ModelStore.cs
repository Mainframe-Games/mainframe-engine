using Whisper.net.Ggml;

namespace MainframeEngine.Video.Analysis;

/// <summary>
/// The per-user cache of downloaded Whisper models. Models are fetched from Hugging Face through
/// Whisper.net's downloader on first use and never bundled with the app.
/// </summary>
public sealed class ModelStore
{
    /// <summary>A cached or downloaded file at least this fraction of the approximate size is treated as complete.</summary>
    private const double MinimumCompleteFraction = 0.9;

    public ModelStore(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        CacheDirectory = cacheDirectory;
    }

    public string CacheDirectory { get; }

    public string PathFor(WhisperModel model) => Path.Combine(CacheDirectory, WhisperModelInfo.For(model).FileName);

    public bool IsDownloaded(WhisperModel model) => File.Exists(PathFor(model));

    /// <summary>
    /// True when the cached file for <paramref name="model"/> exists and its length is at least
    /// <c>90%</c> of the expected download size, i.e. it is unlikely to be a truncated or corrupt file.
    /// </summary>
    public bool IsPlausiblyComplete(WhisperModel model)
    {
        var path = PathFor(model);
        if (!File.Exists(path))
        {
            return false;
        }

        var expected = WhisperModelInfo.For(model).ApproximateBytes;
        return new FileInfo(path).Length >= expected * MinimumCompleteFraction;
    }

    /// <summary>Returns the model's path, downloading it first if it is not cached.</summary>
    /// <param name="model">Which Whisper model to ensure is present.</param>
    /// <param name="progress">Fraction of the approximate size received, in <c>[0, 1]</c>.</param>
    /// <param name="cancellationToken">Cancels the download; a partial file is not left behind.</param>
    /// <exception cref="ModelDownloadException">The download failed. The partial file is removed.</exception>
    /// <exception cref="OperationCanceledException">Cancelled. The partial file is removed.</exception>
    public async Task<string> EnsureAsync(WhisperModel model, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var info = WhisperModelInfo.For(model);
        var path = PathFor(model);
        if (File.Exists(path))
        {
            return path;
        }

        var partial = PartPathFor(path);
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            DeleteAbandonedParts(path, DateTime.UtcNow);
            cancellationToken.ThrowIfCancellationRequested();
            using var source = await WhisperGgmlDownloader.Default
                .GetGgmlModelAsync(info.GgmlType, QuantizationType.NoQuantization, cancellationToken)
                .ConfigureAwait(false);

            long received = 0;
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    progress?.Report(Math.Min(1.0, (double)received / info.ApproximateBytes));
                }
            }

            if (received < info.ApproximateBytes * MinimumCompleteFraction)
            {
                TryDelete(partial);
                throw new ModelDownloadException(
                    $"The download of {info.Key} ended early ({received} of about {info.ApproximateBytes} bytes). "
                    + "Check your connection and try again.");
            }

            Commit(partial, path);
            progress?.Report(1.0);
            return path;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(partial);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            TryDelete(partial);
            throw new ModelDownloadException(
                $"Could not download the {info.Key} speech model (about {info.ApproximateBytes / 1_000_000} MB). "
                + $"Check your internet connection and try again. Models are cached in {CacheDirectory}.\n\n{ex.Message}",
                ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryDelete(partial);
            throw new ModelDownloadException(
                $"Could not download the {info.Key} speech model (about {info.ApproximateBytes / 1_000_000} MB). "
                + $"Check your internet connection and try again. Models are cached in {CacheDirectory}.\n\n{ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Where one download of <paramref name="path"/> writes until it is complete: <c>&lt;file&gt;.&lt;guid&gt;.part</c> beside
    /// it, unique per download, so two first-time downloads of the same model never write into one file.
    /// </summary>
    internal static string PartPathFor(string path) => $"{path}.{Guid.NewGuid():N}.part";

    /// <summary>
    /// How long a <c>.part</c> file can go unwritten before it counts as abandoned. A running download writes it every few
    /// milliseconds, so only one interrupted by a crash or a kill gets this old.
    /// </summary>
    internal static readonly TimeSpan AbandonedPartAge = TimeSpan.FromHours(1);

    /// <summary>
    /// Deletes the <c>.part</c> files of <paramref name="path"/> left by downloads that were interrupted (each download
    /// writes its own, see <see cref="PartPathFor"/>, so nothing else would ever replace them). A file written to within
    /// <see cref="AbandonedPartAge"/> belongs to a download still running and is kept.
    /// </summary>
    internal static void DeleteAbandonedParts(string path, DateTime nowUtc)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var part in Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".*.part"))
        {
            if (nowUtc - File.GetLastWriteTimeUtc(part) > AbandonedPartAge)
            {
                TryDelete(part);
            }
        }
    }

    /// <summary>
    /// Moves a finished download into place atomically. When another download got there first, the model already in
    /// place is kept (a model being loaded is never replaced under it) and this copy is deleted.
    /// </summary>
    internal static void Commit(string partial, string path)
    {
        try
        {
            File.Move(partial, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            TryDelete(partial);
        }
    }

    /// <summary>Removes the cached model file for <paramref name="model"/>, if one is present.</summary>
    public void Delete(WhisperModel model) => TryDelete(PathFor(model));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort; a stale .part file is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
