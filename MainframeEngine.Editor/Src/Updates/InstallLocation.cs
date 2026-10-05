namespace MainframeEngine.Editor;

public enum InstallKind
{
    /// <summary>The install can be renamed and replaced.</summary>
    Replaceable,

    /// <summary>The install or its parent folder is read-only (Program Files, a read-only mount).</summary>
    NotWritable,

    /// <summary>macOS App Translocation: an unsigned app started where it was downloaded runs from a random read-only copy.</summary>
    Translocated,

    /// <summary>A macOS editor that does not run from a <c>.app</c> bundle (a bare build output).</summary>
    NotBundled,
}

/// <summary>The running editor's install (<see cref="Root"/>: the <c>.app</c> bundle on macOS, the editor folder elsewhere).</summary>
public sealed record InstallLocation(string Root, InstallKind Kind)
{
    public bool CanReplace => Kind == InstallKind.Replaceable;

    /// <summary>Why the update is only downloaded (shown in the update dialog); null when it can be installed.</summary>
    public string? Hint => Kind switch
    {
        InstallKind.Translocated => "Move Mainframe Engine to Applications to enable automatic updates.",
        InstallKind.NotWritable => $"The editor's folder ({Root}) is read-only, so the update is downloaded for you to install by hand.",
        InstallKind.NotBundled => "This editor does not run from Mainframe Engine.app, so the update is downloaded for you to install by hand.",
        _ => null,
    };

    /// <summary>The install root for an editor whose <see cref="AppContext.BaseDirectory"/> is <paramref name="baseDirectory"/>.</summary>
    public static string? FindRoot(string baseDirectory, string rid)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);
        ArgumentException.ThrowIfNullOrEmpty(rid);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        if (!UpdatePlatform.IsMac(rid))
            return directory;
        // <X>.app/Contents/MacOS
        var contents = Path.GetDirectoryName(directory);
        var app = contents is null ? null : Path.GetDirectoryName(contents);
        return string.Equals(Path.GetFileName(directory), "MacOS", StringComparison.Ordinal) &&
               string.Equals(Path.GetFileName(contents), "Contents", StringComparison.Ordinal) &&
               app is not null && app.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? app
            : null;
    }

    public static bool IsTranslocated(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("AppTranslocation", StringComparer.Ordinal);

    /// <summary>Finds the root and whether it can be replaced (<paramref name="canWrite"/> defaults to <see cref="CanWriteTo"/>).</summary>
    public static InstallLocation Inspect(string baseDirectory, string rid, Func<string, bool>? canWrite = null)
    {
        var root = FindRoot(baseDirectory, rid);
        if (root is null)
            return new InstallLocation(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)), InstallKind.NotBundled);
        if (IsTranslocated(root))
            return new InstallLocation(root, InstallKind.Translocated);
        canWrite ??= CanWriteTo;
        var parent = Path.GetDirectoryName(root);
        return new InstallLocation(root, parent is not null && canWrite(parent) && canWrite(root) ? InstallKind.Replaceable : InstallKind.NotWritable);
    }

    /// <summary>True when a file can be created (and is deleted again) in <paramref name="directory"/>.</summary>
    public static bool CanWriteTo(string directory)
    {
        try
        {
            using (File.Create(Path.Combine(directory, $".mf-write-test-{Guid.NewGuid():N}"), 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
