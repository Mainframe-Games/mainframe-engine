using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MainframeEngine;

/// <summary>How a pack entry's bytes are stored (<c>.mfpak</c> v1).</summary>
public enum PackCodec : byte
{
    /// <summary>The bytes as they are: seekable, 16 KB aligned (audio, images, video).</summary>
    Stored = 0,

    /// <summary>Brotli, decoded whole when the entry is opened (text and data files).</summary>
    Brotli = 1,
}

/// <summary>The options bits in a pack's header.</summary>
[Flags]
public enum PackOptions : ushort
{
    None = 0,

    /// <summary>The entries and the index are encrypted (G19 phase 3).</summary>
    Encrypted = 1,
}

/// <summary>
/// The <c>.mfpak</c> v1 layout (G19, docs/design/asset-pipeline.md#packs): a 64-byte header, the entries in path order laid
/// out in 1 MB pages, then the Brotli-compressed index. Little-endian throughout.
/// </summary>
/// <remarks>
/// <para>Header: magic <c>MFPK</c>, version (u16), <see cref="PackOptions"/> (u16), index offset, stored length and length
/// (u64 each), the index nonce (12 bytes), 4 reserved bytes, the key check (16 bytes).</para>
/// <para>Index, before compression: the entry count (u32), then per entry its path (u16 UTF-8 byte count, the bytes),
/// offset, stored length and length (u64 each), <see cref="PackCodec"/> (u8) and nonce (12 bytes).</para>
/// <para>Anchors (ADR 0247): an entry of a page or more, and about one entry per <see cref="AnchorSpacing"/> bytes chosen by
/// its path's hash and its size alone (<see cref="IsAnchor"/>), starts on a <see cref="PageSize"/> boundary. A changed,
/// added or removed asset then re-lays out only the entries up to the next anchor; everything after it moves by whole
/// pages, so SteamPipe's ~1 MB chunks still match and a patch stays near the size of what changed.</para>
/// </remarks>
public static class PackFormat
{
    /// <summary>The pack an export writes beside the executable, which <c>GameHost</c> mounts.</summary>
    public const string FileName = "Assets.mfpak";

    /// <summary>The format version this engine writes and reads.</summary>
    public const ushort Version = 1;

    public const int HeaderSize = 64;

    /// <summary>The page entries are laid out in: SteamPipe's chunk size.</summary>
    public const int PageSize = 1 << 20;

    /// <summary>Where stored entries start (seekable media; mobile memory maps them).</summary>
    public const int StoredAlignment = 16 * 1024;

    /// <summary>Where compressed entries start.</summary>
    public const int CompressedAlignment = 16;

    public const int NonceSize = 12;

    public const int KeyCheckSize = 16;

    /// <summary>The last write time every packed file reports (a pack's content never changes once mounted).</summary>
    public static readonly DateTime LastWriteUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The first four bytes of every pack.</summary>
    public static ReadOnlySpan<byte> Magic => "MFPK"u8;

    /// <summary>About how many stored bytes lie between two anchors (ADR 0247): at most this much re-laid out by one change, ~3 % padding.</summary>
    public const long AnchorSpacing = 16L * PageSize;

    /// <summary>
    /// Where an entry of <paramref name="length"/> stored bytes goes when the previous one ended at <paramref name="end"/>:
    /// a page boundary for an anchor (<see cref="IsAnchor"/>), else the codec's alignment.
    /// </summary>
    public static long Place(long end, string path, long length, PackCodec codec)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(end);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0)
            return end;
        return IsAnchor(path, length) ? Align(end, PageSize) : Align(end, codec == PackCodec.Stored ? StoredAlignment : CompressedAlignment);
    }

    /// <summary>
    /// Whether an entry starts a page: one of a page or more always; a smaller one with a chance of
    /// <paramref name="length"/> / <see cref="AnchorSpacing"/>, decided by the first 4 bytes of SHA-256 of its path. It
    /// depends on nothing before it, which is what stops a change from moving the rest of the pack.
    /// </summary>
    public static bool IsAnchor(string path, long length)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (length >= PageSize)
            return true;
        if (length <= 0)
            return false;
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(path), hash);
        return (ulong)BinaryPrimitives.ReadUInt32LittleEndian(hash) * (ulong)AnchorSpacing < (ulong)length << 32;
    }

    /// <summary><paramref name="value"/> rounded up to a multiple of <paramref name="alignment"/> (a power of two).</summary>
    public static long Align(long value, long alignment) => (value + alignment - 1) & ~(alignment - 1);

    /// <summary>Whether a path is a valid entry path: relative, '/'-separated, no empty, <c>.</c> or <c>..</c> segment.</summary>
    public static bool IsValidPath(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty || path.Contains('\\') || path.Contains('\0'))
            return false;
        foreach (var range in path.Split('/'))
        {
            var segment = path[range];
            if (segment.IsEmpty || segment is "." or "..")
                return false;
        }

        return true;
    }

    /// <summary>The header fields.</summary>
    internal struct Header
    {
        public PackOptions Flags;
        public long IndexOffset;
        public long IndexStoredLength;
        public long IndexLength;
        public byte[] IndexNonce;
        public byte[] KeyCheck;

        public readonly void Write(Span<byte> destination)
        {
            destination[..HeaderSize].Clear();
            Magic.CopyTo(destination);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], Version);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], (ushort)Flags);
            BinaryPrimitives.WriteInt64LittleEndian(destination[8..], IndexOffset);
            BinaryPrimitives.WriteInt64LittleEndian(destination[16..], IndexStoredLength);
            BinaryPrimitives.WriteInt64LittleEndian(destination[24..], IndexLength);
            IndexNonce.AsSpan(0, NonceSize).CopyTo(destination[32..]);
            KeyCheck.AsSpan(0, KeyCheckSize).CopyTo(destination[48..]);
        }

        public static Header Read(ReadOnlySpan<byte> source, string name)
        {
            if (source.Length < HeaderSize || !source[..4].SequenceEqual(Magic))
                throw new InvalidDataException($"'{name}' is not a Mainframe pack (no MFPK header).");
            var version = BinaryPrimitives.ReadUInt16LittleEndian(source[4..]);
            if (version != Version)
                throw new InvalidDataException(version > Version
                    ? $"'{name}' is pack format {version}, newer than this engine reads ({Version}): export the game with this engine version."
                    : $"'{name}' has an unknown pack format {version}.");
            return new Header
            {
                Flags = (PackOptions)BinaryPrimitives.ReadUInt16LittleEndian(source[6..]),
                IndexOffset = BinaryPrimitives.ReadInt64LittleEndian(source[8..]),
                IndexStoredLength = BinaryPrimitives.ReadInt64LittleEndian(source[16..]),
                IndexLength = BinaryPrimitives.ReadInt64LittleEndian(source[24..]),
                IndexNonce = source.Slice(32, NonceSize).ToArray(),
                KeyCheck = source.Slice(48, KeyCheckSize).ToArray(),
            };
        }
    }
}
