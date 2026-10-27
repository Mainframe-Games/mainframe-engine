using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MainframeEngine;

/// <summary>A Sentry DSN (<c>https://&lt;key&gt;@&lt;host&gt;/&lt;project&gt;</c>): where envelopes go and how they authenticate.</summary>
/// <param name="Key">The public key.</param>
/// <param name="EnvelopeUri"><c>https://host/api/&lt;project&gt;/envelope/</c> (a path before the project id is kept).</param>
/// <param name="Dsn">The DSN text (it goes into the envelope header).</param>
public readonly record struct SentryDsn(string Key, Uri EnvelopeUri, string Dsn)
{
    /// <summary>Parses <paramref name="dsn"/>; false when it is not a DSN.</summary>
    public static bool TryParse(string? dsn, out SentryDsn result)
    {
        result = default;
        if (!Uri.TryCreate(dsn, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length == 0)
            return false;
        var key = uri.UserInfo.Split(':')[0];
        var path = uri.AbsolutePath.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        if (key.Length == 0 || slash < 0 || slash == path.Length - 1)
            return false;
        var project = path[(slash + 1)..];
        var prefix = path[..slash];
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        result = new SentryDsn(key, new Uri($"{uri.Scheme}://{uri.Host}{port}{prefix}/api/{project}/envelope/"), dsn!);
        return true;
    }

    /// <summary>The <c>X-Sentry-Auth</c> header's value.</summary>
    public string AuthHeader => $"Sentry sentry_version=7, sentry_key={Key}, sentry_client=mainframe-engine/{EngineInfo.Version}";
}

/// <summary>
/// A crash report as a Sentry envelope (newline-delimited JSON): the envelope header, an <c>event</c> item mapped from
/// <c>report.json</c> (exception chain with frames, contexts for the app, OS, device, GPU and runtime, tags, the log as
/// breadcrumbs) and <c>attachment</c> items (<c>log.txt</c>, <c>settings.json</c>, …). Sentry, self-hosted Sentry and
/// compatible servers accept it. Written with <see cref="Utf8JsonWriter"/>: no reflection.
/// </summary>
public static class SentryEnvelope
{
    /// <summary>An attachment item.</summary>
    public readonly record struct Attachment(string FileName, byte[] Data, string ContentType);

    /// <summary>Breadcrumbs taken from the log at most.</summary>
    public const int MaxBreadcrumbs = 100;

    /// <summary>The envelope of <paramref name="report"/> with its <paramref name="log"/> (breadcrumbs) and attachments.</summary>
    public static byte[] Build(CrashReport report, SentryDsn dsn, string? log, IReadOnlyList<Attachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(attachments);
        var output = new ArrayBufferWriter<byte>(16 * 1024);
        using (var header = new Utf8JsonWriter(output))
        {
            header.WriteStartObject();
            header.WriteString("event_id", report.Id);
            header.WriteString("dsn", dsn.Dsn);
            header.WriteString("sent_at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            header.WriteEndObject();
        }

        Newline(output);
        var eventJson = Event(report, log);
        WriteItemHeader(output, "event", eventJson.Length, null, "application/json");
        output.Write(eventJson);
        Newline(output);
        foreach (var attachment in attachments)
        {
            WriteItemHeader(output, "attachment", attachment.Data.Length, attachment.FileName, attachment.ContentType);
            output.Write(attachment.Data);
            Newline(output);
        }

        return output.WrittenSpan.ToArray();
    }

    /// <summary>The Sentry event of <paramref name="report"/> (the <c>event</c> item's payload).</summary>
    public static byte[] Event(CrashReport report, string? log)
    {
        ArgumentNullException.ThrowIfNull(report);
        var output = new ArrayBufferWriter<byte>(8 * 1024);
        using (var w = new Utf8JsonWriter(output))
        {
            w.WriteStartObject();
            w.WriteString("event_id", report.Id);
            w.WriteString("timestamp", report.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            w.WriteString("platform", "csharp");
            w.WriteString("level", report.Kind is CrashKind.Error or CrashKind.UnobservedTask or CrashKind.AudioThread ? "error" : "fatal");
            w.WriteString("logger", "mainframe.crash");
            w.WriteString("release", report.App.Version.Length > 0 ? $"{report.App.Name}@{report.App.Version}" : report.App.Name);
            w.WriteString("environment", report.App.DebugBuild ? "development" : report.App.Demo ? "demo" : "production");
            if (report.Note is { } note)
            {
                w.WriteStartObject("message");
                w.WriteString("formatted", note);
                w.WriteEndObject();
            }

            if (report.Exception.Count > 0)
            {
                w.WriteStartObject("exception");
                w.WriteStartArray("values");
                // Sentry lists the chain innermost-cause first, and each stack oldest frame first.
                for (var i = report.Exception.Count - 1; i >= 0; i--)
                {
                    var exception = report.Exception[i];
                    w.WriteStartObject();
                    w.WriteString("type", exception.Type);
                    w.WriteString("value", exception.Message);
                    if (i == 0)
                    {
                        w.WriteStartObject("mechanism");
                        w.WriteString("type", report.Kind.ToString());
                        w.WriteBoolean("handled", report.Kind is CrashKind.Error or CrashKind.UnobservedTask or CrashKind.AudioThread);
                        w.WriteEndObject();
                    }

                    w.WriteStartObject("stacktrace");
                    w.WriteStartArray("frames");
                    for (var f = exception.Frames.Count - 1; f >= 0; f--)
                    {
                        var frame = exception.Frames[f];
                        w.WriteStartObject();
                        w.WriteString("function", frame.Method);
                        if (frame.Module is { } module)
                            w.WriteString("module", module);
                        if (frame.File is { } file)
                            w.WriteString("filename", file);
                        if (frame.Line is { } line)
                            w.WriteNumber("lineno", line);
                        w.WriteBoolean("in_app", !IsFrameworkFrame(frame));
                        if (frame.IlOffset is { } il)
                            w.WriteString("instruction_addr", "0x" + il.ToString("x", CultureInfo.InvariantCulture));
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteStartObject("tags");
            w.WriteString("kind", report.Kind.ToString());
            w.WriteString("engine", report.App.Engine);
            w.WriteString("rid", report.App.Rid);
            if (report.Session.Scene is { Length: > 0 } scene)
                w.WriteString("scene", scene);
            if (report.Session.Callback is { Length: > 0 } callback)
                w.WriteString("callback", callback);
            if (report.Gpu is { } gpuTag)
                w.WriteString("gpu.vendor", gpuTag.VendorId);
            w.WriteEndObject();

            w.WriteStartObject("contexts");
            w.WriteStartObject("app");
            w.WriteString("app_name", report.App.Name);
            w.WriteString("app_version", report.App.Version);
            w.WriteString("app_build", report.App.EngineCommit ?? report.App.Engine);
            w.WriteEndObject();
            w.WriteStartObject("os");
            w.WriteString("name", report.Os.Description);
            w.WriteString("locale", report.Os.Locale);
            w.WriteEndObject();
            w.WriteStartObject("device");
            w.WriteString("arch", report.Os.Arch);
            w.WriteNumber("processor_count", report.Os.Cpus);
            w.WriteNumber("memory_size", report.Os.MemoryMB * 1024 * 1024);
            w.WriteEndObject();
            w.WriteStartObject("runtime");
            w.WriteString("name", ".NET");
            w.WriteString("version", report.App.Runtime);
            w.WriteEndObject();
            if (report.Gpu is { } gpu)
            {
                w.WriteStartObject("gpu");
                w.WriteString("name", gpu.Name);
                w.WriteString("vendor_id", gpu.VendorId);
                w.WriteString("id", gpu.DeviceId);
                w.WriteString("driver_version", gpu.DriverVersion);
                w.WriteString("api_type", "Vulkan");
                w.WriteString("version", gpu.Api);
                w.WriteEndObject();
            }

            w.WriteStartObject("session");
            w.WriteNumber("uptime_seconds", report.Session.UptimeSeconds);
            w.WriteNumber("rendered_frames", report.Session.RenderedFrames);
            w.WriteNumber("process_frames", report.Session.ProcessFrames);
            if (report.Session.Node is { } node)
                w.WriteString("node", node);
            if (report.Session.NodeType is { } nodeType)
                w.WriteString("node_type", nodeType);
            w.WriteEndObject();
            if (report.GpuLoss is { } loss)
            {
                w.WriteStartObject("gpu_loss");
                w.WriteNumber("frame", loss.Frame);
                w.WriteString("in_flight", string.Join(", ", loss.InFlight));
                if (loss.FaultDescription is { } fault)
                    w.WriteString("fault", fault);
                w.WriteEndObject();
            }

            w.WriteEndObject();

            if (report.Context.Count > 0)
            {
                w.WriteStartObject("extra");
                foreach (var (key, value) in report.Context)
                    w.WriteString(key, value);
                w.WriteEndObject();
            }

            if (log is { Length: > 0 })
            {
                w.WriteStartObject("breadcrumbs");
                w.WriteStartArray("values");
                foreach (var crumb in Breadcrumbs(log))
                {
                    w.WriteStartObject();
                    if (crumb.Timestamp is { } time)
                        w.WriteString("timestamp", time);
                    w.WriteString("level", crumb.Level);
                    if (crumb.Category.Length > 0)
                        w.WriteString("category", crumb.Category);
                    w.WriteString("message", crumb.Message);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        return output.WrittenSpan.ToArray();
    }

    internal readonly record struct Breadcrumb(string? Timestamp, string Level, string Category, string Message);

    /// <summary>
    /// The last <see cref="MaxBreadcrumbs"/> lines of a bundle log (<c>2026-10-05T12:00:00.123Z [WARN] engine Audio: text</c>)
    /// as breadcrumbs; continuation lines (stack traces) join the entry above.
    /// </summary>
    internal static List<Breadcrumb> Breadcrumbs(string log)
    {
        var result = new List<Breadcrumb>();
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            var open = line.IndexOf(" [", StringComparison.Ordinal);
            var close = open >= 0 ? line.IndexOf("] ", open, StringComparison.Ordinal) : -1;
            if (space <= 0 || open != space || close < 0 ||
                !DateTime.TryParse(line.AsSpan(0, space), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            {
                if (result.Count > 0)
                {
                    var last = result[^1];
                    result[^1] = last with { Message = last.Message + "\n" + line };
                }

                continue;
            }

            var level = line.AsSpan(open + 2, close - open - 2) switch
            {
                "Debug" => "debug",
                "INFO" => "info",
                "WARN" => "warning",
                "ERROR" => "error",
                "FATAL" => "fatal",
                _ => "info",
            };
            // "<source> <Category>: message" or "<source> message" (LogEntryFormat.AppendSourceAndCategory).
            var rest = line[(close + 2)..];
            var source = "";
            var sourceEnd = rest.IndexOf(' ', StringComparison.Ordinal);
            if (sourceEnd > 0 && rest.AsSpan(0, sourceEnd) is "engine" or "game")
            {
                source = rest[..sourceEnd];
                rest = rest[(sourceEnd + 1)..];
            }

            var category = source;
            var colon = rest.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0 && colon <= 32 && rest.AsSpan(0, colon).IndexOfAny(' ', '[') < 0)
            {
                category = source.Length > 0 ? $"{source}.{rest[..colon]}" : rest[..colon];
                rest = rest[(colon + 2)..];
            }

            result.Add(new Breadcrumb(line[..space], level, category, rest));
        }

        if (result.Count > MaxBreadcrumbs)
            result.RemoveRange(0, result.Count - MaxBreadcrumbs);
        return result;
    }

    private static bool IsFrameworkFrame(CrashFrame frame) =>
        frame.Method.StartsWith("System.", StringComparison.Ordinal) || frame.Method.StartsWith("Microsoft.", StringComparison.Ordinal) ||
        frame.Method.StartsWith("MainframeEngine.", StringComparison.Ordinal) || frame.Method.StartsWith("Silk.NET.", StringComparison.Ordinal);

    private static void WriteItemHeader(ArrayBufferWriter<byte> output, string type, int length, string? fileName, string contentType)
    {
        using var w = new Utf8JsonWriter(output);
        w.WriteStartObject();
        w.WriteString("type", type);
        w.WriteNumber("length", length);
        if (fileName is not null)
            w.WriteString("filename", fileName);
        w.WriteString("content_type", contentType);
        w.WriteEndObject();
        w.Flush();
        Newline(output);
    }

    private static void Newline(ArrayBufferWriter<byte> output) => output.Write("\n"u8);

    /// <summary>Text as UTF-8 (for attachments).</summary>
    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
