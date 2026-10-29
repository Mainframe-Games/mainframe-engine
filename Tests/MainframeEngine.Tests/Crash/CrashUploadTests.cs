using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MainframeEngine.Tests.Crash;

[Collection(nameof(CrashReporterState))]
public sealed class CrashUploadTests
{
    [Fact]
    public void DsnsParseIntoTheEnvelopeEndpoint()
    {
        Assert.True(SentryDsn.TryParse("https://abc123@o1.ingest.sentry.io/4507", out var dsn));
        Assert.Equal("abc123", dsn.Key);
        Assert.Equal("https://o1.ingest.sentry.io/api/4507/envelope/", dsn.EnvelopeUri.AbsoluteUri);
        Assert.StartsWith("Sentry sentry_version=7, sentry_key=abc123, ", dsn.AuthHeader, StringComparison.Ordinal);

        Assert.True(SentryDsn.TryParse("http://k@localhost:9000/sentry/7", out var selfHosted));
        Assert.Equal("http://localhost:9000/sentry/api/7/envelope/", selfHosted.EnvelopeUri.AbsoluteUri);

        Assert.False(SentryDsn.TryParse("https://o1.ingest.sentry.io/4507", out _)); // no key
        Assert.False(SentryDsn.TryParse("not a url", out _));
        Assert.False(SentryDsn.TryParse("https://k@host/", out _)); // no project
    }

    private static CrashReport SampleReport()
    {
        var report = new CrashReport
        {
            Id = "0123456789abcdef0123456789abcdef",
            Kind = CrashKind.ManagedException,
            Timestamp = new DateTime(2026, 10, 11, 10, 42, 7, DateTimeKind.Utc),
            App = new CrashAppInfo { Name = "Forest Demo", Version = "1.2.0", Engine = "0.42.0" },
            Gpu = new CrashGpuInfo { Name = "Apple M3 Pro", VendorId = "0x106b" },
        };
        report.Session.Scene = "Assets/Scenes/Forest.mscene";
        report.Session.Callback = "OnPhysicsProcess";
        report.Exception.Add(new CrashException
        {
            Type = "System.InvalidOperationException",
            Message = "outer",
            Frames =
            [
                new CrashFrame { Method = "Game.Player.OnPhysicsProcess(Single)", File = "Src/Player.cs", Line = 42, Module = "Game.dll" },
                new CrashFrame { Method = "MainframeEngine.SceneTree.RunPhysicsStepCore(Single)", Module = "MainframeEngine.dll" },
            ],
        });
        report.Exception.Add(new CrashException { Type = "System.FormatException", Message = "inner" });
        report.Context["level"] = "north-ridge";
        return report;
    }

    [Fact]
    public void TheEnvelopeIsAHeaderAnEventAndAttachments()
    {
        Assert.True(SentryDsn.TryParse("https://key@sentry.example.com/1", out var dsn));
        const string log = "2026-10-11T10:42:06.000Z [WARN] engine Audio: underrun\n   continued\n2026-10-11T10:42:07.000Z [ERROR] game Player: oops\n";
        var bytes = SentryEnvelope.Build(SampleReport(), dsn, log, [new SentryEnvelope.Attachment("log.txt", Encoding.UTF8.GetBytes(log), "text/plain")]);

        var text = Encoding.UTF8.GetString(bytes);
        var lines = text.Split('\n');
        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal("0123456789abcdef0123456789abcdef", header.RootElement.GetProperty("event_id").GetString());
        using var itemHeader = JsonDocument.Parse(lines[1]);
        Assert.Equal("event", itemHeader.RootElement.GetProperty("type").GetString());
        var eventLength = itemHeader.RootElement.GetProperty("length").GetInt32();
        var eventStart = lines[0].Length + 1 + lines[1].Length + 1;
        using var evt = JsonDocument.Parse(bytes.AsMemory(eventStart, eventLength));
        var root = evt.RootElement;
        Assert.Equal("fatal", root.GetProperty("level").GetString());
        Assert.Equal("Forest Demo@1.2.0", root.GetProperty("release").GetString());
        // Innermost cause first, each stack oldest frame first (Sentry's order).
        var values = root.GetProperty("exception").GetProperty("values");
        Assert.Equal("System.FormatException", values[0].GetProperty("type").GetString());
        Assert.Equal("System.InvalidOperationException", values[1].GetProperty("type").GetString());
        var frames = values[1].GetProperty("stacktrace").GetProperty("frames");
        Assert.Equal("MainframeEngine.SceneTree.RunPhysicsStepCore(Single)", frames[0].GetProperty("function").GetString());
        Assert.False(frames[0].GetProperty("in_app").GetBoolean());
        Assert.Equal(42, frames[1].GetProperty("lineno").GetInt32());
        Assert.True(frames[1].GetProperty("in_app").GetBoolean());
        Assert.Equal("Apple M3 Pro", root.GetProperty("contexts").GetProperty("gpu").GetProperty("name").GetString());
        Assert.Equal("north-ridge", root.GetProperty("extra").GetProperty("level").GetString());
        var crumbs = root.GetProperty("breadcrumbs").GetProperty("values");
        Assert.Equal(2, crumbs.GetArrayLength());
        Assert.Equal("warning", crumbs[0].GetProperty("level").GetString());
        Assert.Equal("engine.Audio", crumbs[0].GetProperty("category").GetString());
        Assert.Equal("underrun\n   continued", crumbs[0].GetProperty("message").GetString());
        Assert.Equal("game.Player", crumbs[1].GetProperty("category").GetString());
        Assert.Contains("\"type\":\"attachment\"", text, StringComparison.Ordinal);
        Assert.Contains("\"filename\":\"log.txt\"", text, StringComparison.Ordinal);
    }

