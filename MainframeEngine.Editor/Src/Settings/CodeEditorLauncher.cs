using System.Diagnostics;

namespace MainframeEngine.Editor;

/// <summary>
/// Opens source files in the external code editor (Editor Settings › code editor command: VS Code, Rider…), at a line
/// when known — double-clicking a <c>.cs</c> file in the FileSystem panel, clicking a build error or a game log line.
/// An empty command opens the file with the system's default application. The program is looked up on <c>PATH</c>
/// plus the usual tool folders (an app started from the Finder gets a minimal <c>PATH</c>).
/// </summary>
public sealed class CodeEditorLauncher
{
    private readonly Func<EditorSettings> _settings;
    private readonly Func<string?> _projectRoot;
    private readonly Func<ProcessStartInfo, bool> _start;

    /// <param name="start">Starts the process (tests record instead); returns whether it started.</param>
    public CodeEditorLauncher(Func<EditorSettings> settings, Func<string?> projectRoot, Func<ProcessStartInfo, bool>? start = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _projectRoot = projectRoot ?? throw new ArgumentNullException(nameof(projectRoot));
        _start = start ?? StartProcess;
    }

    /// <summary>The last start info used (tests, diagnostics).</summary>
    public ProcessStartInfo? LastStart { get; private set; }

    /// <summary>Opens <paramref name="file"/> at <paramref name="line"/>/<paramref name="column"/>; false (and a warning) when it could not.</summary>
    public bool Open(string file, int line = 1, int column = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        if (!File.Exists(file))
        {
            Log.Warning($"[Editor] Cannot open '{file}': the file does not exist.");
            return false;
        }

        var start = CreateStartInfo(EffectiveCommand(_settings().CodeEditorCommand), file, line, column, _projectRoot());
        LastStart = start;
        try
        {
            if (_start(start))
                return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warning($"[Editor] Could not start the code editor ({start.FileName}): {e.Message}. Set the command in Editor Settings.");
            return false;
        }

        Log.Warning($"[Editor] The code editor ({start.FileName}) did not start. Set the command in Editor Settings.");
        return false;
    }

    /// <summary>
    /// The command used when the setting is <paramref name="configured"/>: itself when set, else <c>MAINFRAME_CODE_EDITOR</c>,
    /// else Visual Studio Code when <c>code</c> is found, else "" (the system's default application).
    /// </summary>
    public static string EffectiveCommand(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        if (Environment.GetEnvironmentVariable("MAINFRAME_CODE_EDITOR") is { Length: > 0 } fromEnvironment)
            return fromEnvironment;
        var code = ResolveProgram(OperatingSystem.IsWindows() ? "code.cmd" : "code");
        return Path.IsPathRooted(code) ? EditorSettings.CodeEditorPresets[1].Command : "";
    }

    /// <summary>The process to start for <paramref name="command"/> (see <see cref="EditorSettings.ExpandCommand"/>).</summary>
    public static ProcessStartInfo CreateStartInfo(string command, string file, int line, int column, string? project)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (EditorSettings.ExpandCommand(command, file, line, column, project) is not { } expanded)
            return new ProcessStartInfo(file) { UseShellExecute = true };
        var start = new ProcessStartInfo(ResolveProgram(expanded.Program)) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in expanded.Arguments)
            start.ArgumentList.Add(argument);
        start.Environment["PATH"] = SearchPath();
        return start;
    }

    /// <summary><paramref name="program"/> as a full path when found on the search path, else unchanged.</summary>
    public static string ResolveProgram(string program)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        if (Path.IsPathRooted(program) || program.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return program;
        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat", ""] : [""];
        foreach (var directory in SearchPath().Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, program + extension);
                if (File.Exists(candidate))
                    return candidate;
            }

        return program;
    }

    private static string SearchPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (OperatingSystem.IsWindows())
            return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] extra =
        [
            "/usr/local/bin", "/opt/homebrew/bin", "/usr/bin", "/snap/bin", Path.Combine(home, ".local", "bin"),
            "/Applications/Visual Studio Code.app/Contents/Resources/app/bin",
            Path.Combine(home, "Library", "Application Support", "JetBrains", "Toolbox", "scripts"),
        ];
        var parts = new List<string>(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var directory in extra)
            if (!parts.Contains(directory, StringComparer.Ordinal))
                parts.Add(directory);
        return string.Join(Path.PathSeparator, parts);
    }

    private static bool StartProcess(ProcessStartInfo start)
    {
        using var process = Process.Start(start);
        return process is not null || start.UseShellExecute;
    }
}
