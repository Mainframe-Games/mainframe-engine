using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace MainframeEngine;

/// <summary>One file a <see cref="PackWriter"/> packs: its path in the pack and the file on disk it comes from.</summary>
/// <param name="Path">The pack path, relative to the pack's root, '/'-separated (<c>Assets/UI/hud.rml</c>).</param>
/// <param name="SourceFile">The file to read.</param>
public readonly record struct PackFile(string Path, string SourceFile);

/// <summary>What <see cref="PackWriter.Write"/> wrote.</summary>
/// <param name="Entries">Files packed.</param>
/// <param name="Stored">Of those, entries kept as they are (media, or files Brotli could not shrink).</param>
/// <param name="ContentBytes">The files' total length.</param>
/// <param name="PackBytes">The pack's length.</param>
public readonly record struct PackWriteResult(int Entries, int Stored, long ContentBytes, long PackBytes);

/// <summary>
/// Writes <c>.mfpak</c> v1 packs (<see cref="PackFormat"/>; G19, ADR 0239). The export's stage step uses it, and tests and
/// benchmarks build packs with it. Deterministic: the same files give the same bytes (path order, no timestamps,
/// nonces from content), and a change to one file moves only its page, the pages after it and the index.
/// </summary>
public static class PackWriter
{
    /// <summary>Formats that are already compressed or are streamed with seeks: stored, never Brotli.</summary>
    private static readonly string[] StoredExtensions =
        [".ogg", ".wav", ".mp3", ".flac", ".opus", ".png", ".jpg", ".jpeg", ".webp", ".ktx2", ".basis", ".mp4", ".webm", ".mov", ".mkv", ".zip"];

    /// <summary>Brotli's quality: within a few percent of 11 at a fraction of its time.</summary>
    private const int BrotliQuality = 9;

    private const int BrotliWindow = 22;

    /// <summary>
    /// Writes the pack <paramref name="output"/> from <paramref name="files"/> (written beside it first, then moved over
    /// it, so a failed write never leaves a half pack).
    /// </summary>
    public static PackWriteResult Write(string output, IEnumerable<PackFile> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        ArgumentNullException.ThrowIfNull(files);
        var sorted = files.ToArray();
        Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        for (var i = 0; i < sorted.Length; i++)
        {
            if (!PackFormat.IsValidPath(sorted[i].Path))
                throw new ArgumentException($"'{sorted[i].Path}' is not a pack path (relative, '/'-separated, no '.' or '..').", nameof(files));
            if (i > 0 && sorted[i].Path == sorted[i - 1].Path)
                throw new ArgumentException($"'{sorted[i].Path}' is packed twice.", nameof(files));
        }

        var partial = output + ".partial";
        var entries = new Entry[sorted.Length];
        long contentBytes = 0;
        var stored = 0;
        try
        {
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(new byte[PackFormat.HeaderSize]);
                for (var i = 0; i < sorted.Length; i++)
                {
                    var content = File.ReadAllBytes(sorted[i].SourceFile); // disk-io: the export packs files from its stage
                    var (codec, bytes) = Encode(sorted[i].Path, content);
                    var offset = PackFormat.Place(stream.Position, sorted[i].Path, bytes.Length, codec);
                    Pad(stream, offset);
                    stream.Write(bytes);
                    entries[i] = new Entry(sorted[i].Path, offset, bytes.Length, content.Length, codec, Nonce(sorted[i].Path, content));
                    contentBytes += content.Length;
                    if (codec == PackCodec.Stored)
                        stored++;
                }

                var index = WriteIndex(entries);
                var compressedIndex = Brotli(index) ?? throw new InvalidOperationException("Brotli could not compress the pack index.");
                var indexOffset = PackFormat.Align(stream.Position, PackFormat.CompressedAlignment);
                Pad(stream, indexOffset);
                stream.Write(compressedIndex);

                var header = new PackFormat.Header
                {
                    Flags = PackOptions.None,
                    IndexOffset = indexOffset,
                    IndexStoredLength = compressedIndex.Length,
                    IndexLength = index.Length,
                    IndexNonce = SHA256.HashData(index)[..PackFormat.NonceSize],
                    KeyCheck = new byte[PackFormat.KeyCheckSize],
                };
                Span<byte> headerBytes = stackalloc byte[PackFormat.HeaderSize];
                header.Write(headerBytes);
                stream.Position = 0;
                stream.Write(headerBytes);
            }

            File.Move(partial, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }

        return new PackWriteResult(entries.Length, stored, contentBytes, new FileInfo(output).Length);
    }

