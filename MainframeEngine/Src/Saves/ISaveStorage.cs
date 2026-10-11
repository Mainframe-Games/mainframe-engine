namespace MainframeEngine;

/// <summary>
/// Where <see cref="SaveServer"/> keeps its files: <see cref="LocalSaveStorage"/> on every platform today, other stores
/// (tests, consoles) later. Paths are relative to the storage's root and use <c>/</c>. <see cref="SaveServer"/> calls it
/// from its worker, or from the calling thread for the synchronous methods, never from two threads at once (it holds a
/// lock around every call), so implementations need no locking of their own.
/// </summary>
public interface ISaveStorage
{
    /// <summary>A short name for logs ("local", "fake", …).</summary>
    string Name { get; }

    /// <summary>The file names (not paths) directly in <paramref name="folder"/> ("" is the root); empty when it is missing.</summary>
    IReadOnlyList<string> List(string folder);

    /// <summary>Opens <paramref name="path"/> for reading, or returns null when it does not exist.</summary>
    Stream? OpenRead(string path);

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="bytes"/> so that a crash at any point leaves a complete copy
    /// (the target, its <c>.tmp</c> or <c>.bak1</c>), keeping <paramref name="backups"/> previous versions as
    /// <c>path.bak1</c> … (<see cref="DurableFile"/>). Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> on failure, leaving the target unchanged.
    /// </summary>
    void WriteDurable(string path, ReadOnlySpan<byte> bytes, int backups);

    /// <summary>Deletes <paramref name="path"/>; false when it did not exist.</summary>
    bool Delete(string path);

    /// <summary>When <paramref name="path"/> was last written, or null when it does not exist.</summary>
    DateTimeOffset? GetModifiedUtc(string path);

    /// <summary>
    /// Renames <paramref name="path"/> to <paramref name="newPath"/> (which must not exist): how a damaged save is moved
    /// aside as <c>.corrupt-{utc}</c>. False when <paramref name="path"/> does not exist. The default copies through
    /// <see cref="OpenRead"/> and <see cref="WriteDurable"/>, then deletes; stores with a cheap rename override it.
    /// </summary>
    bool Move(string path, string newPath)
    {
        using (var source = OpenRead(path))
        {
            if (source is null)
                return false;
            using var copy = new MemoryStream();
            source.CopyTo(copy);
            WriteDurable(newPath, copy.GetBuffer().AsSpan(0, (int)copy.Length), 0);
        }

        Delete(path);
        return true;
    }
}

/// <summary>
/// <see cref="ISaveStorage"/> over a folder on disk (<see cref="GameHost"/> uses <c>{user data}/saves</c>). Writes go
/// through <see cref="DurableFile"/> (temp file, flush to disk, rolling backups, replace). The folder is created by the
/// first write, not by the constructor.
/// </summary>
public sealed class LocalSaveStorage : ISaveStorage
{
    /// <param name="root">The folder holding the files (made absolute).</param>
    public LocalSaveStorage(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>The folder holding the files.</summary>
    public string Root { get; }

    public string Name => "local";

    /// <summary>The file-system path of the storage path <paramref name="path"/> (for UI that shows thumbnails).</summary>
    public string FullPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
            return Root;
        if (Path.IsPathRooted(path) || path.Contains('\\', StringComparison.Ordinal))
            throw new ArgumentException($"'{path}' is not a storage path (relative, '/' separated).", nameof(path));
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or "." or "..")
                throw new ArgumentException($"'{path}' is not a storage path (relative, '/' separated).", nameof(path));
        }

        return Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    }

    public IReadOnlyList<string> List(string folder)
    {
        var directory = FullPath(folder);
        if (!Directory.Exists(directory))
            return [];
        var files = Directory.GetFiles(directory);
        var names = new string[files.Length];
        for (var i = 0; i < files.Length; i++)
            names[i] = Path.GetFileName(files[i]);
        return names;
    }

    public Stream? OpenRead(string path)
    {
        try
        {
            return new FileStream(FullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096,
                FileOptions.SequentialScan);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void WriteDurable(string path, ReadOnlySpan<byte> bytes, int backups) => DurableFile.Write(FullPath(path), bytes, backups);

    public bool Delete(string path)
    {
        var full = FullPath(path);
        if (!File.Exists(full))
            return false;
        File.Delete(full);
        return true;
    }

    public DateTimeOffset? GetModifiedUtc(string path)
    {
        var full = FullPath(path);
        return File.Exists(full) ? new DateTimeOffset(File.GetLastWriteTimeUtc(full), TimeSpan.Zero) : null;
    }

    public bool Move(string path, string newPath)
    {
        var from = FullPath(path);
        if (!File.Exists(from))
            return false;
        File.Move(from, FullPath(newPath), overwrite: false);
        return true;
    }
}
