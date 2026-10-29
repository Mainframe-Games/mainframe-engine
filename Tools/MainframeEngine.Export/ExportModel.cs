namespace MainframeEngine.Export;

/// <summary>One export: a preset of the project in <paramref name="ProjectDirectory"/>, optionally to another folder.</summary>
/// <param name="ProjectDirectory">The folder holding <c>project.mfproj</c>.</param>
/// <param name="Preset">The preset to run (it need not be saved in the project: the CLI's legacy form makes one).</param>
/// <param name="OutputOverride">An output folder that replaces the preset's <c>output</c> (the CLI's <c>--output</c>).</param>
/// <param name="AppName">A display name for the app instead of the project's (the legacy <c>--name</c>).</param>
public sealed record ExportRequest(string ProjectDirectory, ExportPreset Preset, string? OutputOverride = null, string? AppName = null);

/// <summary>The steps of an export, in order.</summary>
public enum ExportPhase
{
    Validate,
    Publish,
    Stage,
    Icon,
    Bundle,
    Sign,
    Notarize,
    Archive,
}

/// <summary>Progress: the phase, a 0–1 fraction when known (null: indeterminate), and a short status line.</summary>
public readonly record struct ExportProgress(int PresetIndex, int PresetCount, ExportPhase Phase, float? Fraction, string Status);

/// <summary>How serious an <see cref="ExportIssue"/> is: errors stop the preset, warnings do not.</summary>
public enum ExportIssueSeverity
{
    Warning,
    Error,
}

/// <summary>A validation finding (docs/design/game-export.md#validation): a stable code, the message and how to fix it.</summary>
public sealed record ExportIssue(ExportIssueSeverity Severity, string Code, string Message, string? Fix = null)
{
    public bool IsError => Severity == ExportIssueSeverity.Error;

    public override string ToString() => Fix is null ? $"{Code}: {Message}" : $"{Code}: {Message} {Fix}";
}

/// <summary>What an export produced. Export failures are results, never exceptions.</summary>
/// <param name="Preset">The preset's name.</param>
/// <param name="Succeeded">The artifact was written.</param>
/// <param name="Cancelled">Stopped by the cancellation token (the previous artifact is untouched).</param>
/// <param name="ArtifactPath">The archive, or the folder for <c>archive: false</c>; null when nothing was written.</param>
/// <param name="SymbolsPath">The symbols zip for <c>symbols: separate</c>, else null.</param>
/// <param name="Duration">Wall-clock time.</param>
/// <param name="Issues">The validation findings (errors mean nothing ran).</param>
/// <param name="Diagnostics">The publish's compiler and MSBuild diagnostics.</param>
/// <param name="Error">Why it failed when neither the issues nor the diagnostics say; null on success.</param>
public sealed record ExportResult(
    string Preset,
    bool Succeeded,
    bool Cancelled,
    string? ArtifactPath,
    string? SymbolsPath,
    TimeSpan Duration,
    IReadOnlyList<ExportIssue> Issues,
    IReadOnlyList<BuildDiagnostic> Diagnostics,
    string? Error)
{
    /// <summary>Validation failed, so nothing ran ("skipped (invalid)").</summary>
    public bool Invalid => !Succeeded && !Cancelled && Issues.Any(i => i.IsError);

    /// <summary>One line for logs: the artifact, or why there is none.</summary>
    public string Summary => Succeeded ? $"exported {ArtifactPath}"
        : Cancelled ? "cancelled"
        : Invalid ? $"invalid: {string.Join("; ", Issues.Where(i => i.IsError).Select(i => $"{i.Code}: {i.Message}"))}"
        : $"failed: {Error ?? string.Join("; ", Diagnostics.Where(d => d.Severity == BuildDiagnosticSeverity.Error).Take(3).Select(d => d.Message))}";

    /// <summary>The counts of a run of several presets: <c>3 exported, 1 failed, 1 skipped (invalid)</c>.</summary>
    public static string Tally(IReadOnlyList<ExportResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var exported = results.Count(r => r.Succeeded);
        var invalid = results.Count(r => r.Invalid);
        var cancelled = results.Count(r => r.Cancelled);
        var failed = results.Count - exported - invalid - cancelled;
        var parts = new List<string> { $"{exported} exported" };
        if (failed > 0)
            parts.Add($"{failed} failed");
        if (invalid > 0)
            parts.Add($"{invalid} skipped (invalid)");
        if (cancelled > 0)
            parts.Add($"{cancelled} cancelled");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// What an export needs from the machine: the <c>dotnet</c> host and its SDK check, the host OS and RID, the environment
/// (<c>MF_SIGN_IDENTITY</c>, <c>MF_NOTARY_PROFILE</c>) and the external tools (macOS <c>codesign</c>, <c>xcrun</c>,
/// <c>ditto</c>). Tests make one with a fake host or tool runner.
/// </summary>
public sealed class ExportEnvironment
{
    /// <summary>The <c>dotnet</c> executable (null: none found).</summary>
    public string? DotnetPath { get; init; }

    /// <summary>The SDK check (null: not checked, treated as supported when <see cref="DotnetPath"/> is set).</summary>
    public DotnetSdkInfo? Sdk { get; init; }

    public ExportHost Host { get; init; } = ExportTargets.CurrentHost;

    public string HostRid { get; init; } = ExportTargets.CurrentRid;

    /// <summary>Reads an environment variable.</summary>
    public Func<string, string?> GetVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>Runs an external tool (<c>dotnet</c>, <c>codesign</c>, <c>xcrun</c>, <c>ditto</c>): <see cref="ProcessRunner.RunAsync"/> by default.</summary>
    public ToolRunner RunTool { get; init; } = static (file, args, dir, onLine, env, ct) =>
        ProcessRunner.RunAsync(file, args, dir, onLine, env, timeout: null, cancellationToken: ct);

    /// <summary>The machine's environment: finds <c>dotnet</c> and checks its SDK (<see cref="DotnetSdk.DetectAsync"/>).</summary>
    public static async Task<ExportEnvironment> DetectAsync(CancellationToken cancellationToken = default)
    {
        var sdk = await DotnetSdk.DetectAsync(ct: cancellationToken).ConfigureAwait(false);
        return new ExportEnvironment { DotnetPath = sdk.DotnetPath, Sdk = sdk };
    }
}

/// <summary>Runs a tool process and reports its result (never throws for process failures).</summary>
public delegate Task<ProcessResult> ToolRunner(
    string fileName,
    IReadOnlyList<string> arguments,
    string? workingDirectory,
    Action<string>? onLine,
    IReadOnlyDictionary<string, string?>? environment,
    CancellationToken cancellationToken);
