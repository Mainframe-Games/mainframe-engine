using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace MainframeEngine;

/// <summary>What an upload did.</summary>
/// <param name="Success">The server accepted it (2xx).</param>
/// <param name="StatusCode">The last HTTP status (0 when no response arrived).</param>
/// <param name="Error">Why it failed, or null.</param>
public readonly record struct CrashUploadResult(bool Success, int StatusCode, string? Error);

/// <summary>Sends a report bundle to the game's endpoint (<see cref="CrashReporter.Uploader"/>).</summary>
public interface ICrashUploader
{
    /// <summary>Sends <paramref name="report"/>'s bundle on a worker; never throws for network errors (see the result).</summary>
    Task<CrashUploadResult> SendAsync(CrashReportInfo report, CancellationToken cancellationToken = default);
}

/// <summary>
/// The crash service's uploader (the only crash code that opens a connection, docs/design/crash-reporting.md#upload-opt-in):
/// the BCL <see cref="HttpClient"/>, no SDK. <see cref="CrashUploadFormat.SentryEnvelope"/> POSTs a
/// <see cref="SentryEnvelope"/> to the DSN's envelope endpoint with <c>X-Sentry-Auth</c>;
/// <see cref="CrashUploadFormat.Zip"/> POSTs <c>multipart/form-data</c> with <c>report</c> (report.json) and
/// <c>bundle</c> (the bundle as a zip) to the URL. Logs go only with <see cref="CrashUploadSettings.IncludeLog"/>, dumps
/// only with <see cref="CrashUploadSettings.IncludeDumps"/>. Server errors (5xx, 408, 429) and network failures are retried
/// <see cref="MaxAttempts"/> times with back-off. The server sees the player's IP address: the game's privacy policy must
/// say so.
/// </summary>
public sealed class HttpCrashUploader : ICrashUploader, IDisposable
{
    /// <summary>Attempts per report.</summary>
    public const int MaxAttempts = 3;

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public HttpCrashUploader(CrashUploadSettings settings, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.Url))
            throw new ArgumentException("crash.upload.url is empty: nothing would be sent.", nameof(settings));
        Settings = settings;
        if (settings.Format == CrashUploadFormat.SentryEnvelope)
        {
            if (!SentryDsn.TryParse(settings.Url, out var dsn))
                throw new ArgumentException($"crash.upload.url '{settings.Url}' is not a Sentry DSN (https://key@host/project).", nameof(settings));
            Dsn = dsn;
        }
        else if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out _))
        {
            throw new ArgumentException($"crash.upload.url '{settings.Url}' is not an absolute URL.", nameof(settings));
        }

        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = TimeSpan.FromSeconds(30);
        _ownsClient = true;
    }

    public CrashUploadSettings Settings { get; }

    /// <summary>The parsed DSN (sentry-envelope only).</summary>
    public SentryDsn? Dsn { get; }

    /// <summary>The delay after failed attempt N (1-based) before the next; tests shorten it.</summary>
    public Func<int, TimeSpan> RetryDelay { get; init; } = static attempt => TimeSpan.FromSeconds(attempt * attempt);

    /// <summary>The uploader for a project's <c>crash.upload</c>, or null when it names no endpoint (or a bad one, logged).</summary>
    public static HttpCrashUploader? FromProject(CrashProjectSettings crash)
    {
        ArgumentNullException.ThrowIfNull(crash);
        if (!crash.Enabled || string.IsNullOrWhiteSpace(crash.Upload.Url))
            return null;
        try
        {
            return new HttpCrashUploader(crash.Upload);
        }
        catch (ArgumentException e)
        {
            Log.Warning($"[Crash] Reports will not be sent: {e.Message}");
            return null;
        }
    }

    public async Task<CrashUploadResult> SendAsync(CrashReportInfo report, CancellationToken cancellationToken = default)
    {
        byte[] body;
        string contentType;
        try
        {
            (body, contentType) = await Task.Run(() => BuildBody(report.Folder), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return new CrashUploadResult(false, 0, $"The report could not be read: {e.Message}");
        }

        var uri = Dsn?.EnvelopeUri ?? new Uri(Settings.Url);
        var last = new CrashUploadResult(false, 0, "not sent");
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan? retryAfter = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                if (Dsn is { } dsn)
                    request.Headers.TryAddWithoutValidation("X-Sentry-Auth", dsn.AuthHeader);
                using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                    return new CrashUploadResult(true, status, null);
                last = new CrashUploadResult(false, status, $"The server answered {status} {response.ReasonPhrase}.");
                if (!IsRetryable(response.StatusCode))
                    return last;
                retryAfter = response.Headers.RetryAfter?.Delta;
            }
            catch (HttpRequestException e)
            {
                last = new CrashUploadResult(false, 0, e.Message);
            }
            catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
            {
                last = new CrashUploadResult(false, 0, $"Timed out: {e.Message}");
            }

            if (attempt < MaxAttempts)
            {
                var delay = retryAfter is { } after && after < TimeSpan.FromSeconds(60) ? after : RetryDelay(attempt);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        return last;
    }

    private static bool IsRetryable(HttpStatusCode code) =>
        (int)code >= 500 || code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    /// <summary>The request body and its content type for the bundle in <paramref name="folder"/>.</summary>
    internal (byte[] Body, string ContentType) BuildBody(string folder)
    {
        var report = CrashReport.FromJson(File.ReadAllBytes(Path.Combine(folder, CrashReport.FileName)));
        return Settings.Format == CrashUploadFormat.SentryEnvelope ? BuildEnvelope(folder, report) : BuildMultipart(folder);
    }

    private (byte[], string) BuildEnvelope(string folder, CrashReport report)
    {
        string? log = null;
        var attachments = new List<SentryEnvelope.Attachment>();
        if (Settings.IncludeLog)
        {
            foreach (var name in (ReadOnlySpan<string>)[CrashReport.LogFileName, CrashReport.LogTailFileName])
            {
                var path = Path.Combine(folder, name);
                if (!File.Exists(path))
                    continue;
                var bytes = File.ReadAllBytes(path);
                log ??= Encoding.UTF8.GetString(bytes);
                attachments.Add(new SentryEnvelope.Attachment(name, bytes, "text/plain"));
            }
        }

        foreach (var name in (ReadOnlySpan<string>)[CrashReport.SettingsFileName, "crashreport.json"])
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path))
                attachments.Add(new SentryEnvelope.Attachment(name, File.ReadAllBytes(path), "application/json"));
        }

        if (Settings.IncludeDumps && Path.Combine(folder, CrashReport.DumpFileName) is var dump && File.Exists(dump))
            attachments.Add(new SentryEnvelope.Attachment(CrashReport.DumpFileName, File.ReadAllBytes(dump), "application/octet-stream"));
        return (SentryEnvelope.Build(report, Dsn!.Value, log, attachments), "application/x-sentry-envelope");
    }

    private (byte[], string) BuildMultipart(string folder)
    {
        var boundary = "mainframe-" + Guid.NewGuid().ToString("N");
        using var body = new MemoryStream();
        void Part(string headers, ReadOnlySpan<byte> content)
        {
            body.Write(Encoding.ASCII.GetBytes($"--{boundary}\r\n{headers}\r\n\r\n"));
            body.Write(content);
            body.Write("\r\n"u8);
        }

        Part("Content-Disposition: form-data; name=\"report\"\r\nContent-Type: application/json",
            File.ReadAllBytes(Path.Combine(folder, CrashReport.FileName)));
        Part($"Content-Disposition: form-data; name=\"bundle\"; filename=\"{Path.GetFileName(folder)}.zip\"\r\nContent-Type: application/zip",
            Zip(folder));
        body.Write(Encoding.ASCII.GetBytes($"--{boundary}--\r\n"));
        return (body.ToArray(), "multipart/form-data; boundary=" + boundary);
    }

    /// <summary>The bundle as a zip: what <see cref="CrashUploadSettings.IncludeLog"/> and <see cref="CrashUploadSettings.IncludeDumps"/> allow.</summary>
    internal byte[] Zip(string folder)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.GetFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (!Settings.IncludeLog && name is CrashReport.LogFileName or CrashReport.LogTailFileName)
                    continue;
                if (!Settings.IncludeDumps && name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (name == CrashReporter.HandledFileName)
                    continue;
                zip.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
            }
        }

        return output.ToArray();
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"HttpCrashUploader({Settings.Format})");
}
