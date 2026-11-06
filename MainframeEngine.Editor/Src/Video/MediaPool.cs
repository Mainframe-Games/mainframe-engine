using MainframeEngine.Video.Timelines;

namespace MainframeEngine.Editor.Video;

/// <summary>What a media pool row is.</summary>
public enum MediaPoolKind
{
    Folder,
    Video,
    Audio,
    Image,
    Timeline,
}

/// <summary>One row of the media pool.</summary>
public sealed record MediaPoolItem(string Path, string Name, MediaPoolKind Kind);

/// <summary>
/// The media pool's listing (docs/design/future/video-nle.md): the project's <c>Assets/Video</c> folder and the folders
/// under it, showing only video, audio, images and timelines. Sidecars (<c>.mfmeta</c>, a recording's <c>.mfcap.json</c>, a
/// transcript), files being written (<c>.tmp</c>, a recording's <c>.partial</c> folder) and hidden files are left out.
/// Main thread; <see cref="Refresh"/> reads the disk, so it runs when the folder changed, not every frame.
/// </summary>
public sealed class MediaPool
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };

    private readonly List<MediaPoolItem> _items = [];

    public MediaPool(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ProjectRoot = Path.GetFullPath(projectRoot);
        RootFolder = Path.Combine(ProjectRoot, ContentPaths.EditorOnlyFolder.Replace('/', Path.DirectorySeparatorChar));
        Folder = RootFolder;
    }

    public string ProjectRoot { get; }

    /// <summary><c>&lt;project&gt;/Assets/Video</c>: the pool's top.</summary>
    public string RootFolder { get; }

    /// <summary>The folder listed.</summary>
    public string Folder { get; private set; }

    public IReadOnlyList<MediaPoolItem> Items => _items;

    public bool CanGoUp => !SamePath(Folder, RootFolder);

    /// <summary>Bumped by every <see cref="Refresh"/> that changed the rows.</summary>
    public int Version { get; private set; }

    /// <summary>The folder relative to the project, with <c>/</c>: <c>Assets/Video/Media</c>.</summary>
    public string DisplayPath => Path.GetRelativePath(ProjectRoot, Folder).Replace('\\', '/');

    /// <summary>The kind a file is shown as, or null when the pool does not show it.</summary>
    public static MediaPoolKind? KindOf(string path, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);
        var name = Path.GetFileName(path);
        if (name.Length == 0 || name.StartsWith('.'))
            return null;
        if (isDirectory)
            return name.EndsWith(AssetDatabase.PartialFolderSuffix, StringComparison.OrdinalIgnoreCase) ? null : MediaPoolKind.Folder;
        var extension = Path.GetExtension(name);
        if (TimelineSerializer.IsTimelinePath(name))
            return MediaPoolKind.Timeline;
        if (MediaRefs.VideoExtensions.Contains(extension))
            return MediaPoolKind.Video;
        if (MediaRefs.AudioExtensions.Contains(extension))
            return MediaPoolKind.Audio;
        return ImageExtensions.Contains(extension) ? MediaPoolKind.Image : null;
    }

    /// <summary>Lists <paramref name="folder"/> (inside the pool's root; created when it is the root and missing).</summary>
    public void Enter(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var full = Path.GetFullPath(folder);
        if (!IsUnder(full, RootFolder))
            return;
        Folder = full;
        Refresh();
    }

    public void Up()
    {
        if (CanGoUp && Path.GetDirectoryName(Folder) is { } parent)
            Enter(parent);
    }

    /// <summary>The pool's standard folders under <see cref="RootFolder"/>, made when video mode first uses the project.</summary>
    public static readonly string[] StandardFolders = ["Recordings", "Media", "Timelines"];

    /// <summary>Makes <c>Assets/Video</c> and its standard folders (video mode's first use of a project).</summary>
    public void EnsureFolders()
    {
        foreach (var folder in StandardFolders)
            Directory.CreateDirectory(Path.Combine(RootFolder, folder)); // disk-io: the editor writes into the project
    }

    /// <summary>Reads the folder: folders first, then files, each by name. True when the rows changed.</summary>
    public bool Refresh()
    {
        var rows = new List<MediaPoolItem>();
        if (Directory.Exists(Folder)) // disk-io: the editor lists the project's own folder
        {
            foreach (var directory in Directory.EnumerateDirectories(Folder).Order(StringComparer.OrdinalIgnoreCase))
                if (KindOf(directory, isDirectory: true) is { } kind)
                    rows.Add(new MediaPoolItem(directory, Path.GetFileName(directory), kind));
            foreach (var file in Directory.EnumerateFiles(Folder).Order(StringComparer.OrdinalIgnoreCase))
                if (KindOf(file, isDirectory: false) is { } kind)
                    rows.Add(new MediaPoolItem(file, Path.GetFileName(file), kind));
        }
        else if (CanGoUp)
        {
            Folder = RootFolder;
            return Refresh();
        }

        if (rows.SequenceEqual(_items))
            return false;
        _items.Clear();
        _items.AddRange(rows);
        Version++;
        return true;
    }

    private static bool IsUnder(string path, string root) =>
        SamePath(path, root) || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison);

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd(Path.DirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar), PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}

