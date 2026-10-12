using System.Globalization;

namespace MainframeEngine.Editor;

/// <summary>
/// Where the editor's Record button saves: <c>{project user data}/recordings/{scene}-{yyyyMMdd-HHmmss}.mp4</c>. The name
/// carries the scene and the start time so recordings sort by when they were made, and a suffix keeps two starts in
/// the same second from overwriting each other.
/// </summary>
public static class RecordingPaths
{
    public static string Directory(string userDataDirectory) => Path.Combine(userDataDirectory, "recordings");

    /// <summary>
    /// A path in <see cref="Directory"/> that <paramref name="exists"/> reports free: the scene's file name (or "game"
    /// without a scene) and <paramref name="now"/>, with <c>-2</c>, <c>-3</c>… appended while the name is taken.
    /// </summary>
    public static string NewPath(string userDataDirectory, string? scenePath, DateTime now, Func<string, bool> exists)
    {
        var label = string.IsNullOrEmpty(scenePath) ? "game" : Safe(Path.GetFileNameWithoutExtension(scenePath));
        var stem = $"{label}-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        var path = Path.Combine(Directory(userDataDirectory), stem + ".mp4");
        for (var n = 2; exists(path); n++)
            path = Path.Combine(Directory(userDataDirectory), $"{stem}-{n.ToString(CultureInfo.InvariantCulture)}.mp4");
        return path;
    }

    /// <summary>
    /// Keeps letters, digits, <c>-</c> and <c>_</c> and turns anything else (spaces, dots, non-ASCII punctuation) into
    /// <c>_</c>, so the name is safe on every platform's file system and in a command line.
    /// </summary>
    private static string Safe(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return chars.Length == 0 ? "game" : new string(chars);
    }
}
