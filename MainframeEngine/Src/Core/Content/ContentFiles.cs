using System.IO.Enumeration;
using System.Text;

namespace MainframeEngine;

/// <summary>
/// The one read path for engine content (G19, ADR 0239): every runtime read of shaders, textures, scenes, UI, audio,
/// catalogs and models goes through here instead of <see cref="File"/>, so an export can serve them from a pack
/// (docs/design/asset-pipeline.md#content-file-system).
/// </summary>
/// <remarks>
/// <para>Paths are what <see cref="File"/> takes, so every call is a one-for-one swap: resolve content paths
/// (<c>"Assets/…"</c>) with <see cref="ContentPaths.Resolve(string)"/> first, as engine code does; another relative path
/// resolves against the working directory, as <see cref="File"/> would. A mounted <see cref="IContentSource"/> serves
/// the absolute paths under its <see cref="IContentSource.Root"/> directory, so code that keeps
/// <c>AssetDatabase.ToAbsolutePath</c> results keeps working. A path is looked up in the mounts whose root holds it,
/// highest priority first, then on the disk. With nothing mounted every call is the <see cref="File"/>/
/// <see cref="Directory"/> call it replaces.</para>
/// <para>Writes are not content (saves, settings, logs, caches, editor saves) and stay on <see cref="File"/>; a read
/// that must see the disk carries a <c>// disk-io:</c> comment (the audit in <c>ContentIoAuditTests</c>).</para>
/// </remarks>
public static class ContentFiles
{
    /// <summary>Relative paths up to this length are normalised on the stack (longer ones allocate).</summary>
    private const int StackPathLength = 512;

    private static readonly Lock MountGate = new();
    private static MountEntry[] _mounts = [];
    private static long _mountOrder;

    /// <summary>
    /// Serves the paths under <paramref name="source"/>'s root from it until the returned handle is disposed. Higher
    /// <paramref name="priority"/> wins; equal priorities are asked in mount order. Thread-safe: readers see the mounts
    /// before or after the call, never half of it.
    /// </summary>
    public static IDisposable Mount(IContentSource source, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Path.IsPathFullyQualified(source.Root) || Path.EndsInDirectorySeparator(source.Root))
            throw new ArgumentException($"A content source's root must be an absolute directory without a trailing separator: '{source.Root}'.", nameof(source));

