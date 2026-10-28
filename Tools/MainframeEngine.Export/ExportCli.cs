using System.Globalization;

namespace MainframeEngine.Export;

/// <summary>What an <c>mf-export</c> (or editor <c>--export-*</c>) command line asks for.</summary>
public enum ExportCommandKind
{
    /// <summary>One preset by name.</summary>
    Preset,

    /// <summary>Every preset in order.</summary>
    All,

    /// <summary>Print the presets and their validation.</summary>
    List,

    /// <summary>The old <c>package-game.sh</c> form: a desktop project, a RID and an output folder, mapped onto an unsaved preset.</summary>
    Legacy,

    /// <summary>Print the usage.</summary>
    Help,
}

/// <summary>A parsed command line.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="ProjectDirectory">The project root (for the legacy form: the folder above the desktop project's).</param>
/// <param name="PresetName">The preset (<see cref="ExportCommandKind.Preset"/>).</param>
/// <param name="Output">An output folder overriding the preset's.</param>
/// <param name="Configuration">A configuration overriding the presets' (the editor's <c>--export-debug</c>).</param>
/// <param name="LegacyPreset">The preset the legacy form describes.</param>
/// <param name="AppName">The legacy <c>--name</c>: the app's display name instead of the project's.</param>
public sealed record ExportCommand(
    ExportCommandKind Kind,
    string? ProjectDirectory = null,
    string? PresetName = null,
    string? Output = null,
    string? Configuration = null,
    ExportPreset? LegacyPreset = null,
    string? AppName = null);

/// <summary>
/// The command line of <c>mf-export</c> (Tools/MainframeEngine.Export.Cli; <c>just package-game</c>; CI) and of the
/// editor's headless export flags (<c>MainframeEngine.Editor --project &lt;dir&gt; --export-release "&lt;preset&gt;"</c>, Godot's
/// names). Exit codes: 0 success, 1 a preset failed, 2 usage or validation error. Errors go to stderr in MSBuild's
/// canonical format (<c>file : error code: text</c>), so CI annotates them.
/// </summary>
public static class ExportCli
{
    public const int Success = 0;
    public const int ExportFailed = 1;
    public const int UsageError = 2;

    public const string Usage = """
        mf-export --project <dir> --preset "<name>" [--output <dir>]   export one preset of project.mfproj
        mf-export --project <dir> --all                                  export every preset in order
        mf-export --project <dir> --list                                 list the presets and their validation
        mf-export <Desktop.csproj> <rid> <out-dir> [--name "Display name"] [--exe Name] [--version v] [--bundle-id id]
                  [--copyright text] [--icon icon.png] [--sign "Developer ID Application: …"] [--notarize profile]
                  [--demo] [--steam] [--symbols ship|separate] [--configuration Release|Debug] [--folder]
                                                                         the old package-game.sh arguments
        Editor: MainframeEngine.Editor --project <dir> (--export-release "<preset>" [<out>] | --export-debug "<preset>" [<out>] | --export-all)
        Exit codes: 0 exported, 1 a preset failed, 2 usage or validation error.
        """;

    /// <summary>Whether <paramref name="args"/> are the editor's headless export flags.</summary>
    public static bool IsEditorExport(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(a => a is "--export-release" or "--export-debug" or "--export-all");
    }

