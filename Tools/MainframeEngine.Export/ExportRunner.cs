using System.Diagnostics;
using System.IO.Compression;

namespace MainframeEngine.Export;

/// <summary>What the editor's export service needs from a runner (the real <see cref="ExportRunner"/>, or a fake in tests).</summary>
public interface IExportRunner
{
    /// <summary>Checks a preset without running anything slow. Thread-safe.</summary>
    IReadOnlyList<ExportIssue> Validate(string projectDirectory, ProjectSettings project, ExportPreset preset, AssetDatabase? database = null);

    /// <summary>Runs presets one after another; a failed or invalid preset does not stop the rest.</summary>
    Task<IReadOnlyList<ExportResult>> RunAllAsync(string projectDirectory, IReadOnlyList<ExportPreset> presets,
        IProgress<ExportProgress>? progress, Action<string>? onLine, CancellationToken cancellationToken);
}

/// <summary>
/// Runs export presets (docs/design/game-export.md): validate, <c>dotnet publish</c> for the preset's RID into a stage,
/// stage it (rename the apphost, prune other platforms' natives, filter <c>Assets/</c>, write the asset index and the
/// exported <c>project.mfproj</c>), write the icon, bundle and sign on macOS, then archive into the output folder. The editor,
/// <c>mf-export</c>, <c>just package-game</c> and CI all use it, so they produce the same output.
/// </summary>
/// <remarks>
/// Thread-safe for one run at a time per project: the stage and the artifacts cache live under the project's
/// <c>.mainframe/export</c>. Never touches RmlUi, a scene tree or <see cref="AssetDatabase.Current"/>; it builds its own
/// <see cref="AssetDatabase"/> over the stage. Progress and output lines arrive on background threads.
/// </remarks>
public sealed class ExportRunner(ExportEnvironment environment) : IExportRunner
{
    public ExportEnvironment Environment { get; } = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>Checks a preset without running anything slow (no build). Thread-safe.</summary>
    public IReadOnlyList<ExportIssue> Validate(string projectDirectory, ProjectSettings project, ExportPreset preset, AssetDatabase? database = null) =>
        ExportValidator.Validate(projectDirectory, project, preset, Environment, database);

    /// <summary>Validates, then exports. Never throws for export failures: they are in the result.</summary>
    public Task<ExportResult> RunAsync(ExportRequest request, IProgress<ExportProgress>? progress, Action<string>? onLine,
        CancellationToken cancellationToken) => RunAsync(request, 0, 1, progress, onLine, cancellationToken);

    /// <summary>Runs presets one after another; a failed or invalid preset does not stop the rest (cancelling does).</summary>
    public async Task<IReadOnlyList<ExportResult>> RunAllAsync(string projectDirectory, IReadOnlyList<ExportPreset> presets,
        IProgress<ExportProgress>? progress, Action<string>? onLine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presets);
        var results = new List<ExportResult>(presets.Count);
        for (var i = 0; i < presets.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(Cancelled(presets[i].Name, TimeSpan.Zero, []));
                continue;
            }