/// <summary>
/// Copies outside files into the project for video mode (File › Import Media, the pool's button, a Finder drop): into the
/// pool's folder under their own names (<c>name-2.ext</c> on a clash), each through a <c>.tmp</c> file that is renamed when
/// complete (the asset scan skips <c>.tmp</c>), with a recording's <c>.mfcap.json</c> sidecar beside it. A file already
/// inside the project is used where it is.
/// </summary>
public static class MediaImporter
{
    /// <summary>A recording's sidecar (the engine recorder's <c>&lt;video&gt;.mfcap.json</c>).</summary>
    public const string SidecarSuffix = ".mfcap.json";

    // The files that travel with a media file: the recording's sidecar and its transcript (G20d).
    private static readonly string[] Sidecars = [SidecarSuffix, MainframeEngine.Video.Transcript.TranscriptSerializer.Suffix];

    /// <summary>The free name for <paramref name="fileName"/> in <paramref name="folder"/>: as it is, then <c>-2</c>, <c>-3</c>….</summary>
    public static string Destination(string folder, string fileName, Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(exists);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var path = Path.Combine(folder, fileName);
        for (var n = 2; exists(path); n++)
            path = Path.Combine(folder, $"{stem}-{n.ToString(System.Globalization.CultureInfo.InvariantCulture)}{extension}");
        return path;
    }

    /// <summary>Whether <paramref name="path"/> is inside <paramref name="projectRoot"/>.</summary>
    public static bool IsInProject(string path, string projectRoot)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Imports <paramref name="sources"/> into <paramref name="folder"/>: the files the pool then holds, in order (an
    /// in-project file as it is). Folders and files the pool would not show are skipped.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ImportAsync(IReadOnlyList<string> sources, string folder, string projectRoot,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var result = new List<string>(sources.Count);
        Directory.CreateDirectory(folder); // disk-io: the editor writes into the project
        for (var i = 0; i < sources.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.GetFullPath(sources[i]);
            if (!File.Exists(source) || MediaPool.KindOf(source, isDirectory: false) is null) // disk-io: an outside file being imported
                continue;
            if (IsInProject(source, projectRoot))
            {
                result.Add(source);
                continue;
            }

            var target = Destination(folder, Path.GetFileName(source), File.Exists);
            await CopyAsync(source, target, cancellationToken).ConfigureAwait(false);
            foreach (var suffix in Sidecars)
                if (File.Exists(source + suffix))
                    await CopyAsync(source + suffix, target + suffix, cancellationToken).ConfigureAwait(false);
            result.Add(target);
            progress?.Report((i + 1) / (double)sources.Count);
        }

        return result;
    }

    private static async Task CopyAsync(string source, string target, CancellationToken cancellationToken)
    {
        var temp = target + ".tmp";
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(temp))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            File.Move(temp, target, overwrite: false);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }
}
