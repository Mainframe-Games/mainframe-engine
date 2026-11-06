using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MainframeEngine;

/// <summary>One file in a pack (<see cref="PackFormat"/>).</summary>
/// <param name="Path">Its path, relative to the pack's root, '/'-separated.</param>
/// <param name="Offset">Where its bytes start in the pack.</param>
/// <param name="StoredLength">How many bytes it takes in the pack.</param>
/// <param name="Length">Its length once decoded.</param>
/// <param name="Codec">How it is stored.</param>
public readonly record struct PackEntry(string Path, long Offset, long StoredLength, long Length, PackCodec Codec);

/// <summary>
/// An open <c>.mfpak</c> pack (G19, ADR 0239): the index in memory and one read-only file handle. Thread-safe: entries are
/// read with positional reads (<see cref="RandomAccess"/>), so the loader worker and the audio streamer read at once.
/// </summary>
public sealed class PackReader : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly PackEntry[] _entries;
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _directoryLookup;

    private PackReader(string path, SafeFileHandle handle, PackEntry[] entries, HashSet<string> directories)
    {
        FilePath = path;
        Name = System.IO.Path.GetFileName(path);
        _handle = handle;
        _entries = entries;
        var lookup = new Dictionary<string, int>(entries.Length, StringComparer.Ordinal);
        for (var i = 0; i < entries.Length; i++)
            lookup.Add(entries[i].Path, i);
        _lookup = lookup.GetAlternateLookup<ReadOnlySpan<char>>();
        Directories = directories;
        _directoryLookup = directories.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>The pack file.</summary>
    public string FilePath { get; }

    /// <summary>Its file name (errors name it).</summary>
    public string Name { get; }

    /// <summary>The entries in path order (ordinal).</summary>
    public IReadOnlyList<PackEntry> Entries => _entries;

    /// <summary>Every directory that holds an entry at any depth, "" (the root) included.</summary>
    public IReadOnlySet<string> Directories { get; }

    /// <summary>
    /// Opens a pack and reads its index. Throws <see cref="InvalidDataException"/> naming the pack when it is not one, is
    /// another version, is encrypted (G19 phase 3) or is damaged (an entry or the index outside the file).
    /// </summary>
    public static PackReader Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = System.IO.Path.GetFullPath(path);
        var name = System.IO.Path.GetFileName(path);
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try
        {
            var fileLength = RandomAccess.GetLength(handle);
            Span<byte> headerBytes = stackalloc byte[PackFormat.HeaderSize];
            if (fileLength < PackFormat.HeaderSize || RandomAccess.Read(handle, headerBytes, 0) != PackFormat.HeaderSize)
                throw new InvalidDataException($"'{name}' is not a Mainframe pack (shorter than its header).");
            var header = PackFormat.Header.Read(headerBytes, name);
            if ((header.Flags & PackOptions.Encrypted) != 0)
                throw new InvalidDataException($"'{name}' is encrypted, and this engine build reads only unencrypted packs.");
            if (header.IndexOffset < PackFormat.HeaderSize || header.IndexStoredLength <= 0 || header.IndexOffset + header.IndexStoredLength > fileLength
                || header.IndexLength is < 4 or > int.MaxValue)
                throw Damaged(name, "its index lies outside the file");

            var stored = new byte[header.IndexStoredLength];
            ReadExactly(handle, stored, header.IndexOffset, name);
            var index = new byte[header.IndexLength];
            if (!BrotliDecoder.TryDecompress(stored, index, out var written) || written != index.Length)
                throw Damaged(name, "its index does not decompress");
            var (entries, directories) = ParseIndex(index, fileLength, name);
            return new PackReader(path, handle, entries, directories);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>The entry at <paramref name="path"/>, if the pack has one. 0 B.</summary>
    public bool TryGetEntry(ReadOnlySpan<char> path, out PackEntry entry)
    {
        if (_lookup.TryGetValue(path, out var i))
        {
            entry = _entries[i];
            return true;
        }

        entry = default;
        return false;
    }

    /// <summary>Whether the pack holds a file at <paramref name="path"/>. 0 B.</summary>
    public bool FileExists(ReadOnlySpan<char> path) => _lookup.ContainsKey(path);

    /// <summary>Whether a directory holds an entry at any depth ("" is the root). 0 B.</summary>
    public bool DirectoryExists(ReadOnlySpan<char> path) => _directoryLookup.Contains(path);

    /// <summary>
    /// A read-only, seekable stream over an entry. Stored entries read straight from the pack (0 B per read); compressed
    /// ones are decoded whole here and served from memory.
    /// </summary>
    public Stream OpenEntry(in PackEntry entry)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (entry.Codec == PackCodec.Stored)
            return new PackEntryStream(_handle, entry.Offset, entry.Length);

        var stored = new byte[entry.StoredLength];
        ReadExactly(_handle, stored, entry.Offset, Name);
        var bytes = new byte[entry.Length];
        if (!BrotliDecoder.TryDecompress(stored, bytes, out var written) || written != bytes.Length)
            throw Damaged(Name, $"'{entry.Path}' does not decompress");
        return new MemoryStream(bytes, writable: false);
    }

    /// <summary>The files in <paramref name="directory"/> ("" for the root), directly or at any depth, in path order.</summary>
    public IEnumerable<string> EnumerateFiles(string directory, bool recursive)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var prefix = directory.Length == 0 ? "" : directory + "/";
        for (var i = LowerBound(prefix); i < _entries.Length && _entries[i].Path.StartsWith(prefix, StringComparison.Ordinal); i++)
        {
            var path = _entries[i].Path;
            if (recursive || path.IndexOf('/', prefix.Length) < 0)
                yield return path;
        }
    }

    /// <summary>The directories in <paramref name="directory"/>, like <see cref="EnumerateFiles"/>.</summary>
    public IEnumerable<string> EnumerateDirectories(string directory, bool recursive)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var prefix = directory.Length == 0 ? "" : directory + "/";
        var found = new List<string>();
        foreach (var d in Directories)
        {
            if (d.Length > prefix.Length && d.StartsWith(prefix, StringComparison.Ordinal) && (recursive || d.IndexOf('/', prefix.Length) < 0))
                found.Add(d);
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    public void Dispose() => _handle.Dispose();

    private int LowerBound(string prefix)
    {
        int lo = 0, hi = _entries.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (string.CompareOrdinal(_entries[mid].Path, prefix) < 0)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private static (PackEntry[] Entries, HashSet<string> Directories) ParseIndex(ReadOnlySpan<byte> index, long fileLength, string name)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(index);
        const int fixedSize = 2 + 8 * 3 + 1 + PackFormat.NonceSize;
        if (count < 0 || (long)count * fixedSize > index.Length - 4)
            throw Damaged(name, "its index is truncated");
        var entries = new PackEntry[count];
        var directories = new HashSet<string>(StringComparer.Ordinal) { "" };
        var at = 4;
        for (var i = 0; i < count; i++)
        {
            if (index.Length - at < 2)
                throw Damaged(name, "its index is truncated");
            var pathLength = BinaryPrimitives.ReadUInt16LittleEndian(index[at..]);
            at += 2;
            if (index.Length - at < pathLength + fixedSize - 2)
                throw Damaged(name, "its index is truncated");
            var path = Encoding.UTF8.GetString(index.Slice(at, pathLength));
            at += pathLength;
            var offset = BinaryPrimitives.ReadInt64LittleEndian(index[at..]);
            var storedLength = BinaryPrimitives.ReadInt64LittleEndian(index[(at + 8)..]);
            var length = BinaryPrimitives.ReadInt64LittleEndian(index[(at + 16)..]);
            var codec = (PackCodec)index[at + 24];
            at += 25 + PackFormat.NonceSize;

            if (!PackFormat.IsValidPath(path))
                throw Damaged(name, $"'{path}' is not a pack path");
            if (i > 0 && string.CompareOrdinal(entries[i - 1].Path, path) >= 0)
                throw Damaged(name, $"its index is not in path order at '{path}'");
            if (codec is not (PackCodec.Stored or PackCodec.Brotli) || (offset < PackFormat.HeaderSize && storedLength > 0) || storedLength < 0
                || offset + storedLength > fileLength || length < 0 || (codec == PackCodec.Stored && length != storedLength)
                || (codec == PackCodec.Brotli && length > Array.MaxLength))
                throw Damaged(name, $"the entry '{path}' lies outside the file or has a bad codec");
            entries[i] = new PackEntry(path, offset, storedLength, length, codec);
            for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
                if (!directories.Add(path[..slash]))
                    break; // its parents are in already
        }

        return (entries, directories);
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> buffer, long offset, string name)
    {
        while (!buffer.IsEmpty)
        {
            var read = RandomAccess.Read(handle, buffer, offset);
            if (read <= 0)
                throw Damaged(name, "it ends early");
            buffer = buffer[read..];
            offset += read;
        }
    }

    private static InvalidDataException Damaged(string name, string why) =>
        new($"The pack '{name}' is damaged: {why}. Export the game again.");
}

/// <summary>
/// A stored pack entry as a read-only, seekable stream over the pack's shared handle. Reads are positional, so streams
/// over one pack read from any thread at once; they allocate nothing.
/// </summary>
internal sealed class PackEntryStream(SafeFileHandle handle, long start, long length) : Stream
{
    private long _position;

    public override bool CanRead => !handle.IsClosed;

    public override bool CanSeek => !handle.IsClosed;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var remaining = length - _position;
        if (remaining <= 0 || buffer.IsEmpty)
            return 0;
        if (buffer.Length > remaining)
            buffer = buffer[..(int)remaining];
        var read = RandomAccess.Read(handle, buffer, start + _position);
        _position += read;
        return read;
    }

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1 ? one[0] : -1;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
            throw new IOException("Cannot seek before the start of a pack entry.");
        _position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
