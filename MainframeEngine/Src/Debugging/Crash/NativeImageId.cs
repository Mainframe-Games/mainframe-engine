using System.Buffers.Binary;
using System.Globalization;

namespace MainframeEngine;

/// <summary>
/// Reads a native image's build id from its headers (ADR 0260): the Mach-O <c>LC_UUID</c>, the ELF
/// <c>NT_GNU_BUILD_ID</c> note or the PE CodeView (<c>RSDS</c>) GUID and age. NativeAOT crash frames carry it, and
/// <c>mf-symbolicate</c> reads the same id from the symbol files, so symbols of another build are never used.
/// </summary>
/// <remarks>
/// The reader works on an image as the loader mapped it (<see cref="FromMemory"/>, addresses relative to the image base)
/// or on any other layout through <see cref="IImage"/> (files, tests). It never throws on malformed input: an image it
/// cannot read has no id.
/// </remarks>
public static class NativeImageId
{
    /// <summary>Byte access to an image, by offset from its start (the image base in memory, or a file offset).</summary>
    public interface IImage
    {
        /// <summary>Fills <paramref name="destination"/> from <paramref name="offset"/>; false when that range is not readable.</summary>
        bool TryRead(long offset, Span<byte> destination);

        /// <summary>Maps a virtual address of the image (ELF <c>p_vaddr</c>, PE RVA) to an offset for <see cref="TryRead"/>.</summary>
        long MapAddress(long virtualAddress);
    }

    private const uint MachO64 = 0xFEEDFACF;
    private const uint LcUuid = 0x1B;
    private const uint PtLoad = 1;
    private const uint PtNote = 4;
    private const uint NtGnuBuildId = 3;

