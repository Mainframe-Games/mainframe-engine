using System.Text;

namespace MainframeEngine;

/// <summary>
/// An <see cref="IContentSource"/> holding its files in memory: what tests mount to prove a loader reads through
/// <see cref="ContentFiles"/>, and the reference behaviour for the pack source. Add every file before mounting it.
/// </summary>
internal sealed class MemoryContentSource : IContentSource
{
    /// <summary>The last write time every file reports (the content of a mounted source never changes).</summary>
    public static readonly DateTime LastWriteUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal) { "" };
    private readonly Dictionary<string, byte[]>.AlternateLookup<ReadOnlySpan<char>> _fileLookup;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _directoryLookup;

    public MemoryContentSource(string root, string name = "memory")
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Name = name;
        _fileLookup = _files.GetAlternateLookup<ReadOnlySpan<char>>();
        _directoryLookup = _directories.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public string Root { get; }

    public string Name { get; }

    /// <summary>Adds (or replaces) a file at <paramref name="path"/>, relative to <see cref="Root"/>.</summary>
    public MemoryContentSource Add(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);
        path = path.Replace('\\', '/').Trim('/');
        _files[path] = bytes;
        for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
            _directories.Add(path[..slash]);
        return this;
    }

    /// <summary>Adds a UTF-8 text file.</summary>
    public MemoryContentSource Add(string path, string text) => Add(path, Encoding.UTF8.GetBytes(text));

    public bool FileExists(ReadOnlySpan<char> path) => _fileLookup.ContainsKey(path);

    public bool DirectoryExists(ReadOnlySpan<char> path) => _directoryLookup.Contains(path);

    public ContentFileInfo GetInfo(ReadOnlySpan<char> path) =>
        _fileLookup.TryGetValue(path, out var bytes) ? new ContentFileInfo(true, bytes.Length, LastWriteUtc) : default;

    public Stream? OpenRead(string path) =>
        _files.TryGetValue(path, out var bytes) ? new MemoryStream(bytes, writable: false) : null;

    public IEnumerable<string> EnumerateFiles(string directory, bool recursive) =>
        _files.Keys.Where(file => IsIn(file, directory, recursive));

    public IEnumerable<string> EnumerateDirectories(string directory, bool recursive) =>
        _directories.Where(d => d.Length > 0 && IsIn(d, directory, recursive));

    private static bool IsIn(string path, string directory, bool recursive)
    {
        if (directory.Length > 0 && !(path.StartsWith(directory, StringComparison.Ordinal) && path.Length > directory.Length && path[directory.Length] == '/'))
            return false;
        var rest = directory.Length == 0 ? path.AsSpan() : path.AsSpan(directory.Length + 1);
        return recursive || !rest.Contains('/');
    }
}