    private static string WriteBundle(CrashScratch scratch, bool withLog = true)
    {
        CrashReporter.Install(scratch.Options());
        Log.Info("[Test] a line for the log");
        var folder = CrashReporter.WriteReport(new InvalidOperationException("for upload"), CrashKind.ManagedException)!;
        if (!withLog)
            File.Delete(Path.Combine(folder, CrashReport.LogFileName));
        File.WriteAllBytes(Path.Combine(folder, CrashReport.DumpFileName), [9, 9, 9]);
        CrashReporter.Shutdown();
        return folder;
    }

    private static CrashReportInfo InfoOf(string folder)
    {
        var report = CrashReporter.LoadFolder(folder)!;
        return new CrashReportInfo(folder, report.Id, report.Kind, report.Timestamp, report.Summary);
    }

    [Fact]
    public async Task TheEnvelopeIsPostedWithTheSentryAuthHeader()
    {
        using var scratch = new CrashScratch();
        var folder = WriteBundle(scratch);
        using var server = new TestServer();
        var settings = new CrashUploadSettings { Url = $"http://public-key@127.0.0.1:{server.Port}/77" };
        using var uploader = new HttpCrashUploader(settings);

        var result = await uploader.SendAsync(InfoOf(folder), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        var request = Assert.Single(server.Requests);
        Assert.Equal("/api/77/envelope/", request.Path);
        Assert.Equal("application/x-sentry-envelope", request.ContentType);
        Assert.Contains("sentry_key=public-key", request.Auth, StringComparison.Ordinal);
        var body = Encoding.UTF8.GetString(request.Body);
        Assert.Contains("for upload", body, StringComparison.Ordinal);
        Assert.Contains("\"filename\":\"log.txt\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("dump.dmp", body, StringComparison.Ordinal); // dumps only with includeDumps
    }

    [Fact]
    public async Task TheZipFormatPostsTheBundleWithoutTheLogWhenAsked()
    {
        using var scratch = new CrashScratch();
        var folder = WriteBundle(scratch);
        using var server = new TestServer();
        using var uploader = new HttpCrashUploader(new CrashUploadSettings
        {
            Url = $"http://127.0.0.1:{server.Port}/reports",
            Format = CrashUploadFormat.Zip,
            IncludeLog = false,
        });

        var result = await uploader.SendAsync(InfoOf(folder), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        var request = Assert.Single(server.Requests);
        Assert.StartsWith("multipart/form-data; boundary=", request.ContentType, StringComparison.Ordinal);
        var zipBytes = uploader.Zip(folder);
        using var zip = new ZipArchive(new MemoryStream(zipBytes));
        var names = zip.Entries.Select(e => e.Name).ToList();
        Assert.Contains(CrashReport.FileName, names);
        Assert.DoesNotContain(CrashReport.LogFileName, names);
        Assert.DoesNotContain(CrashReport.DumpFileName, names);
        Assert.Contains("name=\"bundle\"", Encoding.UTF8.GetString(request.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerErrorsAreRetriedAndClientErrorsAreNot()
    {
        using var scratch = new CrashScratch();
        var folder = WriteBundle(scratch);
        using var server = new TestServer(500, 200);
        using var uploader = new HttpCrashUploader(new CrashUploadSettings { Url = $"http://k@127.0.0.1:{server.Port}/1" })
        {
            RetryDelay = static _ => TimeSpan.FromMilliseconds(10),
        };

        Assert.True((await uploader.SendAsync(InfoOf(folder), TestContext.Current.CancellationToken)).Success);
        Assert.Equal(2, server.Requests.Count);

        using var refusing = new TestServer(403);
        using var refused = new HttpCrashUploader(new CrashUploadSettings { Url = $"http://k@127.0.0.1:{refusing.Port}/1" })
        {
            RetryDelay = static _ => TimeSpan.FromMilliseconds(10),
        };
        var result = await refused.SendAsync(InfoOf(folder), TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal(403, result.StatusCode);
        Assert.Single(refusing.Requests);
    }

    [Fact]
    public void NothingIsSentWithoutAnEndpoint()
    {
        Assert.Null(HttpCrashUploader.FromProject(new CrashProjectSettings()));
        var bad = new CrashProjectSettings();
        bad.Upload.Url = "https://no-key.example.com/1"; // not a DSN
        Assert.Null(HttpCrashUploader.FromProject(bad));
        var off = new CrashProjectSettings { Enabled = false };
        off.Upload.Url = "https://k@sentry.example.com/1";
        Assert.Null(HttpCrashUploader.FromProject(off));
        Assert.Throws<ArgumentException>(() => new HttpCrashUploader(new CrashUploadSettings()));
    }

    /// <summary>A local HTTP server answering each request with the next status (the last repeats).</summary>
    private sealed class TestServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly int[] _statuses;
        private readonly Thread _thread;
        private int _served;

        public sealed record Request(string Path, string? ContentType, string? Auth, byte[] Body);

        public TestServer(params int[] statuses)
        {
            _statuses = statuses.Length == 0 ? [200] : statuses;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _thread = new Thread(Serve) { IsBackground = true };
            _thread.Start();
        }

        public int Port { get; }

        public List<Request> Requests { get; } = [];

        private void Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                using var body = new MemoryStream();
                context.Request.InputStream.CopyTo(body);
                lock (Requests)
                    Requests.Add(new Request(context.Request.Url!.AbsolutePath, context.Request.ContentType, context.Request.Headers["X-Sentry-Auth"], body.ToArray()));
                context.Response.StatusCode = _statuses[Math.Min(_served++, _statuses.Length - 1)];
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
