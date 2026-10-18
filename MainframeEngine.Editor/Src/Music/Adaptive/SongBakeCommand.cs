namespace MainframeEngine.Editor.Music;

/// <summary>
/// <c>--bake-song &lt;path.msong&gt;</c>: bakes an adaptive song headless (no window, SDL or engine) into its project like
/// the editor's Bake — the stems, their <c>.meta</c> and the <c>.mmusic</c> manifest — and exits 0, or 1 on an error (CI,
/// <c>just bake-song</c>). The project is the nearest folder above the song holding <c>project.mfproj</c>. Stems are
/// encoded to <c>.ogg</c> through the plugin helper when this platform has one, and plugin stems render through it.
/// </summary>
public static class SongBakeCommand
{
    public const string Flag = "--bake-song";

    public static bool IsBakeSong(IReadOnlyList<string> args) =>
        args is { Count: > 0 } && string.Equals(args[0], Flag, StringComparison.Ordinal);

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (!IsBakeSong(args) || args.Count != 2)
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

        if (SongRenderCommand.ProjectRootOf(path) is not { } root)
        {
            error.WriteLine($"'{path}' is not inside a project (no {ProjectSettings.FileName} above it).");
            return 1;
        }

        try
        {
            var song = SongFormat.Load(path).Song;
            var name = Path.GetFileNameWithoutExtension(path);
            var database = new AssetDatabase(root);
            AssetDatabase.Current = database; // audio clips resolve their UIDs in the song's project
            using var encoder = PluginHostClient.TryCreate();
            var options = new SongBakeOptions
            {
                Encoder = encoder,
                Plugins = SongRenderJob.UsesPlugins(song)
                    ? rate => new PluginRack(() => PluginHostClient.TryCreate(), rate, offline: true) { SongName = name }
                    : null,
            };
            var result = SongBaker.Bake(song, name, database, options);
            output.WriteLine($"Baked {result.ManifestPath} ({result.Summary}).");
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
                                      or FormatException or System.Text.Json.JsonException or PluginHostException or SongBakeException)
        {
            error.WriteLine($"Could not bake '{path}': {e.Message}");
            return 1;
        }
    }
}
