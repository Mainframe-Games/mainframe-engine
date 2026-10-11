using System.ComponentModel;
using System.Diagnostics;

namespace MainframeEngine.Editor;

/// <summary>Hands things to the operating system: show a file or open a folder in Finder / Explorer / the file manager, open a URL.</summary>
public static class OsShell
{
    public static void Reveal(string path)
    {
        try
        {
            var start = OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open") { ArgumentList = { "-R", path } }
                : OperatingSystem.IsWindows()
                    ? new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path } }
                    : new ProcessStartInfo("xdg-open") { ArgumentList = { Directory.Exists(path) ? path : Path.GetDirectoryName(path)! } };
            start.UseShellExecute = false;
            using var _ = Process.Start(start);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Warning($"[Editor] Could not show {path}: {e.Message}");
        }
    }

    /// <summary>
    /// Opens <paramref name="directory"/> itself (not its parent with it selected, as <see cref="Reveal"/> does) in Finder /
    /// Explorer / the file manager; false when that failed (logged).
    /// </summary>
    public static bool OpenFolder(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        try
        {
            var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open")
            {
                ArgumentList = { directory },
                UseShellExecute = false,
            };
            using var _ = Process.Start(start);
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Warning($"[Editor] Could not open {directory}: {e.Message}");
            return false;
        }
    }

    public static void OpenUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Warning($"[Editor] Open {url.AbsoluteUri} in a browser ({e.Message}).");
        }
    }
}