    /// <summary>Parses the editor's flags (<c>--project</c> plus one of <c>--export-release/debug/all</c>).</summary>
    public static ExportCommand ParseEditor(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? project = null, preset = null, output = null, configuration = null;
        var all = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--project":
                    project = Value(args, ref i);
                    break;
                case "--export-release" or "--export-debug":
                    configuration = args[i] == "--export-debug" ? "Debug" : "Release";
                    preset = Value(args, ref i);
                    if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        output = args[++i];
                    break;
                case "--export-all":
                    all = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown export option '{args[i]}'.");
            }
        }

        if (project is null)
            throw new ArgumentException("--export-* needs --project <dir>.");
        if (all == (preset is not null))
            throw new ArgumentException("Pass exactly one of --export-release \"<preset>\", --export-debug \"<preset>\" or --export-all.");
        return all
            ? new ExportCommand(ExportCommandKind.All, project)
            : new ExportCommand(ExportCommandKind.Preset, project, preset, output, configuration);
    }

    /// <summary>Parses an <c>mf-export</c> command line; throws <see cref="ArgumentException"/> for usage errors.</summary>
    public static ExportCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || args.Any(a => a is "-h" or "--help"))
            return new ExportCommand(ExportCommandKind.Help);
        if (!args[0].StartsWith("--", StringComparison.Ordinal))
            return ParseLegacy(args);

        string? project = null, preset = null, output = null;
        bool all = false, list = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--project":
                    project = Value(args, ref i);
                    break;
                case "--preset":
                    preset = Value(args, ref i);
                    break;
                case "--output":
                    output = Value(args, ref i);
                    break;
                case "--all":
                    all = true;
                    break;
                case "--list":
                    list = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }

        project ??= Directory.GetCurrentDirectory();
        var modes = (preset is null ? 0 : 1) + (all ? 1 : 0) + (list ? 1 : 0);
        if (modes != 1)
            throw new ArgumentException("Pass exactly one of --preset \"<name>\", --all or --list.");
        if (output is not null && preset is null)
            throw new ArgumentException("--output goes with --preset.");
        return list ? new ExportCommand(ExportCommandKind.List, project)
            : all ? new ExportCommand(ExportCommandKind.All, project)
            : new ExportCommand(ExportCommandKind.Preset, project, preset, output);
    }

    private static ExportCommand ParseLegacy(IReadOnlyList<string> args)
    {
        if (args.Count < 3)
            throw new ArgumentException("The package-game form needs <Desktop.csproj> <rid> <out-dir>.");
        var csproj = Path.GetFullPath(args[0]);
        var rid = args[1];
        var output = Path.GetFullPath(args[2]);
        var platform = ExportTargets.PlatformOf(rid) ?? throw new ArgumentException($"'{rid}' is not a desktop RID (win-x64, linux-x64, osx-arm64, osx-x64).");
        var preset = ExportPreset.Create($"{rid} (command line)", platform);
        var desktop = preset.Desktop;
        desktop.Rid = rid;
        var mac = desktop.MacOs ?? new MacOsExportOptions();
        string? name = null;
        for (var i = 3; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--name":
                    name = Value(args, ref i);
                    break;
                case "--exe":
                    desktop.Exe = Value(args, ref i);
                    break;
                case "--version":
                    preset.Version = Value(args, ref i);
                    break;
                case "--bundle-id":
                    mac.BundleId = Value(args, ref i);
                    break;
                case "--copyright":
                    mac.Copyright = Value(args, ref i);
                    break;
                case "--icon":
                    preset.Icon = Path.GetFullPath(Value(args, ref i));
                    break;
                case "--sign":
                    mac.Sign = Value(args, ref i);
                    break;
                case "--notarize":
                    mac.NotaryProfile = Value(args, ref i);
                    break;
                case "--demo":
                    preset.Demo = true;
                    break;
                case "--steam":
                    desktop.Steam = true;
                    break;
                case "--folder":
                    preset.Archive = false;
                    break;
                case "--configuration":
                    preset.Configuration = Value(args, ref i);
                    break;
                case "--symbols":
                    desktop.Symbols = Value(args, ref i) switch
                    {
                        "ship" => ExportSymbols.Ship,
                        "separate" => ExportSymbols.Separate,
                        var other => throw new ArgumentException($"--symbols must be ship or separate (got '{other}')."),
                    };
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }

        if (platform == ExportPlatform.MacOS)
            desktop.MacOs = mac;
        var project = Path.GetDirectoryName(Path.GetDirectoryName(csproj)) ?? throw new ArgumentException($"'{csproj}' has no project folder above it.");
        return new ExportCommand(ExportCommandKind.Legacy, project, preset.Name, output, LegacyPreset: preset, AppName: name);
    }

    private static string Value(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count)
            throw new ArgumentException($"{args[i]} needs a value.");
        return args[++i];
    }

    /// <summary>Runs <c>mf-export</c>.</summary>
    public static Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        ExportCommand command;
        try
        {
            command = Parse(args);
        }
        catch (ArgumentException e)
        {
            stderr.WriteLine($"mf-export : error usage: {e.Message}");
            stderr.WriteLine(Usage);
            return Task.FromResult(UsageError);
        }

        return RunAsync(command, stdout, stderr, environment: null, cancellationToken);
    }

    /// <summary>Runs the editor's <c>--export-*</c> flags (no window, SDL or engine).</summary>
    public static Task<int> RunEditorAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        ExportCommand command;
        try
        {
            command = ParseEditor(args);
        }
        catch (ArgumentException e)
        {
            stderr.WriteLine($"MainframeEngine.Editor : error usage: {e.Message}");
            stderr.WriteLine(Usage);
            return Task.FromResult(UsageError);
        }

        return RunAsync(command, stdout, stderr, environment: null, cancellationToken);
    }

    /// <summary>Runs a parsed command; <paramref name="environment"/> null detects the machine's.</summary>
    public static async Task<int> RunAsync(ExportCommand command, TextWriter stdout, TextWriter stderr, ExportEnvironment? environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (command.Kind == ExportCommandKind.Help)
        {
            stdout.WriteLine(Usage);
            return Success;
        }

        var root = Path.GetFullPath(command.ProjectDirectory ?? Directory.GetCurrentDirectory());
        var projectFile = Path.Combine(root, ProjectSettings.FileName);
        ProjectSettings project;
        try
        {
            project = ProjectSettings.Load(root);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"{projectFile} : error project: {e.Message}");
            return UsageError;
        }

        environment ??= await ExportEnvironment.DetectAsync(cancellationToken).ConfigureAwait(false);
        var runner = new ExportRunner(environment);
        List<ExportPreset> presets;
        switch (command.Kind)
        {
            case ExportCommandKind.List:
                return List(project, root, runner, stdout);
            case ExportCommandKind.Legacy:
                presets = [command.LegacyPreset!];
                break;
            case ExportCommandKind.All:
                presets = [.. project.ExportPresets];
                if (presets.Count == 0)
                {
                    stderr.WriteLine($"{projectFile} : error no-presets: The project has no export presets (add one in the editor's File › Export).");
                    return UsageError;
                }

                break;
            default:
                if (project.ExportPresets.FirstOrDefault(p => p.Name == command.PresetName) is not { } found)
                {
                    stderr.WriteLine($"{projectFile} : error no-preset: No preset is named '{command.PresetName}'. Presets: " +
                                     (project.ExportPresets.Count == 0 ? "(none)" : string.Join(", ", project.ExportPresets.Select(p => $"\"{p.Name}\""))));
                    return UsageError;
                }

                presets = [found];
                break;
        }

        if (command.Configuration is { } configuration)
            presets = [.. presets.Select(p => WithConfiguration(p, configuration))];

        var results = new List<ExportResult>();
        for (var i = 0; i < presets.Count; i++)
        {
            stdout.WriteLine($"[{i + 1}/{presets.Count}] {presets[i].Name}");
            var request = new ExportRequest(root, presets[i], command.Output, command.AppName);
            var result = await runner.RunAsync(request, null, stdout.WriteLine, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            Report(result, projectFile, stdout, stderr);
            if (result.Cancelled)
                break;
        }

        if (presets.Count > 1)
            stdout.WriteLine(ExportResult.Tally(results));
        if (results.All(r => r.Succeeded))
            return Success;
        return results.Count == 1 && results[0].Invalid ? UsageError : ExportFailed;
    }

    private static ExportPreset WithConfiguration(ExportPreset preset, string configuration)
    {
        var copy = preset.Clone();
        copy.Configuration = configuration;
        return copy;
    }

    private static int List(ProjectSettings project, string root, ExportRunner runner, TextWriter stdout)
    {
        if (project.ExportPresets.Count == 0)
        {
            stdout.WriteLine("No export presets.");
            return Success;
        }

        var database = new AssetDatabase(root);
        if (Directory.Exists(Path.Combine(root, ContentPaths.FolderName)))
            database.Scan();
        var anyError = false;
        foreach (var preset in project.ExportPresets)
        {
            var issues = runner.Validate(root, project, preset, database);
            var errors = issues.Count(i => i.IsError);
            anyError |= errors > 0;
            var plan = ExportPlan.Create(root, project, preset, runner.Environment);
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{(errors > 0 ? "✕" : issues.Count > 0 ? "⚠" : "✓")} \"{preset.Name}\"  {ExportPreset.PlatformKey(preset.Platform)} {plan.Rid}  → {Path.GetRelativePath(root, plan.ArtifactPath)}"));
            foreach (var issue in issues)
                stdout.WriteLine($"    {(issue.IsError ? "error" : "warning")} {issue.Code}: {issue.Message}{(issue.Fix is null ? "" : " " + issue.Fix)}");
        }

        return anyError ? UsageError : Success;
    }

    private static void Report(ExportResult result, string projectFile, TextWriter stdout, TextWriter stderr)
    {
        foreach (var issue in result.Issues.Where(i => i.IsError))
            stderr.WriteLine($"{projectFile} : error {issue.Code}: [{result.Preset}] {issue.Message}{(issue.Fix is null ? "" : " " + issue.Fix)}");
        foreach (var diagnostic in result.Diagnostics.Where(d => d.Severity == BuildDiagnosticSeverity.Error))
        {
            var where = diagnostic.File is null ? "mf-export" : diagnostic.Line > 0 ? $"{diagnostic.File}({diagnostic.Line},{diagnostic.Column})" : diagnostic.File;
            stderr.WriteLine($"{where} : error {diagnostic.Code}: {diagnostic.Message}");
        }

        if (!result.Succeeded && !result.Invalid && result.Error is { } error)
            stderr.WriteLine($"{projectFile} : error export: [{result.Preset}] {error}");
        var seconds = result.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        if (result.SymbolsPath is { } symbols)
            stdout.WriteLine($"Symbols: {symbols}");
        // The artifact last, alone on its line (scripts take the last line, as with package-game.sh).
        stdout.WriteLine(result.Succeeded ? $"{result.ArtifactPath}" : $"\"{result.Preset}\" {result.Summary} ({seconds} s)");
    }
}