            results.Add(await RunAsync(new ExportRequest(projectDirectory, presets[i]), i, presets.Count, progress, onLine, cancellationToken)
                .ConfigureAwait(false));
        }

        return results;
    }

    private async Task<ExportResult> RunAsync(ExportRequest request, int index, int count, IProgress<ExportProgress>? progress,
        Action<string>? onLine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var preset = request.Preset;
        void Report(ExportPhase phase, float? fraction, string status) =>
            progress?.Report(new ExportProgress(index, count, phase, fraction, status));
        void Line(string line) => onLine?.Invoke(line);

        Report(ExportPhase.Validate, null, "Validating");
        ProjectSettings project;
        try
        {
            project = ProjectSettings.Load(request.ProjectDirectory);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Failed(preset.Name, stopwatch.Elapsed, [], [], $"Cannot read the project: {e.Message}");
        }

        var issues = ExportValidator.Validate(request.ProjectDirectory, project, preset, Environment, outputOverride: request.OutputOverride,
            appName: request.AppName);
        foreach (var issue in issues)
            Line($"{(issue.IsError ? "error" : "warning")} {issue.Code}: {issue.Message}{(issue.Fix is null ? "" : " " + issue.Fix)}");
        if (ExportValidator.HasErrors(issues))
            return new ExportResult(preset.Name, false, false, null, null, stopwatch.Elapsed, issues, [], null);

        var plan = ExportPlan.Create(request.ProjectDirectory, project, preset, Environment, request.OutputOverride, request.AppName);
        var diagnostics = (IReadOnlyList<BuildDiagnostic>)[];
        var icoDirectory = Path.GetDirectoryName(plan.IcoPath)!;
        try
        {
            Archiver.TryDelete(plan.StageDirectory);
            Directory.CreateDirectory(plan.StageDirectory);
            Directory.CreateDirectory(plan.OutputDirectory);

            // Icon (Windows): the .ico is a build input, written before the publish.
            string? ico = null;
            var warnings = new List<ExportIssue>(issues);
            if (plan.Platform == ExportPlatform.Windows && !plan.ProjectHasWindowsIcon && plan.IconPng is { } png && File.Exists(png))
            {
                Report(ExportPhase.Icon, null, "Writing the icon");
                try
                {
                    IconWriter.WriteIco(png, plan.IcoPath);
                    ico = plan.IcoPath;
                }
                catch (InvalidDataException e)
                {
                    warnings.Add(new ExportIssue(ExportIssueSeverity.Warning, "icon", $"No exe icon: {e.Message}"));
                    Line($"warning icon: {e.Message}");
                }
            }

            // Publish.
            Report(ExportPhase.Publish, null, $"Publishing for {plan.Rid}");
            Line($"dotnet {string.Join(' ', plan.PublishArguments(ico).Select(Quote))}");
            var publish = await Environment.RunTool(Environment.DotnetPath!, plan.PublishArguments(ico), plan.ProjectDirectory, Line,
                null, cancellationToken).ConfigureAwait(false);
            diagnostics = MsBuildOutputParser.ParseAll(publish.Output);
            cancellationToken.ThrowIfCancellationRequested();
            if (publish.StartError is { } startError)
                return Failed(preset.Name, stopwatch.Elapsed, warnings, diagnostics, $"Could not run dotnet: {startError}");
            if (!publish.Succeeded || diagnostics.Any(d => d.Severity == BuildDiagnosticSeverity.Error))
                return Failed(preset.Name, stopwatch.Elapsed, warnings, diagnostics,
                    publish.Cancelled ? "The publish was cancelled." : $"dotnet publish failed (exit code {publish.ExitCode}).");

            // Stage.
            Report(ExportPhase.Stage, 0f, "Staging");
            var stageError = Stage(plan, Line);
            if (stageError is not null)
                return Failed(preset.Name, stopwatch.Elapsed, warnings, diagnostics, stageError);
            Report(ExportPhase.Stage, 0.5f, "Staging");
            cancellationToken.ThrowIfCancellationRequested();

            string? symbols = null;
            if (plan.Desktop.Symbols == ExportSymbols.Separate)
            {
                var symbolsFolder = Path.Combine(plan.StageDirectory, "symbols");
                var collected = SymbolCollector.Collect(plan.PublishDirectory, symbolsFolder, move: true);
                Line($"Collected {collected} symbol file(s) into {Path.GetFileName(plan.SymbolsArchivePath)}");
                var partial = plan.SymbolsArchivePath + Archiver.PartialSuffix;
                Archiver.TryDelete(partial);
                ZipFile.CreateFromDirectory(symbolsFolder, partial, CompressionLevel.Optimal, includeBaseDirectory: false);
                File.Move(partial, plan.SymbolsArchivePath, overwrite: true);
                symbols = plan.SymbolsArchivePath;
            }

            Report(ExportPhase.Stage, 1f, "Staged");

            // Bundle, sign, archive.
            var artifactError = plan.Platform == ExportPlatform.MacOS
                ? await PackageMacOsAsync(plan, Report, Line, cancellationToken).ConfigureAwait(false)
                : PackageFolder(plan, Report, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (artifactError is not null)
                return Failed(preset.Name, stopwatch.Elapsed, warnings, diagnostics, artifactError);
            Line($"Exported {plan.ArtifactPath}");
            return new ExportResult(preset.Name, true, false, plan.ArtifactPath, symbols, stopwatch.Elapsed, warnings, diagnostics, null);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(preset.Name, stopwatch.Elapsed, issues, diagnostics);
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) // failures are results, never exceptions
        {
            return Failed(preset.Name, stopwatch.Elapsed, issues, diagnostics, e.Message);
        }
        finally
        {
            Archiver.TryDelete(plan.StageDirectory);
            Archiver.TryDelete(icoDirectory);
            Archiver.TryDelete(plan.ArtifactPath + Archiver.PartialSuffix);
            Archiver.TryDelete(plan.SymbolsArchivePath + Archiver.PartialSuffix);
        }
    }

    // Rename the apphost, prune natives, check Steam, filter content, write the project file and the asset index.
    private static string? Stage(ExportPlan plan, Action<string> line)
    {
        var publish = plan.PublishDirectory;
        var apphost = Path.Combine(publish, plan.PublishedApphost);
        var executable = Path.Combine(publish, plan.ExecutableFile);
        if (!File.Exists(apphost))
            return $"The publish has no executable {plan.PublishedApphost}.";
        if (!string.Equals(apphost, executable, StringComparison.Ordinal))
            File.Move(apphost, executable, overwrite: true);
        if (plan.Platform != ExportPlatform.Windows && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(executable, Archiver.ExecutableMode);

        var pruned = NativesPruner.Prune(publish, plan.Rid);
        if (pruned.Count > 0)
            line($"Removed other platforms' natives: {string.Join(", ", pruned)}");

        if (plan.Desktop.Steam)
        {
            var variant = NativesPruner.SteamworksVariant(plan.Rid);
            var steamApi = NativesPruner.SteamApiNative(plan.Rid);
            string[] required = [steamApi, $"steamworks/{variant}/MainframeEngine.Steamworks.dll"];
            var missing = required.Where(f => !File.Exists(Path.Combine(publish, f))).ToArray();
            if (missing.Length > 0)
                return $"Steam is on, but the publish lacks {string.Join(" and ", missing)}: Steam would report NativeLibraryMissing.";
        }

        var removed = ContentFilter.Apply(publish, plan.ProjectDirectory, plan.Preset.Include, plan.Preset.Exclude);
        if (removed.Count > 0)
            line($"Content filter: {removed.Count} file(s) left out");

        ExportedProjectFile.Transform(plan.Settings, plan.Preset, plan.Version).Save(Path.Combine(publish, ProjectSettings.FileName));
        if (Directory.Exists(Path.Combine(publish, ContentPaths.FolderName)))
        {
            var database = new AssetDatabase(publish);
            database.Scan();
            database.WriteIndex();
        }

        if (File.Exists(Path.Combine(publish, "steam_appid.txt")))
            return "steam_appid.txt is in the publish output: a player build must never carry one (remove it from the project's copied files).";
        return null;
    }

    // Windows and Linux: a zip or tar.gz of <base>/, or the folder itself.
    private static string? PackageFolder(ExportPlan plan, Action<ExportPhase, float?, string> report, CancellationToken cancellationToken)
    {
        report(ExportPhase.Archive, 0f, "Archiving");
        if (!plan.Preset.Archive)
        {
            Archiver.MoveFolder(plan.PublishDirectory, plan.ArtifactPath);
            return null;
        }

        void Progress(float f) => report(ExportPhase.Archive, f, "Archiving");
        if (plan.Platform == ExportPlatform.Linux)
            Archiver.TarGz(plan.PublishDirectory, plan.BaseName, plan.ArtifactPath, plan.Executables(), Progress, cancellationToken);
        else
            Archiver.Zip(plan.PublishDirectory, plan.BaseName, plan.ArtifactPath, plan.Executables(), Progress, cancellationToken);
        return null;
    }

    // macOS: <Name>.app (icon, Info.plist), signed inside out, zipped with ditto; notarized and stapled when asked.
    private async Task<string?> PackageMacOsAsync(ExportPlan plan, Action<ExportPhase, float?, string> report, Action<string> line,
        CancellationToken cancellationToken)
    {
        report(ExportPhase.Bundle, null, "Bundling");
        var apphost = Path.Combine(plan.PublishDirectory, plan.ExecutableFile);
        if (!AppBundleWriter.StartsFromBundle(apphost, plan.AssemblyName))
            return $"{plan.ExecutableFile} does not start ../MonoBundle/{plan.AssemblyName}.dll: the desktop project does not import " +
                   "build/macos/BundleAppHost.targets (MainframeGame.props, or the MainframeEngine package's buildTransitive).";

        string? icns = plan.ProjectMacIcns is { } own && File.Exists(own) ? own : null;
        if (icns is null && plan.IconPng is { } png && File.Exists(png))
        {
            report(ExportPhase.Icon, null, "Writing the icon");
            try
            {
                icns = Path.Combine(plan.StageDirectory, "icon.icns");
                IconWriter.WriteIcns(png, icns);
            }
            catch (InvalidDataException e)
            {
                icns = null;
                line($"warning icon: {e.Message}");
            }
        }

        var holder = Path.Combine(plan.StageDirectory, "app");
        var app = Path.Combine(holder, plan.AppName + ".app");
        AppBundleWriter.Write(plan.PublishDirectory, app,
            new AppBundleInfo(plan.AppName, plan.Exe, plan.BundleId, plan.Version, plan.Copyright, icns is not null), icns);

        var signer = new MacSigner(Environment.RunTool, line);
        report(ExportPhase.Sign, null, plan.SignIdentity == "-" ? "Signing (ad hoc)" : "Signing");
        if (await signer.SignAsync(app, plan.Exe, plan.SignIdentity, cancellationToken).ConfigureAwait(false) is { } signError)
            return signError;
        cancellationToken.ThrowIfCancellationRequested();

        var zip = plan.Preset.Archive ? plan.ArtifactPath : Path.Combine(plan.StageDirectory, "notarize.zip");
        if (plan.Preset.Archive || plan.NotaryProfile.Length > 0)
        {
            report(ExportPhase.Archive, null, "Archiving");
            if (await signer.ZipAsync(app, zip, cancellationToken).ConfigureAwait(false) is { } zipError)
                return zipError;
        }

        if (plan.NotaryProfile.Length > 0)
        {
            report(ExportPhase.Notarize, null, "Notarizing (waiting for Apple)");
            if (await signer.NotarizeAsync(app, zip, plan.NotaryProfile, cancellationToken).ConfigureAwait(false) is { } notaryError)
                return notaryError;
            if (plan.Preset.Archive)
            {
                report(ExportPhase.Archive, null, "Archiving the stapled app");
                if (await signer.ZipAsync(app, zip, cancellationToken).ConfigureAwait(false) is { } zipError)
                    return zipError;
            }
        }

        if (!plan.Preset.Archive)
        {
            Archiver.TryDelete(zip);
            Archiver.MoveFolder(holder, plan.ArtifactPath);
        }

        return null;
    }

    private static ExportResult Failed(string preset, TimeSpan duration, IReadOnlyList<ExportIssue> issues, IReadOnlyList<BuildDiagnostic> diagnostics,
        string error) => new(preset, false, false, null, null, duration, issues, diagnostics, error);

    private static ExportResult Cancelled(string preset, TimeSpan duration, IReadOnlyList<ExportIssue> issues, IReadOnlyList<BuildDiagnostic>? diagnostics = null) =>
        new(preset, false, true, null, null, duration, issues, diagnostics ?? [], "The export was cancelled.");

    private static string Quote(string argument) => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;
}
