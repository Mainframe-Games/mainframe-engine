using System.Text.Json;
using System.Text.Json.Serialization;

namespace MainframeEngine;

/// <summary>
/// A crash report: the bundle's <c>report.json</c> (format <see cref="CurrentFormat"/>, docs/design/crash-reporting.md).
/// Shared with mobile (M13) and mapped field for field onto a Sentry event (<see cref="SentryEnvelope"/>). Serialized
/// with source-generated JSON (<see cref="ToJson"/>, <see cref="FromJson"/>), so it stays trim- and AOT-safe.
/// </summary>
public sealed class CrashReport
{
    /// <summary>The <c>format</c> this engine writes.</summary>
    public const int CurrentFormat = 1;

    /// <summary>The report file in a bundle.</summary>
    public const string FileName = "report.json";

    /// <summary>The bundle's log (the last entries, formatted like the console).</summary>
    public const string LogFileName = "log.txt";

    /// <summary>The bundle's settings snapshot.</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>The previous log's tail (<see cref="CrashKind.UncleanExit"/> only).</summary>
    public const string LogTailFileName = "game.log.tail";

    /// <summary>The native dump (<see cref="CrashKind.NativeDump"/> only).</summary>
    public const string DumpFileName = "dump.dmp";

    public int Format { get; set; } = CurrentFormat;

    /// <summary>32 lower-case hex digits (a Sentry event id).</summary>
    public string Id { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter<CrashKind>))]
    public CrashKind Kind { get; set; }

    public DateTime Timestamp { get; set; }

    public CrashAppInfo App { get; set; } = new();

    public CrashOsInfo Os { get; set; } = new();

    public CrashGpuInfo? Gpu { get; set; }

    public CrashSessionInfo Session { get; set; } = new();

    /// <summary>The exception chain, outermost first (inner and aggregate exceptions flattened); empty for unclean exits.</summary>
    public List<CrashException> Exception { get; set; } = [];

    /// <summary>The game's keys (<see cref="CrashReporter.SetContext"/>) and the engine's extras (recovery copies, the exit).</summary>
    public Dictionary<string, string> Context { get; set; } = [];

    /// <summary>Mobile facts (M12/M13); null fields on desktop.</summary>
    public CrashDeviceInfo Device { get; set; } = new();

    /// <summary>What the GPU was doing when it was lost (<see cref="CrashKind.DeviceLost"/>), else null.</summary>
    public CrashGpuLoss? GpuLoss { get; set; }

    /// <summary>The bundle's log file name, or null when the log was not written.</summary>
    public string? Log { get; set; }

    /// <summary>A note the game passed to <see cref="CrashReporter.ReportError"/>, or the reason of an unclean exit.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// How the process ended, when known (<see cref="CrashKind.UncleanExit"/> found by the supervisor or the editor): its
    /// exit code; null otherwise.
    /// </summary>
    public int? ExitCode { get; set; }

    /// <summary>The bundle folder this report was read from or written to (not serialized).</summary>
    [JsonIgnore]
    public string? Folder { get; set; }

    /// <summary>
    /// One line for people: "NullReferenceException in PlayerController.OnPhysicsProcess", "The graphics driver stopped
    /// responding", "The game stopped unexpectedly (SIGSEGV, a native crash)".
    /// </summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            switch (Kind)
            {
                case CrashKind.DeviceLost:
                    return "The graphics driver stopped responding";
                case CrashKind.UncleanExit or CrashKind.NativeDump:
                    return ExitCode is { } code && code != 0
                        ? $"The game stopped unexpectedly ({ExitCodes.Describe(code)})"
                        : "The game stopped unexpectedly";
                case CrashKind.Hang:
                    return "The game stopped responding";
            }

            if (Exception.Count == 0)
                return Note ?? Kind.ToString();
            var first = Exception[0];
            var type = ShortTypeName(first.Type);
            if (Session.NodeType is { Length: > 0 } nodeType && Session.Callback is { Length: > 0 } callback)
                return $"{type} in {ShortTypeName(nodeType)}.{callback}";
            foreach (var frame in first.Frames)
            {
                if (frame.Method is { Length: > 0 } method)
                    return $"{type} in {ShortMethodName(method)}";
            }

            return type;
        }
    }

    /// <summary>The report as <c>report.json</c> (indented UTF-8).</summary>
    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, CrashJsonContext.Default.CrashReport);

    /// <summary>Writes the report to <paramref name="writer"/>.</summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        JsonSerializer.Serialize(writer, this, CrashJsonContext.Default.CrashReport);
    }

    /// <summary>Reads a <c>report.json</c>; throws <see cref="JsonException"/> when it is not one.</summary>
    public static CrashReport FromJson(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(json, CrashJsonContext.Default.CrashReport) ?? throw new JsonException("The report is empty.");

    /// <summary>Reads a <c>report.json</c>; throws <see cref="JsonException"/> when it is not one.</summary>
    public static CrashReport FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize(json, CrashJsonContext.Default.CrashReport) ?? throw new JsonException("The report is empty.");
    }

    /// <summary><c>Game.PlayerController</c> → <c>PlayerController</c> (generic arity and nesting kept).</summary>
    internal static string ShortTypeName(string type)
    {
        var dot = type.LastIndexOf('.');
        return dot >= 0 && dot < type.Length - 1 ? type[(dot + 1)..] : type;
    }

    /// <summary><c>Game.PlayerController.OnPhysicsProcess(Single)</c> → <c>PlayerController.OnPhysicsProcess</c>.</summary>
    internal static string ShortMethodName(string method)
    {
        var paren = method.IndexOf('(', StringComparison.Ordinal);
        var name = paren >= 0 ? method[..paren] : method;
        var last = name.LastIndexOf('.');
        if (last <= 0)
            return name;
        var previous = name.LastIndexOf('.', last - 1);
        return previous >= 0 ? name[(previous + 1)..] : name;
    }
}

