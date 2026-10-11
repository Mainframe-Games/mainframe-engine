namespace MainframeEngine;

/// <summary>
/// Crash- and power-loss-safe file replacement: write <c>path.tmp</c>, flush it to disk (<c>fsync</c> /
/// <c>FlushFileBuffers</c>), rotate <c>path.bak1</c> … <c>path.bakN</c>, then replace the target (keeping the old file as
/// <c>.bak1</c>). At every moment at least one of the target, <c>.bak1</c> or a fully flushed <c>.tmp</c> is a complete
/// copy, so a reader that validates its data can recover by trying <see cref="RecoveryCandidates"/> in order.
/// </summary>
/// <remarks>
/// On Windows the replace is <c>ReplaceFileW</c> (atomic). On Unix it is a hard link plus a rename, not one step. .NET
/// has no API to <c>fsync</c> a directory, so a rename can still be lost on power failure on some file systems; the
/// recovery order covers that too.
/// </remarks>
public static class DurableFile
{
    /// <summary>The most backups a file keeps.</summary>
    public const int MaxBackups = 16;

    /// <summary>The temporary file a write goes through: <c>path.tmp</c>.</summary>
    public static string TempPath(string path) => path + ".tmp";

    /// <summary>The <paramref name="n"/>-th backup (1 = the previous version): <c>path.bak{n}</c>.</summary>
    public static string BackupPath(string path, int n) => path + ".bak" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/> durably, keeping the previous <paramref name="backups"/>
    /// versions (0 = none). Creates the folder. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>
    /// on failure; the target is then unchanged.
    /// </summary>
    public static void Write(string path, ReadOnlySpan<byte> bytes, int backups = 0) => Write(path, bytes, backups, FileOps.Real);

    /// <summary>
    /// The copies a reader tries, in order, when the target is missing or invalid: the target, its <c>.tmp</c> (complete
    /// when a crash hit after the flush), then <c>.bak1</c> … <c>.bak{backups}</c>. Only existing files are returned.
    /// </summary>
    public static IReadOnlyList<string> RecoveryCandidates(string path, int backups)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var result = new List<string>(backups + 2);
        if (File.Exists(path))
            result.Add(path);
        var temp = TempPath(path);
        if (File.Exists(temp))
            result.Add(temp);
        for (var i = 1; i <= Math.Clamp(backups, 0, MaxBackups); i++)
        {
            var backup = BackupPath(path, i);
            if (File.Exists(backup))
                result.Add(backup);
        }

        return result;
    }

    /// <summary>
    /// Makes <paramref name="source"/> (a valid copy found by <see cref="RecoveryCandidates"/>) the target again, durably.
    /// A no-op when it already is the target.
    /// </summary>
    public static void Restore(string path, string source, int backups = 0)
    {
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(source), StringComparison.Ordinal))
            return;
        Write(path, File.ReadAllBytes(source), backups);
    }

    /// <summary>
    /// Moves a damaged <paramref name="path"/> aside as <c>path.corrupt-{utc}</c> (never deleted, so a player can send it
    /// to support). Returns the new path, or null when the file is missing or cannot be moved.
    /// </summary>
    public static string? QuarantineCorrupt(string path)
    {
        if (!File.Exists(path))
            return null;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var target = $"{path}.corrupt-{stamp}";
        for (var i = 1; File.Exists(target); i++)
            target = $"{path}.corrupt-{stamp}-{i}";
        try
        {
            File.Move(path, target);
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void Write(string path, ReadOnlySpan<byte> bytes, int backups, IDurableFileOps ops)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegative(backups);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(backups, MaxBackups);
        var full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) is { Length: > 0 } directory)
            ops.CreateDirectory(directory);

        // 1. The new content, flushed to disk before anything else moves.
        var temp = TempPath(full);
        ops.WriteFlushed(temp, bytes);

        // 2. First save: nothing to replace.
        if (!ops.Exists(full))
        {
            ops.Move(temp, full, overwrite: true);
            return;
        }

        if (backups == 0)
        {
            ops.Move(temp, full, overwrite: true);
            return;
        }

        // 3. Rotate .bak{N-1} → .bak{N} … .bak1 → .bak2 (the oldest falls off), then replace keeping the old file as .bak1.
        var oldest = BackupPath(full, backups);
        if (ops.Exists(oldest))
            ops.Delete(oldest);
        for (var i = backups - 1; i >= 1; i--)
        {
            var from = BackupPath(full, i);
            if (ops.Exists(from))
                ops.Move(from, BackupPath(full, i + 1), overwrite: true);
        }

        ops.Replace(temp, full, BackupPath(full, 1));
    }

    /// <summary>The file-system steps of a durable write (a test shim simulates a crash between any two).</summary>
    internal interface IDurableFileOps
    {
        void CreateDirectory(string path);
        void WriteFlushed(string path, ReadOnlySpan<byte> bytes);
        bool Exists(string path);
        void Move(string from, string to, bool overwrite);
        void Replace(string source, string destination, string backup);
        void Delete(string path);
    }

    internal sealed class FileOps : IDurableFileOps
    {
        public static readonly FileOps Real = new();

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public void WriteFlushed(string path, ReadOnlySpan<byte> bytes)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.None);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        public bool Exists(string path) => File.Exists(path);

        public void Move(string from, string to, bool overwrite) => File.Move(from, to, overwrite);

        public void Replace(string source, string destination, string backup) =>
            File.Replace(source, destination, backup, ignoreMetadataErrors: true);

        public void Delete(string path) => File.Delete(path);
    }
}
