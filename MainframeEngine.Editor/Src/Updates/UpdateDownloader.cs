using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace MainframeEngine.Editor;

/// <summary>A downloaded, verified and extracted release: <see cref="Root"/> is what replaces the install.</summary>
public sealed record StagedUpdate(ReleaseVersion Version, string Folder, string Root, string Executable);

/// <summary>
/// Downloads a release archive into <c>&lt;updates&gt;/X.Y.Z/</c>, checks its SHA-256, extracts it to <c>app/</c> (entries
/// escaping the folder are refused; Unix modes are kept) and finds the editor executable. Any failure deletes the folder.
/// </summary>
public sealed class UpdateDownloader(HttpClient http, string editorVersion)
{
    private const int BufferSize = 81920;

    public async Task<StagedUpdate> DownloadAsync(ReleaseInfo release, ReleaseAsset asset, string rid, string updatesDirectory,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Sha256 is null)
            throw new UpdateException($"The release has no checksum for {asset.Name}, so it cannot be verified.");
        if (!string.Equals(asset.DownloadUrl.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            throw new UpdateException("The download address is not HTTPS; the update was refused.");

        var folder = Path.Combine(updatesDirectory, release.Version.ToString());
        // Off the caller's (UI) thread before the first file-system call: deleting an earlier staging can take a while.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            var archive = Path.Combine(folder, asset.Name);
            await DownloadFileAsync(asset, archive, progress, ct).ConfigureAwait(false);
            Verify(archive, asset.Sha256);
            ct.ThrowIfCancellationRequested(); // Cancel stays offered while hashing and unpacking
            var extracted = Path.Combine(folder, "app");
            Extract(archive, extracted);
            ct.ThrowIfCancellationRequested();
            File.Delete(archive);
            var root = UpdatePlatform.StagedRoot(extracted, release.Version, rid);
            var executable = UpdatePlatform.ExecutablePath(root, rid);
            if (!File.Exists(executable))
                throw new UpdateException($"The downloaded archive has no {Path.GetFileName(executable)}.");
            return new StagedUpdate(release.Version, folder, root, executable);
        }
        catch (Exception e)
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"[Editor] Could not delete {folder}: {cleanup.Message}");
            }

            // Callers only ever see UpdateException or cancellation: stray file-system errors (disk full, locked folder) end up here.
            if (e is IOException or UnauthorizedAccessException)
                throw new UpdateException($"The update could not be staged ({e.Message}).", e);
            throw;
        }
    }

    private async Task DownloadFileAsync(ReleaseAsset asset, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MainframeEngine-Editor", editorVersion));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException($"The download failed ({e.Message}).", e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"The download failed: GitHub answered {(int)response.StatusCode} ({response.ReasonPhrase}).");
            var total = response.Content.Headers.ContentLength ?? asset.Size;
            Stream source;
            try
            {
                source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                throw new UpdateException($"The download failed ({e.Message}).", e);
            }

            await using (source.ConfigureAwait(false))
            {
                var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                await using (target.ConfigureAwait(false))
                {
                    var buffer = new byte[BufferSize];
                    long done = 0;
                    int read;
                    while ((read = await ReadAsync(source, buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        done += read;
                        if (total > 0)
                            progress?.Report(Math.Min(1.0, (double)done / total));
                    }
                }
            }
        }

        progress?.Report(1.0);
    }

    private static async Task<int> ReadAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        try
        {
            return await source.ReadAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            throw new UpdateException($"The download failed ({e.Message}).", e);
        }
    }

    /// <summary>Throws <see cref="UpdateException"/> unless <paramref name="file"/>'s SHA-256 is <paramref name="sha256"/> (hex).</summary>
    public static void Verify(string file, string sha256)
    {
        using var stream = File.OpenRead(file);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("The download is damaged (its checksum does not match the release). Try again.");
    }

    /// <summary>Extracts a <c>.zip</c> or <c>.tar.gz</c>; entries outside <paramref name="destination"/> are refused.</summary>
    public static void Extract(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        try
        {
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: false);
                return;
            }

            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: false);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"The downloaded archive could not be unpacked ({e.Message}).", e);
        }
    }
}
