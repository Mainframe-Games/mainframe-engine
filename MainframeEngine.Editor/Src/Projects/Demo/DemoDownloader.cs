using System.Net;

namespace MainframeEngine.Editor;

public sealed record DemoDownloadRequest(string ParentDirectory, string FolderName, string EnginePath)
{
    public string Destination => Path.Combine(ParentDirectory, FolderName);
}

/// <summary>
/// Downloads the Demo zip for <c>editorVersion</c>, extracts and validates it in a work folder under
/// <c>downloadsDirectory</c>, points it at the engine and moves it to the destination. Runs off the UI thread; failures
/// are <see cref="DemoDownloadException"/>s, cancellation is <see cref="OperationCanceledException"/>; either way the work
/// files are deleted and the destination is not created.
/// </summary>
public sealed class DemoDownloader(HttpClient http, string editorVersion, string downloadsDirectory)
{
    public static string DefaultDownloadsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mainframe", "downloads");

    public async Task<string> DownloadAsync(DemoDownloadRequest request, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); // off the caller's thread
        Directory.CreateDirectory(downloadsDirectory);
        var id = Guid.NewGuid().ToString("N");
        var zipPath = Path.Combine(downloadsDirectory, $"demo-{id}.zip");
        var work = Path.Combine(downloadsDirectory, $"demo-{id}");
        try
        {
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
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (done > DemoArchive.MaxUncompressedBytes)
                    throw new DemoDownloadException("The demo download is larger than expected.");
                progress?.Report(total > 0 ? (double)done / total : -1);
            }
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