    /// <summary>
    /// The files under <paramref name="root"/> a pack of a staged game holds: everything in <c>Assets/</c> and the
    /// <c>project.mfproj</c>, minus what <paramref name="keepLoose"/> (given pack paths) keeps beside the pack.
    /// </summary>
    public static IReadOnlyList<PackFile> GameFiles(string root, Func<string, bool>? keepLoose = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var files = new List<PackFile>();
        var assets = Path.Combine(root, ContentPaths.FolderName);
        if (Directory.Exists(assets)) // disk-io: the export packs its stage
        {
            foreach (var file in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories)) // disk-io: the export packs its stage
            {
                var path = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (keepLoose?.Invoke(path) != true)
                    files.Add(new PackFile(path, file));
            }
        }

        var project = Path.Combine(root, ProjectSettings.FileName);
        if (File.Exists(project)) // disk-io: the export packs its stage
            files.Add(new PackFile(ProjectSettings.FileName, project));
        files.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        return files;
    }

    /// <summary>Whether an entry at <paramref name="path"/> is stored rather than compressed, whatever Brotli would do.</summary>
    public static bool IsStoredFormat(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var extension = Path.GetExtension(path);
        foreach (var stored in StoredExtensions)
            if (extension.Equals(stored, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static (PackCodec Codec, byte[] Bytes) Encode(string path, byte[] content)
    {
        if (content.Length == 0 || IsStoredFormat(path))
            return (PackCodec.Stored, content);
        // Keep Brotli only when it saves at least 1/16 of the file: decoding costs more than reading a few bytes.
        return Brotli(content) is { } compressed && compressed.Length <= content.Length - content.Length / 16
            ? (PackCodec.Brotli, compressed)
            : (PackCodec.Stored, content);
    }

    private static byte[]? Brotli(byte[] content)
    {
        var buffer = new byte[BrotliEncoder.GetMaxCompressedLength(content.Length)];
        return BrotliEncoder.TryCompress(content, buffer, out var written, BrotliQuality, BrotliWindow) ? buffer[..written] : null;
    }

    // SHA-256(path ‖ content), first 12 bytes: unique per content, stable across builds (phase 3's CTR nonce).
    private static byte[] Nonce(string path, byte[] content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(path));
        hash.AppendData(content);
        return hash.GetHashAndReset()[..PackFormat.NonceSize];
    }

    private static byte[] WriteIndex(Entry[] entries)
    {
        using var index = new MemoryStream();
        Span<byte> scratch = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(scratch, entries.Length);
        index.Write(scratch[..4]);
        foreach (var entry in entries)
        {
            var path = Encoding.UTF8.GetBytes(entry.Path);
            if (path.Length > ushort.MaxValue)
                throw new ArgumentException($"The pack path '{entry.Path}' is longer than {ushort.MaxValue} bytes.");
            BinaryPrimitives.WriteUInt16LittleEndian(scratch, (ushort)path.Length);
            index.Write(scratch[..2]);
            index.Write(path);
            foreach (var value in (ReadOnlySpan<long>)[entry.Offset, entry.StoredLength, entry.Length])
            {
                BinaryPrimitives.WriteInt64LittleEndian(scratch, value);
                index.Write(scratch);
            }

            index.WriteByte((byte)entry.Codec);
            index.Write(entry.Nonce);
        }

        return index.ToArray();
    }

    private static void Pad(Stream stream, long offset)
    {
        Span<byte> zeros = stackalloc byte[4096];
        zeros.Clear();
        while (stream.Position < offset)
            stream.Write(zeros[..(int)Math.Min(zeros.Length, offset - stream.Position)]);
    }

    private readonly record struct Entry(string Path, long Offset, long StoredLength, long Length, PackCodec Codec, byte[] Nonce);
}
