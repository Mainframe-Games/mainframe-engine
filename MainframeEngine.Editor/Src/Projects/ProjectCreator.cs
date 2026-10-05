using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MainframeEngine.Editor;

/// <summary>The outcome of <see cref="ProjectCreator.CreateAsync"/>.</summary>
/// <param name="Succeeded">The project exists, and its <c>project.mfproj</c> and main scene were verified.</param>
/// <param name="ProjectDirectory">
/// The new project's folder (also on failure: where it would have been), as a real path
/// (<see cref="GameProjectLayout.RealPath"/>: a symlinked parent folder is resolved, since MSBuild cannot build through it).
/// </param>
/// <param name="Error">What failed, as user-facing text with the tail of the tool output; null on success.</param>
/// <param name="Log">Every command run and every line it printed (for the Output panel or a "details" box).</param>
/// <param name="Duration">Wall-clock time of the whole creation.</param>
public sealed record ProjectCreateResult(bool Succeeded, string ProjectDirectory, string? Error, string Log, TimeSpan Duration);

/// <summary>
/// Creates a game project from the <c>mfgame</c> template, as <c>build/template-smoke.sh</c> does: the template is
/// installed into a private template hive (<c>--debug:custom-hive</c>, default <c>~/.mainframe/templates</c>) so the
/// user's global <c>dotnet new</c> templates are never touched, then <c>dotnet new mfgame</c> runs against the chosen
/// engine checkout. The install is skipped while a marker in the hive records the same template (folder, files and
/// <c>dotnet</c>); if creating then fails, the template is reinstalled and creation retried once.
/// </summary>
/// <remarks>
/// A failed or cancelled creation removes what it wrote (the target folder was missing or empty before), so the user
/// can retry with the same name. Never throws for tool failures; argument errors still throw.
/// </remarks>
public sealed class ProjectCreator
{
    /// <summary>How long one <c>dotnet new</c> command may run.</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(3);

    private const string MarkerFileName = "mainframe-template.txt";
    private const int ErrorTailLines = 15;

