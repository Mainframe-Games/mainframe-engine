using System.Diagnostics;

namespace MainframeEngine.Editor;

/// <summary>
/// Opens a source file at a line in the user's code editor (the Output panel's click-to-source): the command in
/// <c>MAINFRAME_CODE_EDITOR</c> (<c>{file}</c> and <c>{line}</c> are replaced, e.g. <c>rider --line {line} {file}</c>),
/// else Visual Studio Code when <c>code</c> is on the PATH, else the system's default app for the file.
/// </summary>
public static class ExternalEditor
{
    /// <summary>Opens <paramref name="file"/> at <paramref name="line"/>; false (logged) when nothing could be started.</summary>
    public static bool Open(string file, int line)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        try
        {
            var template = Environment.GetEnvironmentVariable("MAINFRAME_CODE_EDITOR");
            ProcessStartInfo info;
            if (!string.IsNullOrWhiteSpace(template))
            {
                var parts = template.Trim().Split(' ', 2);
                info = new ProcessStartInfo(parts[0], parts.Length > 1 ? Substitute(parts[1], file, line) : $"\"{file}\"");
            }
            else if (FindOnPath(OperatingSystem.IsWindows() ? "code.cmd" : "code") is { } code)
            {
                info = new ProcessStartInfo(code) { ArgumentList = { "-g", $"{file}:{Math.Max(1, line)}" } };
            }
            else if (OperatingSystem.IsMacOS())
            {
                info = new ProcessStartInfo("open") { ArgumentList = { file } };
            }
            else if (OperatingSystem.IsWindows())
            {
                info = new ProcessStartInfo(file) { UseShellExecute = true };
            }
            else
            {
                info = new ProcessStartInfo("xdg-open") { ArgumentList = { file } };
            }

            using var process = Process.Start(info);
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warning($"[Editor] Could not open {file}:{line}: {e.Message}");
            return false;
        }
    }

    private static string Substitute(string arguments, string file, int line) =>
        arguments.Replace("{file}", $"\"{file}\"", StringComparison.Ordinal).Replace("{line}", Math.Max(1, line).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static string? FindOnPath(string name)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
