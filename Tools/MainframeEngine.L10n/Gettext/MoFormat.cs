using System.Buffers.Binary;
using System.Text;

namespace MainframeEngine.L10n.Gettext;

/// <summary>One message of a compiled <c>.mo</c> catalog.</summary>
/// <param name="Key">The msgid, prefixed with <c>context\u0004</c> when it has a context.</param>
/// <param name="IdPlural">The msgid_plural, or null.</param>
/// <param name="Translations">One translation, or one per plural form.</param>
internal sealed record MoMessage(string Key, string? IdPlural, IReadOnlyList<string> Translations);

/// <summary>
/// GNU <c>.mo</c> writer and reader. The writer reproduces <c>msgfmt</c> byte for byte (revision 0, little-endian,
/// messages sorted by msgid bytes, the <c>hashpjw</c> hash table with GNU's sizing and double hashing, strings
/// NUL-terminated after the tables), so compiled catalogs are identical whether built by the engine or by gettext.
/// </summary>
internal static class MoFormat
{
    public const uint Magic = 0x950412de;
    private const int HeaderSize = 28;

    /// <summary>
    /// The messages msgfmt would compile from <paramref name="catalog"/>: not obsolete, first form translated, not fuzzy
    /// (unless <paramref name="useFuzzy"/>; a fuzzy header is always kept).
    /// </summary>
    public static List<MoMessage> SelectMessages(PoCatalog catalog, bool useFuzzy = false)
    {
        var messages = new List<MoMessage>();
        foreach (var entry in catalog.Entries)
        {
            if (entry.Obsolete || !entry.HasTranslation)
                continue;
            if (entry.IsFuzzy && !useFuzzy && !entry.IsHeader)
                continue;
            messages.Add(new MoMessage(entry.Key, entry.IdPlural, [.. entry.Translations]));
        }

        return messages;
    }

    public static byte[] Write(PoCatalog catalog, bool useFuzzy = false) => Write(SelectMessages(catalog, useFuzzy));

    public static byte[] Write(IReadOnlyList<MoMessage> messages) => Write(messages, hashTableSize: null);

    /// <summary>
    /// Writes with an explicit hash table size instead of <see cref="HashTableSize"/> (null). Only for comparing with
    /// older msgfmt builds whose sizing differs (<see cref="LegacyHashTableSize"/>); the table must have room for
    /// every message.
    /// </summary>
    public static byte[] Write(IReadOnlyList<MoMessage> messages, int? hashTableSize)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (hashTableSize is { } explicitSize && (explicitSize <= messages.Count || explicitSize < 3))
            throw new ArgumentOutOfRangeException(nameof(hashTableSize), explicitSize, "The hash table needs more slots than messages (and at least 3).");
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var items = new List<(byte[] Original, byte[] Translation)>(messages.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in messages)
        {
            if (!seen.Add(m.Key))
                throw new InvalidDataException($"duplicate message \"{m.Key}\"");
            var original = m.IdPlural is null ? m.Key : m.Key + '\0' + m.IdPlural;
            items.Add((utf8.GetBytes(original), utf8.GetBytes(string.Join('\0', m.Translations))));
        }

        // msgfmt sorts by strcmp on the msgid: bytes up to the first NUL (the plural part never decides).
        items.Sort(static (a, b) => CompareCString(a.Original, b.Original));

        var count = items.Count;
        var hashSize = hashTableSize ?? HashTableSize(count);
        var originalsTable = HeaderSize;
        var translationsTable = originalsTable + 8 * count;
        var hashTable = translationsTable + 8 * count;
        var stringsStart = hashTable + 4 * hashSize;

        var size = stringsStart;
        foreach (var (original, translation) in items)
            size += original.Length + 1 + translation.Length + 1;
        var data = new byte[size];
        var span = data.AsSpan();

        WriteUInt32(span, 0, Magic);
        WriteUInt32(span, 4, 0);
        WriteUInt32(span, 8, (uint)count);
        WriteUInt32(span, 12, (uint)originalsTable);
        WriteUInt32(span, 16, (uint)translationsTable);
        WriteUInt32(span, 20, (uint)hashSize);
        WriteUInt32(span, 24, (uint)hashTable);

        var offset = stringsStart;
        for (var i = 0; i < count; i++)
        {
            var original = items[i].Original;
            WriteUInt32(span, originalsTable + 8 * i, (uint)original.Length);
            WriteUInt32(span, originalsTable + 8 * i + 4, (uint)offset);
            original.CopyTo(span[offset..]);
            offset += original.Length + 1;
        }

        for (var i = 0; i < count; i++)
        {
            var translation = items[i].Translation;
            WriteUInt32(span, translationsTable + 8 * i, (uint)translation.Length);
            WriteUInt32(span, translationsTable + 8 * i + 4, (uint)offset);
            translation.CopyTo(span[offset..]);
            offset += translation.Length + 1;
        }

        if (hashSize > 0)
        {
            var table = new uint[hashSize];
            for (var i = 0; i < count; i++)
            {
                var hash = HashPjw(items[i].Original);
                var index = hash % (uint)hashSize;
                if (table[index] != 0)
                {
                    var increment = 1 + hash % (uint)(hashSize - 2);
                    do
                    {
                        if (index >= hashSize - increment)
                            index -= (uint)hashSize - increment;
                        else
                            index += increment;
                    }
                    while (table[index] != 0);
                }

                table[index] = (uint)i + 1;
            }

            for (var i = 0; i < hashSize; i++)
                WriteUInt32(span, hashTable + 4 * i, table[i]);
        }

