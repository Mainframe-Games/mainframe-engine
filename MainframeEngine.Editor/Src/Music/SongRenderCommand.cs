namespace MainframeEngine.Editor.Music;

/// <summary>
/// <c>--render-song &lt;path.msong&gt;</c>: renders a song headless (no window, SDL or engine) into its project like the
/// song tab's Render — the output file and its <c>.meta</c> — and exits 0, or 1 on an error (CI, batch renders,
/// <c>just render-song</c>). The project is the nearest folder above the song holding <c>project.mfproj</c>. An <c>.ogg</c>
/// output is encoded through the plugin helper when this platform has one (<see cref="PluginHostClient.Locate"/>).
/// </summary>
public static class SongRenderCommand
{
    public const string Flag = "--render-song";

    public static bool IsRenderSong(IReadOnlyList<string> args) =>
        args is { Count: > 0 } && string.Equals(args[0], Flag, StringComparison.Ordinal);

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (!IsRenderSong(args) || args.Count != 2)
        {
            error.WriteLine($"Usage: MainframeEngine.Editor {Flag} <path.msong>");
            return 1;
        }

        var path = Path.GetFullPath(args[1]);
        if (!File.Exists(path))
        {
            error.WriteLine($"No song at '{path}'.");
            return 1;
        }

        if (ProjectRootOf(path) is not { } root)
        {
            error.WriteLine($"'{path}' is not inside a project (no {ProjectSettings.FileName} above it).");
            return 1;
        }

        try
        {
            var song = SongFormat.Load(path).Song;
            var database = new AssetDatabase(root);
            AssetDatabase.Current = database; // audio clips resolve their UIDs in the song's project
            using var encoder = PluginHostClient.TryCreate();
            var result = SongRenderer.Render(song, Path.GetFileNameWithoutExtension(path), database, encoder: encoder);
            output.WriteLine($"Rendered {result.OutputPath} ({result.Seconds:0.00} s, {result.SampleRate} Hz{(result.Loop ? ", loops" : "")}).");
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
                                      or FormatException or System.Text.Json.JsonException or PluginHostException)
        {
            error.WriteLine($"Could not render '{path}': {e.Message}");
            return 1;
        }
    }

    /// <summary>The nearest folder at or above <paramref name="songPath"/>'s folder that holds <c>project.mfproj</c>, or null.</summary>
    public static string? ProjectRootOf(string songPath)
    {
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(songPath)); dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, ProjectSettings.FileName)))
                return dir;
        return null;
    }
}
