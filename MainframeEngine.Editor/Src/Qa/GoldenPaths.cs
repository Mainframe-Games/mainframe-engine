namespace MainframeEngine.Editor;

/// <summary>
/// Where editor golden runs put their throwaway projects. The paths appear on screen (Project Manager rows, Output
/// lines), so they must be identical on every machine: <see cref="Path.GetTempPath"/> is a per-user
/// <c>/var/folders/…/T/</c> on macOS, which would bake one Mac's temp folder into the moltenvk goldens. Unix uses the
/// fixed <c>/tmp/mainframe-golden</c> (macOS's <c>/tmp</c> is a symlink the editor never resolves); Windows has no editor
/// goldens and keeps the temp folder.
/// </summary>
public static class GoldenPaths
{
    public static string Root { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(Path.GetTempPath(), "mainframe-golden")
        : "/tmp/mainframe-golden";
}