    /// <summary>The id of the image mapped at <paramref name="imageBase"/> in this process, or null.</summary>
    public static unsafe string? FromMemory(nint imageBase)
    {
        if (imageBase == 0)
            return null;
        try
        {
            return Read(new MemoryImage(imageBase));
        }
        catch (Exception e) when (e is AccessViolationException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The id of <paramref name="image"/>, or null when it is not a Mach-O, ELF or PE image with one.</summary>
    public static string? Read(IImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Span<byte> magic = stackalloc byte[4];
        if (!image.TryRead(0, magic))
            return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(magic) == MachO64)
            return MachOUuid(image);
        if (magic[0] == 0x7F && magic[1] == (byte)'E' && magic[2] == (byte)'L' && magic[3] == (byte)'F')
            return ElfBuildId(image);
        if (magic[0] == (byte)'M' && magic[1] == (byte)'Z')
            return PeCodeViewId(image);
        return null;
    }

    /// <summary>The Mach-O UUID as <c>dwarfdump --uuid</c> prints it (upper-case <c>8-4-4-4-12</c>).</summary>
    public static string FormatUuid(ReadOnlySpan<byte> uuid)
    {
        var hex = Convert.ToHexString(uuid);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private static string? MachOUuid(IImage image)
    {
        Span<byte> header = stackalloc byte[32]; // mach_header_64
        if (!image.TryRead(0, header))
            return null;
        var commands = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        long offset = 32;
        Span<byte> command = stackalloc byte[24]; // cmd, cmdsize, uuid[16]
        for (var i = 0; i < commands && i < 4096; i++)
        {
            if (!image.TryRead(offset, command[..8]))
                return null;
            var cmd = BinaryPrimitives.ReadUInt32LittleEndian(command);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(command[4..]);
            if (cmd == LcUuid)
                return image.TryRead(offset, command) ? FormatUuid(command[8..24]) : null;
            if (size < 8)
                return null;
            offset += size;
        }

        return null;
    }

    private static string? ElfBuildId(IImage image)
    {
        Span<byte> header = stackalloc byte[64]; // Elf64_Ehdr
        if (!image.TryRead(0, header) || header[4] != 2 || header[5] != 1) // 64-bit, little-endian
            return null;
        var phoff = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[0x20..]);
        var phentsize = BinaryPrimitives.ReadUInt16LittleEndian(header[0x36..]);
        var phnum = BinaryPrimitives.ReadUInt16LittleEndian(header[0x38..]);
        if (phentsize < 56)
            return null;
        Span<byte> ph = stackalloc byte[56]; // Elf64_Phdr
        // The image's own first loadable segment: virtual addresses are relative to it (0 for a position-independent image).
        long? firstLoad = null;
        for (var i = 0; i < phnum && firstLoad is null; i++)
        {
            if (!image.TryRead(phoff + ((long)i * phentsize), ph))
                return null;
            if (BinaryPrimitives.ReadUInt32LittleEndian(ph) == PtLoad)
                firstLoad = (long)BinaryPrimitives.ReadUInt64LittleEndian(ph[0x10..]);
        }

        for (var i = 0; i < phnum; i++)
        {
            if (!image.TryRead(phoff + ((long)i * phentsize), ph) || BinaryPrimitives.ReadUInt32LittleEndian(ph) != PtNote)
                continue;
            var vaddr = (long)BinaryPrimitives.ReadUInt64LittleEndian(ph[0x10..]) - (firstLoad ?? 0);
            var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(ph[0x28..]);
            if (Notes(image, image.MapAddress(vaddr), size) is { } id)
                return id;
        }

        return null;
    }

    /// <summary>The GNU build id among the ELF notes at <paramref name="offset"/> (<paramref name="size"/> bytes), as lower-case hex.</summary>
    public static string? Notes(IImage image, long offset, long size)
    {
        ArgumentNullException.ThrowIfNull(image);
        Span<byte> note = stackalloc byte[12];
        Span<byte> owner = stackalloc byte[4];
        var end = offset + Math.Min(size, 1 << 16);
        while (offset + 12 <= end)
        {
            if (!image.TryRead(offset, note))
                return null;
            var nameSize = BinaryPrimitives.ReadUInt32LittleEndian(note);
            var descSize = BinaryPrimitives.ReadUInt32LittleEndian(note[4..]);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(note[8..]);
            if (nameSize > 256 || descSize > 256)
                return null;
            var name = offset + 12;
            var desc = name + Align4(nameSize);
            if (type == NtGnuBuildId && nameSize == 4 && descSize > 0)
            {
                var id = new byte[descSize];
                if (image.TryRead(name, owner) && owner.SequenceEqual("GNU\0"u8) && image.TryRead(desc, id))
                    return Convert.ToHexStringLower(id);
            }

            offset = desc + Align4(descSize);
        }

        return null;
    }

    private static string? PeCodeViewId(IImage image)
    {
        Span<byte> dos = stackalloc byte[64];
        if (!image.TryRead(0, dos))
            return null;
        var pe = BinaryPrimitives.ReadInt32LittleEndian(dos[0x3C..]);
        Span<byte> headers = stackalloc byte[24 + 112 + (16 * 8)]; // signature, COFF header, PE32+ optional header
        if (pe <= 0 || !image.TryRead(pe, headers) || !headers[..4].SequenceEqual("PE\0\0"u8))
            return null;
        var optional = headers[24..];
        if (BinaryPrimitives.ReadUInt16LittleEndian(optional) != 0x20B) // PE32+ only (x64, arm64)
            return null;
        var debug = optional[(112 + (6 * 8))..]; // DataDirectory[IMAGE_DIRECTORY_ENTRY_DEBUG]
        var rva = BinaryPrimitives.ReadUInt32LittleEndian(debug);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(debug[4..]);
        Span<byte> entry = stackalloc byte[28];
        Span<byte> codeView = stackalloc byte[24]; // "RSDS", GUID, age
        for (uint at = 0; at + 28 <= size && at < 28 * 64; at += 28)
        {
            if (!image.TryRead(image.MapAddress(rva + at), entry) || BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]) != 2) // CODEVIEW
                continue;
            var data = BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]); // AddressOfRawData
            if (image.TryRead(image.MapAddress(data), codeView) && codeView[..4].SequenceEqual("RSDS"u8))
            {
                var guid = new Guid(codeView[4..20]);
                var age = BinaryPrimitives.ReadUInt32LittleEndian(codeView[20..]);
                return guid.ToString("N", CultureInfo.InvariantCulture).ToUpperInvariant() + age.ToString("X", CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static long Align4(uint value) => (value + 3L) & ~3L;

    // An image as the loader mapped it: offsets and virtual addresses are both relative to the base.
    private sealed unsafe class MemoryImage(nint imageBase) : IImage
    {
        public bool TryRead(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset > int.MaxValue)
                return false;
            new ReadOnlySpan<byte>((byte*)imageBase + offset, destination.Length).CopyTo(destination);
            return true;
        }

        public long MapAddress(long virtualAddress) => virtualAddress;
    }

    /// <summary>An image held in memory as bytes (tests, small files), with virtual addresses equal to offsets.</summary>
    public sealed class Bytes(byte[] data) : IImage
    {
        public bool TryRead(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset + destination.Length > data.Length)
                return false;
            data.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return true;
        }

        public long MapAddress(long virtualAddress) => virtualAddress;
    }

}