/// <summary>The <c>app</c> object: what was running.</summary>
public sealed class CrashAppInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Engine { get; set; } = "";
    public string? EngineCommit { get; set; }
    public bool Demo { get; set; }
    public bool DebugBuild { get; set; }
    public string Runtime { get; set; } = "";
    public string Rid { get; set; } = "";
    public bool Headless { get; set; }

    /// <summary>The editor itself crashed (not a game).</summary>
    public bool Editor { get; set; }
}

/// <summary>The <c>os</c> object.</summary>
public sealed class CrashOsInfo
{
    public string Description { get; set; } = "";
    public string Arch { get; set; } = "";
    public int Cpus { get; set; }
    public long MemoryMB { get; set; }
    public string Locale { get; set; } = "";
}

/// <summary>The <c>gpu</c> object (<see cref="GpuInfo"/>; null in headless runs and before the renderer started).</summary>
public sealed class CrashGpuInfo
{
    public string Name { get; set; } = "";
    public string VendorId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Type { get; set; } = "";
    public string Driver { get; set; } = "";
    public string DriverInfo { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public string Api { get; set; } = "";

    internal static CrashGpuInfo From(GpuInfo gpu) => new()
    {
        Name = gpu.Name,
        VendorId = "0x" + gpu.VendorId.ToString("x4", System.Globalization.CultureInfo.InvariantCulture),
        DeviceId = "0x" + gpu.DeviceId.ToString("x4", System.Globalization.CultureInfo.InvariantCulture),
        Type = gpu.Type,
        Driver = gpu.Driver,
        DriverInfo = gpu.DriverInfo,
        DriverVersion = gpu.DriverVersion,
        Api = gpu.ApiVersion,
    };
}

/// <summary>The <c>session</c> object: where the run was when it ended.</summary>
public sealed class CrashSessionInfo
{
    public double UptimeSeconds { get; set; }
    public long RenderedFrames { get; set; }
    public long ProcessFrames { get; set; }
    public string? Scene { get; set; }

    /// <summary>The node whose callback threw (<c>/root/Forest/Player</c>), when the exception left a node callback.</summary>
    public string? Node { get; set; }

    public string? NodeType { get; set; }

    /// <summary><c>OnPhysicsProcess</c>, <c>OnReady</c>, … (<see cref="NodeCallback"/>).</summary>
    public string? Callback { get; set; }

    public List<string> Args { get; set; } = [];

    /// <summary>The process id of the run.</summary>
    public int ProcessId { get; set; }
}

/// <summary>One exception of the chain (a record of one, not an exception: the name follows the report format).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "The report format's name for an exception entry; it is not an Exception.")]
public sealed class CrashException
{
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public List<CrashFrame> Frames { get; set; } = [];
}

/// <summary>One stack frame, innermost first. <see cref="Mvid"/> and <see cref="IlOffset"/> symbolicate a release without PDBs offline.</summary>
public sealed class CrashFrame
{
    public string Method { get; set; } = "";
    public string? File { get; set; }
    public int? Line { get; set; }
    public string? Module { get; set; }
    public string? Mvid { get; set; }
    public int? IlOffset { get; set; }

    /// <summary>The method's metadata token (with <see cref="Mvid"/> and <see cref="IlOffset"/>: a line from the PDB).</summary>
    public int? Token { get; set; }
}

/// <summary>The <c>device</c> object: filled on mobile (M12/M13), null fields on desktop.</summary>
public sealed class CrashDeviceInfo
{
    public string? Tier { get; set; }
    public string? ThermalState { get; set; }
}

/// <summary>The <c>gpuLoss</c> object: the passes of the last submitted frame and the driver's fault report.</summary>
public sealed class CrashGpuLoss
{
    /// <summary>The frame number the breadcrumbs describe (the last one submitted).</summary>
    public long Frame { get; set; }

    /// <summary>Passes that began and did not end (the GPU was in them).</summary>
    public List<string> InFlight { get; set; } = [];

    /// <summary>Passes that finished.</summary>
    public List<string> Completed { get; set; } = [];

    /// <summary>Breadcrumbs were off or unavailable.</summary>
    public bool BreadcrumbsAvailable { get; set; }

    /// <summary><c>VK_EXT_device_fault</c>'s description, null when the driver has none.</summary>
    public string? FaultDescription { get; set; }

    public List<string> FaultAddresses { get; set; } = [];

    public List<string> FaultVendorCodes { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(CrashReport))]
[JsonSerializable(typeof(CrashSessionFile))]
[JsonSerializable(typeof(CrashHandledFile))]
internal sealed partial class CrashJsonContext : JsonSerializerContext;