    public ProjectCreator(string dotnetPath, string templateDirectory, string? hiveDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateDirectory);
        DotnetPath = dotnetPath;
        TemplateDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(templateDirectory));
        HiveDirectory = Path.GetFullPath(hiveDirectory ?? DefaultHiveDirectory);
    }

    /// <summary>The default private template hive: <c>~/.mainframe/templates</c>.</summary>
    public static string DefaultHiveDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mainframe", "templates");

    /// <summary>The <c>dotnet</c> executable used for every command.</summary>
    public string DotnetPath { get; }

    /// <summary>The <c>mfgame</c> template folder (<see cref="TemplateLocator.FindTemplate"/>).</summary>
    public string TemplateDirectory { get; }

    /// <summary>The private template hive the template is installed into.</summary>
    public string HiveDirectory { get; }

    /// <summary>
    /// True when the hive's marker records this template as installed (same folder, same files, same <c>dotnet</c>),
    /// so <see cref="CreateAsync"/> skips <c>dotnet new install</c>.
    /// </summary>
    public bool IsTemplateInstalled()
    {
        try
        {
            var marker = Path.Combine(HiveDirectory, MarkerFileName);
            return File.Exists(marker) && string.Equals(File.ReadAllText(marker), MarkerContent(), StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Validates <paramref name="request"/> (<see cref="NewProjectValidation.Validate"/>), installs the template when
    /// needed, runs <c>dotnet new mfgame -n Name -o ProjectDirectory --engine-path EnginePath --engine-version
    /// EngineInfo.Version</c> and checks the result: <c>project.mfproj</c> loads and <c>Content/Scenes/Main.mscene</c>
    /// exists. <paramref name="onOutput"/> receives every log line (from a background thread). Cancelling kills the
    /// running command and returns a failed result.
    /// </summary>
    public async Task<ProjectCreateResult> CreateAsync(NewProjectRequest request, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var log = new StringBuilder();
        var target = SafeProjectDirectory(request);

        void Write(string line)
        {
            lock (log)
                log.Append(line).Append('\n');
            try
            {
                onOutput?.Invoke(line);
            }
            catch (Exception e) when (EditorCommands.IsRecoverable(e))
            {
                Console.Error.WriteLine($"[ProjectCreator] Output handler failed: {e.Message}");
            }
        }

        ProjectCreateResult Fail(string error)
        {
            Write(error);
            string text;
            lock (log)
                text = log.ToString();
            return new ProjectCreateResult(false, target, error, text, stopwatch.Elapsed);
        }

        if (NewProjectValidation.Validate(request) is { } invalid)
            return Fail(invalid);
        target = Path.Combine(GameProjectLayout.RealPath(request.ParentDirectory), request.Name);
        if (!TemplateLocator.IsTemplate(TemplateDirectory))
            return Fail($"The project template was not found at '{TemplateDirectory}' (no .template.config/template.json).");

        var targetExisted = Directory.Exists(target);
        var skippedInstall = IsTemplateInstalled();
        if (skippedInstall)
            Write($"Template already installed in {HiveDirectory}.");
        else if (await InstallAsync(Write, ct).ConfigureAwait(false) is { } installError)
            return Fail(installError);

        var (createError, cancelled) = await RunNewAsync(request, target, Write, ct).ConfigureAwait(false);
        if (createError is not null && skippedInstall && !cancelled)
        {
            // The marker said installed, but the hive may have been reset (or the SDK changed): reinstall and retry once.
            Write("Creating failed with the installed template; reinstalling it and retrying.");
            Cleanup(target, targetExisted, Write);
            if (await InstallAsync(Write, ct).ConfigureAwait(false) is { } installError)
                return Fail(installError);
            (createError, cancelled) = await RunNewAsync(request, target, Write, ct).ConfigureAwait(false);
        }

        if (createError is not null)
        {
            Cleanup(target, targetExisted, Write);
            return Fail(createError);
        }

        if (Verify(target) is { } verifyError)
        {
            Cleanup(target, targetExisted, Write);
            return Fail(verifyError);
        }

        Write($"Created {request.Name} in {target} ({stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s).");
        string done;
        lock (log)
            done = log.ToString();
        return new ProjectCreateResult(true, target, null, done, stopwatch.Elapsed);
    }

    private async Task<string?> InstallAsync(Action<string> write, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(HiveDirectory);
            File.Delete(Path.Combine(HiveDirectory, MarkerFileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Could not create the template folder '{HiveDirectory}': {e.Message}";
        }

        string[] arguments = ["new", "install", TemplateDirectory, "--debug:custom-hive", HiveDirectory, "--force"];
        var result = await RunAsync(arguments, write, ct).ConfigureAwait(false);
        if (Describe(result, "Installing the project template (dotnet new install)") is { } error)
            return error;

        try
        {
            AtomicFile.WriteAllBytes(Path.Combine(HiveDirectory, MarkerFileName), Encoding.UTF8.GetBytes(MarkerContent()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the next creation installs again.
            write($"Could not record the template installation: {e.Message}");
        }

        return null;
    }

    private async Task<(string? Error, bool Cancelled)> RunNewAsync(NewProjectRequest request, string target, Action<string> write, CancellationToken ct)
    {
        string[] arguments =
        [
            "new", "mfgame", "-n", request.Name, "-o", target,
            "--engine-path", GameProjectLayout.RealPath(request.EnginePath), "--engine-version", EngineInfo.Version,
            "--debug:custom-hive", HiveDirectory,
        ];
        var result = await RunAsync(arguments, write, ct).ConfigureAwait(false);
        return (Describe(result, "Creating the project (dotnet new mfgame)"), result.Cancelled);
    }

    private Task<ProcessResult> RunAsync(string[] arguments, Action<string> write, CancellationToken ct)
    {
        write($"> {DotnetPath} {string.Join(' ', arguments.Select(Quote))}");
        // The hive folder has no global.json above it (normally), so the user's folders cannot pin another SDK.
        return ProcessRunner.RunAsync(DotnetPath, arguments, workingDirectory: HiveDirectory, onLine: write, timeout: CommandTimeout, cancellationToken: ct);
    }

    private static string? Describe(ProcessResult result, string step)
    {
        if (result.StartError is not null)
            return $"{step} failed: {result.StartError} Check that the .NET 10 SDK is installed ({DotnetSdk.DownloadUrl}).";
        if (result.Cancelled)
            return "Creating the project was cancelled.";
        if (result.TimedOut)
            return $"{step} did not finish within {CommandTimeout.TotalMinutes:0} minutes.{Tail(result.Output)}";
        if (result.ExitCode != 0)
            return $"{step} failed (exit code {result.ExitCode}).{Tail(result.Output)}";
        return null;
    }

    internal static string? Verify(string target)
    {
        var projectFile = GameProjectLayout.ProjectFileOf(target);
        if (!File.Exists(projectFile))
            return $"The template did not create {ProjectSettings.FileName} in '{target}'.";
        var scene = Path.Combine(target, "Content", "Scenes", "Main.mscene");
        if (!File.Exists(scene))
            return $"The template did not create Content/Scenes/Main.mscene in '{target}'.";
        ProjectSettings settings;
        try
        {
            settings = ProjectSettings.Load(projectFile);
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            return $"The new project's {ProjectSettings.FileName} could not be read: {e.Message}";
        }

        if (settings.Window.Icon is { Length: > 0 } icon && !File.Exists(Path.Combine(target, icon)))
            return $"The new project's icon '{icon}' is missing.";
        return null;
    }

    // Removes what a failed creation wrote; the folder was missing or empty before (validation guarantees it).
    private static void Cleanup(string target, bool existedBefore, Action<string> write)
    {
        try
        {
            if (!Directory.Exists(target))
                return;
            if (!existedBefore)
            {
                Directory.Delete(target, recursive: true);
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(target).ToArray())
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            write($"Could not remove the partly created project at '{target}': {e.Message}");
        }
    }

    // What the marker records: the template folder, every file's path/size/time, and the dotnet used.
    private string MarkerContent()
    {
        var builder = new StringBuilder();
        builder.Append(TemplateDirectory).Append('\n').Append(DotnetPath).Append('\n');
        var files = Directory.EnumerateFiles(TemplateDirectory, "*", SearchOption.AllDirectories)
            .Select(f => (Relative: Path.GetRelativePath(TemplateDirectory, f).Replace('\\', '/'), Full: f))
            .OrderBy(f => f.Relative, StringComparer.Ordinal);
        var fingerprint = new StringBuilder();
        foreach (var (relative, full) in files)
        {
            var info = new FileInfo(full);
            fingerprint.Append(relative).Append('|').Append(info.Length.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        builder.Append(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString())))).Append('\n');
        return builder.ToString();
    }

    private static string Tail(IReadOnlyList<string> output)
    {
        var lines = output.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(ErrorTailLines).ToArray();
        return lines.Length == 0 ? " The tool printed nothing." : "\n" + string.Join('\n', lines);
    }

    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"') ? argument : $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    // The request is validated after this, so it may hold empty or malformed paths.
    private static string SafeProjectDirectory(NewProjectRequest request)
    {
        try
        {
            return Path.GetFullPath(request.ProjectDirectory);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"{request.ParentDirectory}{Path.DirectorySeparatorChar}{request.Name}";
        }
    }
}