        lock (MountGate)
        {
            var entry = new MountEntry(source, priority, _mountOrder++);
            MountEntry[] next = [.. _mounts, entry];
            Array.Sort(next, static (a, b) => a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : a.Order.CompareTo(b.Order));
            Volatile.Write(ref _mounts, next);
            return entry;
        }
    }

    /// <summary>Whether a content file exists (false for a null or empty path, like <see cref="File.Exists"/>). 0 B for an
    /// absolute path, mounted or not.</summary>
    public static bool Exists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var full = Full(path);
        Span<char> buffer = stackalloc char[StackPathLength];
        foreach (var mount in Volatile.Read(ref _mounts))
        {
            if (IsUnder(full, mount.Source.Root) && mount.Source.FileExists(Relative(full, mount.Source.Root, buffer)))
                return true;
        }

        return File.Exists(full);
    }

    /// <summary>Whether a content directory exists (false for a null or empty path, like <see cref="Directory.Exists"/>).</summary>
    public static bool DirectoryExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var full = Full(path);
        Span<char> buffer = stackalloc char[StackPathLength];
        foreach (var mount in Volatile.Read(ref _mounts))
        {
            if (IsUnder(full, mount.Source.Root) && mount.Source.DirectoryExists(Relative(full, mount.Source.Root, buffer)))
                return true;
        }

        return Directory.Exists(full);
    }

    /// <summary>A content file's length and last write time; <c>default</c> when it does not exist.</summary>
    public static ContentFileInfo GetInfo(string path)
    {
        var full = Full(path);
        Span<char> buffer = stackalloc char[StackPathLength];
        foreach (var mount in Volatile.Read(ref _mounts))
        {
            if (IsUnder(full, mount.Source.Root) && mount.Source.GetInfo(Relative(full, mount.Source.Root, buffer)) is { Exists: true } info)
                return info;
        }

        var file = new FileInfo(full);
        return file.Exists ? new ContentFileInfo(true, file.Length, file.LastWriteTimeUtc) : default;
    }

    /// <summary>
    /// A seekable read stream over a content file. On disk it is a <see cref="FileStream"/> with
    /// <paramref name="share"/> and <paramref name="bufferSize"/>. A file missing from the mounts that hold its path and
    /// from the disk throws <see cref="FileNotFoundException"/> naming those mounts.
    /// </summary>
    public static Stream OpenRead(string path, FileShare share = FileShare.Read, int bufferSize = 4096)
    {
        var full = Full(path);
        var searched = false;
        foreach (var mount in Volatile.Read(ref _mounts))
        {
            if (!IsUnder(full, mount.Source.Root))
                continue;
            searched = true;
            if (mount.Source.OpenRead(RelativeString(full, mount.Source.Root)) is { } stream)
                return stream;
        }

        if (searched && !File.Exists(full))
            throw new FileNotFoundException($"Content not found: '{full}' (searched {SearchedNames(full)} and the disk).", full);
        return new FileStream(full, FileMode.Open, FileAccess.Read, share, bufferSize);
    }

    /// <summary>A content file's bytes.</summary>
    public static byte[] ReadAllBytes(string path)
    {
        var full = Full(path);
        if (!UnderAnyMount(full))
            return File.ReadAllBytes(full);

        using var stream = OpenRead(full);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>A content file's text: UTF-8 unless a byte order mark or <paramref name="encoding"/> says otherwise.</summary>
    public static string ReadAllText(string path, Encoding? encoding = null)
    {
        var full = Full(path);
        if (!UnderAnyMount(full))
            return encoding is null ? File.ReadAllText(full) : File.ReadAllText(full, encoding);

        using var reader = new StreamReader(OpenRead(full), encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The files in a content directory matching <paramref name="pattern"/> (<c>*</c> and <c>?</c>), as absolute paths.
    /// With nothing mounted over it, this is <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>;
    /// otherwise the mounts' and the disk's files, without duplicates, in ordinal order.
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string directory, string pattern = "*", bool recursive = false) =>
        Enumerate(directory, pattern, recursive, files: true);

    /// <summary>The directories in a content directory, like <see cref="EnumerateFiles"/>.</summary>
    public static IEnumerable<string> EnumerateDirectories(string directory, string pattern = "*", bool recursive = false) =>
        Enumerate(directory, pattern, recursive, files: false);

    private static IEnumerable<string> Enumerate(string directory, string pattern, bool recursive, bool files)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var full = Full(directory);
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        if (!UnderAnyMount(full))
            return files ? Directory.EnumerateFiles(full, pattern, option) : Directory.EnumerateDirectories(full, pattern, option);

        // Only mounts whose root holds the directory take part: a source never lists itself into a parent of its root.
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var exists = false;
        foreach (var mount in Volatile.Read(ref _mounts))
        {
            var root = mount.Source.Root;
            if (!IsUnder(full, root))
                continue;
            var relative = RelativeString(full, root);
            if (!mount.Source.DirectoryExists(relative))
                continue;
            exists = true;
            var entries = files ? mount.Source.EnumerateFiles(relative, recursive) : mount.Source.EnumerateDirectories(relative, recursive);
            foreach (var entry in entries)
            {
                if (FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(entry.AsSpan()), ignoreCase: false))
                    found.Add(Path.Join(root, entry.Replace('/', Path.DirectorySeparatorChar)));
            }
        }

        if (Directory.Exists(full))
        {
            exists = true;
            found.UnionWith(files ? Directory.EnumerateFiles(full, pattern, option) : Directory.EnumerateDirectories(full, pattern, option));
        }

        return exists ? found : throw new DirectoryNotFoundException($"Content directory not found: '{full}' (searched {SearchedNames(full)} and the disk).");
    }

    /// <summary>Whether a mount's root holds <paramref name="path"/> (native readers that open files by path need a bridge then).</summary>
    internal static bool IsMounted(string path) => UnderAnyMount(Full(path));

    // A normalised absolute path is used as it is (0 B); anything else is normalised as File would (relative paths
    // against the working directory), so mounted roots compare against one spelling.
    private static string Full(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.IsPathFullyQualified(path) && !NeedsNormalising(path) ? path : Path.GetFullPath(path);
    }

    private static bool NeedsNormalising(string path)
    {
        if (Path.DirectorySeparatorChar != '/' && path.Contains('/'))
            return true;

        for (var i = 0; i + 1 < path.Length; i++)
        {
            if (IsSeparator(path[i]) && path[i + 1] == '.' && (i + 2 == path.Length || IsSeparator(path[i + 2]) || path[i + 2] == '.'))
                return true;
        }

        return false;
    }

    private static bool UnderAnyMount(string full)
    {
        foreach (var mount in Volatile.Read(ref _mounts))
        {
            if (IsUnder(full, mount.Source.Root))
                return true;
        }

        return false;
    }

    private static bool IsUnder(string full, string root) =>
        full.StartsWith(root, StringComparison.Ordinal) && (full.Length == root.Length || IsSeparator(full[root.Length]));

    private static bool IsSeparator(char c) => c == '/' || c == Path.DirectorySeparatorChar;

    // The '/'-separated path below root ("" for root itself), copied into buffer only when separators need changing.
    private static ReadOnlySpan<char> Relative(string full, string root, Span<char> buffer)
    {
        var relative = full.AsSpan(Math.Min(full.Length, root.Length + 1));
        if (Path.DirectorySeparatorChar == '/' || !relative.Contains(Path.DirectorySeparatorChar))
            return relative;
        if (relative.Length > buffer.Length)
            return relative.ToString().Replace(Path.DirectorySeparatorChar, '/');
        relative.Replace(buffer, Path.DirectorySeparatorChar, '/');
        return buffer[..relative.Length];
    }

    private static string RelativeString(string full, string root)
    {
        var relative = full.Length > root.Length ? full[(root.Length + 1)..] : "";
        return Path.DirectorySeparatorChar == '/' ? relative : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string SearchedNames(string full)
    {
        var names = new List<string>();
        foreach (var mount in Volatile.Read(ref _mounts))
        {
            if (IsUnder(full, mount.Source.Root))
                names.Add($"'{mount.Source.Name}'");
        }

        return string.Join(", ", names);
    }

    private static void Unmount(MountEntry entry)
    {
        lock (MountGate)
            Volatile.Write(ref _mounts, Array.FindAll(_mounts, m => m != entry));
    }

    private sealed class MountEntry(IContentSource source, int priority, long order) : IDisposable
    {
        public IContentSource Source { get; } = source;
        public int Priority { get; } = priority;
        public long Order { get; } = order;

        public void Dispose() => Unmount(this);
    }
}
