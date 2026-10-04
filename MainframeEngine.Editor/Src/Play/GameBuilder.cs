namespace MainframeEngine.Editor;

/// <summary>The outcome of <see cref="IGameBuilder.BuildAsync"/>.</summary>
/// <param name="Succeeded">The build ran and produced no errors.</param>
/// <param name="Diagnostics">Errors, warnings and infos, deduplicated, in output order.</param>
/// <param name="Duration">Wall-clock build time.</param>
/// <param name="Cancelled">Stopped by the cancellation token.</param>
/// <param name="Error">Why the build failed when the diagnostics do not say (tool missing, timeout, exit code); null otherwise.</param>
/// <param name="Output">Every output line.</param>
public sealed record GameBuildResult(
    bool Succeeded,
    IReadOnlyList<BuildDiagnostic> Diagnostics,
    TimeSpan Duration,
    bool Cancelled,
    string? Error,
    IReadOnlyList<string> Output)
{
    /// <summary>A failed result with only an <see cref="Error"/>.</summary>
    public static GameBuildResult Failed(string error, TimeSpan duration = default, bool cancelled = false) =>
        new(false, [], duration, cancelled, error, []);
}

/// <summary>Builds the game's project or solution (the editor's Play button builds before it launches).</summary>
public interface IGameBuilder
{
    /// <summary>
    /// Builds <paramref name="projectOrSolutionPath"/>, reporting each output line to <paramref name="onLine"/> (from a
    /// background thread). Reports failures in the result instead of throwing.
    /// </summary>
    Task<GameBuildResult> BuildAsync(string projectOrSolutionPath, Action<string>? onLine, CancellationToken cancellationToken);
}

/// <summary>
/// Builds with <c>dotnet build &lt;path&gt; -c &lt;cfg&gt; -nologo -v:minimal -clp:NoSummary -p:GenerateFullPaths=true</c>
/// through <see cref="ProcessRunner"/> (killed after <see cref="Timeout"/>).
/// </summary>
public sealed class DotnetGameBuilder : IGameBuilder
{
    /// <summary>A build still running after this long is killed.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public DotnetGameBuilder(string dotnetPath, string configuration = "Debug")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        DotnetPath = dotnetPath;
        Configuration = configuration;
    }

    /// <summary>The <c>dotnet</c> executable.</summary>
    public string DotnetPath { get; }

    /// <summary>The build configuration (<c>Debug</c>).</summary>
    public string Configuration { get; }

    /// <summary>The <c>dotnet</c> arguments for building <paramref name="path"/>.</summary>
    public static IReadOnlyList<string> BuildArguments(string path, string configuration) =>
        ["build", path, "-c", configuration, "-nologo", "-v:minimal", "-clp:NoSummary", "-p:GenerateFullPaths=true"];

    public async Task<GameBuildResult> BuildAsync(string projectOrSolutionPath, Action<string>? onLine, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectOrSolutionPath))
            return GameBuildResult.Failed("No project or solution to build.");
        var path = Path.GetFullPath(projectOrSolutionPath);
        if (!File.Exists(path) && !Directory.Exists(path))
            return GameBuildResult.Failed($"'{path}' does not exist.");

        var workingDirectory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        var result = await ProcessRunner.RunAsync(DotnetPath, BuildArguments(path, Configuration), workingDirectory, onLine,
            timeout: Timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.StartError is { } startError)
            return GameBuildResult.Failed($"Could not run dotnet ('{DotnetPath}'): {startError}", result.Duration);

        var diagnostics = MsBuildOutputParser.ParseAll(result.Output);
        string? error = null;
        if (result.TimedOut)
            error = $"The build timed out after {Timeout.TotalMinutes:0} minutes.";
        else if (result.Cancelled)
            error = "The build was cancelled.";
        else if (result.ExitCode != 0 && !HasError(diagnostics))
            error = $"dotnet build exited with code {result.ExitCode}.";
        var succeeded = result.Succeeded && !HasError(diagnostics);
        return new GameBuildResult(succeeded, diagnostics, result.Duration, result.Cancelled, error, result.Output);
    }

    private static bool HasError(IReadOnlyList<BuildDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
            if (diagnostic.Severity == BuildDiagnosticSeverity.Error)
                return true;
        return false;
    }
}