        return data;
    }

    /// <summary>Reads a <c>.mo</c> file (either byte order) and checks that its hash table, if any, finds every message.</summary>
    public static List<MoMessage> Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new InvalidDataException("not a .mo file (too short)");
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        bool bigEndian;
        if (magic == Magic)
            bigEndian = false;
        else if (BinaryPrimitives.ReverseEndianness(magic) == Magic)
            bigEndian = true;
        else
            throw new InvalidDataException("not a .mo file (bad magic)");

        var revision = U32(data, 4, bigEndian);
        if (revision >> 16 != 0)
            throw new InvalidDataException($"unsupported .mo revision {revision >> 16}");
        var count = checked((int)U32(data, 8, bigEndian));
        var originals = checked((int)U32(data, 12, bigEndian));
        var translations = checked((int)U32(data, 16, bigEndian));
        var hashSize = checked((int)U32(data, 20, bigEndian));
        var hashOffset = checked((int)U32(data, 24, bigEndian));

        var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
        var messages = new List<MoMessage>(count);
        var keys = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            var original = Slice(data, (int)U32(data, originals + 8 * i, bigEndian), (int)U32(data, originals + 8 * i + 4, bigEndian));
            var translation = Slice(data, (int)U32(data, translations + 8 * i, bigEndian), (int)U32(data, translations + 8 * i + 4, bigEndian));
            keys.Add(original.ToArray());
            var id = utf8.GetString(original);
            var nul = id.IndexOf('\0', StringComparison.Ordinal);
            messages.Add(new MoMessage(
                nul < 0 ? id : id[..nul],
                nul < 0 ? null : id[(nul + 1)..],
                utf8.GetString(translation).Split('\0')));
        }

        if (hashSize > 2)
        {
            for (var i = 0; i < count; i++)
            {
                if (FindByHash(data, bigEndian, originals, hashOffset, hashSize, keys[i]) != i)
                    throw new InvalidDataException($"hash table does not find message {i} (\"{messages[i].Key}\")");
            }
        }

        return messages;
    }

    // Walks the probe sequence the way libintl does, comparing keys, and returns the message index (or -1).
    private static int FindByHash(ReadOnlySpan<byte> data, bool bigEndian, int originals, int tableOffset, int size, byte[] key)
    {
        var hash = HashPjw(key);
        var index = hash % (uint)size;
        var increment = 1 + hash % (uint)(size - 2);
        for (var probes = 0; probes < size; probes++)
        {
            var slot = U32(data, tableOffset + 4 * (int)index, bigEndian);
            if (slot == 0)
                return -1;
            var message = (int)slot - 1;
            var length = (int)U32(data, originals + 8 * message, bigEndian);
            var at = (int)U32(data, originals + 8 * message + 4, bigEndian);
            if (CompareCString(Slice(data, length, at), key) == 0)
                return message;
            if (index >= size - increment)
                index -= (uint)size - increment;
            else
                index += increment;
        }

        return -1;
    }

    private static uint U32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        if (offset < 0 || offset + 4 > data.Length)
            throw new InvalidDataException("table entry out of range");
        return bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[offset..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, int length, int offset)
    {
        if (offset < 0 || length < 0 || offset + length > data.Length)
            throw new InvalidDataException("string table entry out of range");
        return data.Slice(offset, length);
    }

    /// <summary>
    /// GNU's hash table size: the next odd prime at or above 4n/3 (gettext's trial division), at least 3; 0 for no
    /// messages. Verified against msgfmt for 1..120 messages.
    /// </summary>
    public static int HashTableSize(int count)
    {
        if (count == 0)
            return 0;
        var size = NextPrime((uint)(count * 4 / 3));
        return (int)Math.Max(size, 3u);
    }

    /// <summary>
    /// The size gettext 0.21 and older choose (Ubuntu 24.04 ships 0.21): their <c>is_prime</c> rejects 3, so a
    /// two-message catalog gets a 5-slot table instead of 3. Every other count matches <see cref="HashTableSize"/>.
    /// </summary>
    public static int LegacyHashTableSize(int count) => count == 2 ? 5 : HashTableSize(count);

    // gettext's next_prime/is_prime: odd seed, trial division by odd numbers up to the square root.
    private static uint NextPrime(uint seed)
    {
        seed |= 1;
        while (!IsPrime(seed))
            seed += 2;
        return seed;
    }

    private static bool IsPrime(uint candidate)
    {
        ulong divisor = 3;
        var square = divisor * divisor;
        while (square < candidate && candidate % divisor != 0)
        {
            divisor++;
            square += 4 * divisor;
            divisor++;
        }

        return candidate % divisor != 0 || candidate == divisor;
    }

    /// <summary>
    /// gettext's <c>hash_string</c> (hashpjw) over the msgid bytes up to the first NUL. gettext folds only bits 28–31
    /// (<c>0xf &lt;&lt; 28</c>); a carry out of bit 31 stays above the 32 bits it stores, so 32-bit wrapping arithmetic
    /// gives the same result.
    /// </summary>
    public static uint HashPjw(ReadOnlySpan<byte> key)
    {
        uint hash = 0;
        foreach (var b in key)
        {
            if (b == 0)
                break;
            hash = unchecked((hash << 4) + b);
            var g = hash & 0xF000_0000u;
            if (g != 0)
            {
                hash ^= g >> 24;
                hash ^= g;
            }
        }

        return hash;
    }

    private static int CompareCString(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var na = a.IndexOf((byte)0);
        var nb = b.IndexOf((byte)0);
        return (na < 0 ? a : a[..na]).SequenceCompareTo(nb < 0 ? b : b[..nb]);
    }

    private static void WriteUInt32(Span<byte> span, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], value);
}
