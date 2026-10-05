using System.Net;

namespace MainframeEngine.Editor;

public sealed record DemoDownloadRequest(string ParentDirectory, string FolderName, string EnginePath)
{
    public string Destination => Path.Combine(ParentDirectory, FolderName);
}

/// <summary>
/// Downloads the Demo zip for <c>editorVersion</c>, extracts and validates it in a work folder under
/// <c>downloadsDirectory</c> (the zip) and a hidden staging folder beside the destination (the extracted project, so the final
/// move is a same-volume rename), points it at the engine and moves it to the destination. Runs off the UI thread; failures
/// are <see cref="DemoDownloadException"/>s, cancellation is <see cref="OperationCanceledException"/>; either way the work
/// files are deleted, the destination is not created and a parent folder the download had to create is removed again.
/// </summary>
public sealed class DemoDownloader(HttpClient http, string editorVersion, string downloadsDirectory)
{
    private const long MaxDownloadBytes = 1L << 30;

    public static string DefaultDownloadsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mainframe", "downloads");

    public async Task<string> DownloadAsync(DemoDownloadRequest request, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); // off the caller's thread
        var id = Guid.NewGuid().ToString("N");
        var zipPath = Path.Combine(downloadsDirectory, $"demo-{id}.zip");
        var work = Path.Combine(request.ParentDirectory, $".mainframe-demo-{id}"); // same volume as the destination
        var createdFolder = FirstMissingFolder(request.ParentDirectory); // removed again unless the download succeeds
        var succeeded = false;
        try
        {
            Directory.CreateDirectory(downloadsDirectory);
            Directory.CreateDirectory(request.ParentDirectory);
            await DownloadZipAsync(zipPath, progress, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var root = DemoArchive.ExtractAndValidate(zipPath, work);
            ct.ThrowIfCancellationRequested();
            EnginePathRewriter.Rewrite(Path.Combine(root, "Directory.Build.props"), GameProjectLayout.RealPath(request.EnginePath));
            if (Directory.Exists(request.Destination) && Directory.EnumerateFileSystemEntries(request.Destination).Any())
                throw new DemoDownloadException($"'{request.Destination}' already exists and is not empty.");
            if (Directory.Exists(request.Destination))
                Directory.Delete(request.Destination);
            Directory.Move(root, request.Destination);
            succeeded = true;
            return request.Destination;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new DemoDownloadException("The demo could not be saved: " + e.Message, e);
        }
        finally
        {
            TryDelete(zipPath);
            TryDeleteDirectory(work);
            if (!succeeded && createdFolder is not null)
                TryDeleteEmptyChain(request.ParentDirectory, createdFolder);
        }
    }

    /// <summary>The outermost folder of <paramref name="directory"/>'s path that does not exist yet (what creating it will add), or null.</summary>
    private static string? FirstMissingFolder(string directory)
    {
        string? missing = null;
        for (var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
             current is not null && !Directory.Exists(current);
             current = Path.GetDirectoryName(current))
            missing = current;
        return missing;
    }

    /// <summary>Deletes <paramref name="leaf"/> and its parents up to <paramref name="top"/> while they are empty.</summary>
    private static void TryDeleteEmptyChain(string leaf, string top)
    {
        try
        {
            var topPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(top));
            for (var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(leaf)); current is not null; current = Path.GetDirectoryName(current))
            {
                if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                    return;
                Directory.Delete(current);
                if (current == topPath)
                    return;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task DownloadZipAsync(string zipPath, IProgress<double>? progress, CancellationToken ct)
    {
        var url = DemoRelease.AssetUrl(editorVersion);
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.UserAgent.ParseAdd($"MainframeEngine-Editor/{editorVersion}");
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new DemoDownloadException("Could not reach GitHub: " + e.Message, e);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new DemoDownloadException($"There is no published demo for this editor version (v{editorVersion}).");
            if (!response.IsSuccessStatusCode)
                throw new DemoDownloadException($"The demo download failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
            var total = response.Content.Headers.ContentLength ?? -1;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(zipPath);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await ReadChunkAsync(source, buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (done > MaxDownloadBytes)
                    throw new DemoDownloadException("The demo download is larger than expected.");
                progress?.Report(total > 0 ? (double)done / total : -1);
            }
        }
    }

    private static async Task<int> ReadChunkAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        try
        {
            return await source.ReadAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            ct.ThrowIfCancellationRequested(); // a cancelled read can surface as an IOException: that is a cancel, not an interruption
            throw new DemoDownloadException("The demo download was interrupted: " + e.Message, e);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
