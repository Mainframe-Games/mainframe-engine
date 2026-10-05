using System.ComponentModel;
using System.Diagnostics;

namespace MainframeEngine.Editor;

/// <summary>Hands things to the operating system: show a file in Finder / Explorer / the file manager, open a URL.</summary>
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
