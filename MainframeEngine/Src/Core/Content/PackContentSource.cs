namespace MainframeEngine;

/// <summary>
/// A <see cref="PackReader"/> mounted with <see cref="ContentFiles.Mount"/>: an export's <c>Assets.mfpak</c> serving
/// <c>Assets/…</c> and <c>project.mfproj</c> under the application folder (G19, ADR 0239). Loose files beside it are the
/// disk fallthrough.
/// </summary>
public sealed class PackContentSource : IContentSource, IDisposable
{
    private readonly PackReader _pack;

    /// <summary>Serves <paramref name="pack"/>'s paths under <paramref name="root"/> (an absolute directory); owns the pack.</summary>
    public PackContentSource(PackReader pack, string root)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _pack = pack;
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public string Root { get; }

    public string Name => _pack.Name;

    /// <summary>The pack this source reads.</summary>
    public PackReader Pack => _pack;

    /// <summary>
    /// Opens <c>Assets.mfpak</c> in <paramref name="directory"/> and mounts it there, or returns null when there is none.
    /// The handle unmounts it and closes the pack. An encrypted pack opens with <paramref name="key"/>, else the game's
    /// (<see cref="PackKeys"/>). Throws <see cref="InvalidDataException"/> for a damaged pack and
    /// <see cref="PackKeyException"/> for a missing or wrong key.
    /// </summary>
    public static IDisposable? MountBeside(string directory, int priority = 0, byte[]? key = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var file = Path.Combine(directory, PackFormat.FileName);
        if (!File.Exists(file)) // disk-io: the pack itself
            return null;
        var source = new PackContentSource(PackReader.Open(file, key), directory);
        return new Mounted(ContentFiles.Mount(source, priority), source);
    }

    public bool FileExists(ReadOnlySpan<char> path) => _pack.FileExists(path);

    public bool DirectoryExists(ReadOnlySpan<char> path) => _pack.DirectoryExists(path);

    public ContentFileInfo GetInfo(ReadOnlySpan<char> path) =>
        _pack.TryGetEntry(path, out var entry) ? new ContentFileInfo(true, entry.Length, PackFormat.LastWriteUtc) : default;

    public Stream? OpenRead(string path) => _pack.TryGetEntry(path, out var entry) ? _pack.OpenEntry(entry) : null;

    public IEnumerable<string> EnumerateFiles(string directory, bool recursive) => _pack.EnumerateFiles(directory, recursive);

    public IEnumerable<string> EnumerateDirectories(string directory, bool recursive) => _pack.EnumerateDirectories(directory, recursive);

    public void Dispose() => _pack.Dispose();

    private sealed class Mounted(IDisposable mount, PackContentSource source) : IDisposable
    {
        public void Dispose()
        {
            mount.Dispose();
            source.Dispose();
        }
    }
}
