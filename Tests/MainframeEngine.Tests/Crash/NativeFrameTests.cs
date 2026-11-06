using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using MainframeEngine.Symbolicate;

namespace MainframeEngine.Tests.Crash;

/// <summary>
/// NativeAOT crash frames (G19 phase 4, ADR 0260): the image build id read from Mach-O, ELF and PE headers (in memory
/// and in symbol files), and <c>mf-symbolicate</c> resolving native frames through the platform's symbolizers, whose
/// command lines and output are checked against a fake runner (CI machines need not have them).
/// </summary>
public sealed class NativeFrameTests : IDisposable
{
    private static readonly byte[] Uuid = [.. Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i))];
    private static readonly byte[] BuildId = [.. Enumerable.Range(0, 20).Select(i => (byte)(i * 7))];
    private static readonly Guid PdbGuid = new("0b7c4f44-9a1e-4f7b-8d3a-6a2bb1d0c9e5");

    private readonly string _folder = Directory.CreateTempSubdirectory("mf-native-symbols").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // ── Synthetic images ─────────────────────────────────────────────────────────────────────────────────────────

    // mach_header_64 (arm64) + LC_SEGMENT_64 __TEXT at 0x100000000 + LC_UUID.
    private static byte[] MachO()
    {
        var data = new byte[32 + 72 + 24];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x0100000C);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 2); // ncmds
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 72 + 24);
        var segment = data.AsSpan(32);
        BinaryPrimitives.WriteUInt32LittleEndian(segment, 0x19);
        BinaryPrimitives.WriteUInt32LittleEndian(segment[4..], 72);
        Encoding.ASCII.GetBytes("__TEXT").CopyTo(segment[8..]);
        BinaryPrimitives.WriteUInt64LittleEndian(segment[24..], 0x100000000);
        var uuid = data.AsSpan(32 + 72);
        BinaryPrimitives.WriteUInt32LittleEndian(uuid, 0x1B);
        BinaryPrimitives.WriteUInt32LittleEndian(uuid[4..], 24);
        Uuid.CopyTo(uuid[8..]);
        return data;
    }

    // Elf64 header, PT_LOAD at vaddr 0 and PT_NOTE (program headers), and an SHT_NOTE section header, over one GNU
    // build-id note at offset 0x100 (vaddr == offset, as in the first segment of a position-independent image).
    private static byte[] Elf()
    {
        var data = new byte[0x200];
        data[0] = 0x7F;
        "ELF"u8.CopyTo(data.AsSpan(1));
        data[4] = 2; // 64-bit
        data[5] = 1; // little-endian
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x20), 0x40); // e_phoff
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x28), 0x180); // e_shoff
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x36), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x38), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x3A), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x3C), 1);
        var load = data.AsSpan(0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(load, 1);
        var note = data.AsSpan(0x40 + 56);
        BinaryPrimitives.WriteUInt32LittleEndian(note, 4);
        BinaryPrimitives.WriteUInt64LittleEndian(note[0x10..], 0x100); // p_vaddr
        BinaryPrimitives.WriteUInt64LittleEndian(note[0x28..], 16 + 20); // p_memsz
        var notes = data.AsSpan(0x100);
        BinaryPrimitives.WriteUInt32LittleEndian(notes, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(notes[4..], 20);
        BinaryPrimitives.WriteUInt32LittleEndian(notes[8..], 3); // NT_GNU_BUILD_ID
        "GNU\0"u8.CopyTo(notes[12..]);
        BuildId.CopyTo(notes[16..]);
        var section = data.AsSpan(0x180);
        BinaryPrimitives.WriteUInt32LittleEndian(section[4..], 7); // SHT_NOTE
        BinaryPrimitives.WriteUInt64LittleEndian(section[0x18..], 0x100);
        BinaryPrimitives.WriteUInt64LittleEndian(section[0x20..], 16 + 20);
        return data;
    }

    // A PE32+ image as mapped (RVA == offset): DOS stub, PE header, a debug directory with one CodeView RSDS entry.
    private static byte[] Pe()
    {
        var data = new byte[0x400];
        "MZ"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(data.AsSpan(0x80));
        var optional = data.AsSpan(0x80 + 24);
        BinaryPrimitives.WriteUInt16LittleEndian(optional, 0x20B);
        var debugDirectory = optional[(112 + (6 * 8))..];
        BinaryPrimitives.WriteUInt32LittleEndian(debugDirectory, 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(debugDirectory[4..], 28);
        var entry = data.AsSpan(0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], 2); // IMAGE_DEBUG_TYPE_CODEVIEW
        BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], 0x300); // AddressOfRawData
        var rsds = data.AsSpan(0x300);
        "RSDS"u8.CopyTo(rsds);
        PdbGuid.TryWriteBytes(rsds[4..]);
        BinaryPrimitives.WriteUInt32LittleEndian(rsds[20..], 3);
        return data;
    }

    private const string MachOId = "A0A1A2A3-A4A5-A6A7-A8A9-AAABACADAEAF";
    private static readonly string ElfId = Convert.ToHexStringLower(BuildId);
    private static readonly string PeId = PdbGuid.ToString("N").ToUpperInvariant() + "3";

    // ── Build ids ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildIdsComeFromTheImageHeaders()
    {
        Assert.Equal(MachOId, NativeImageId.Read(new NativeImageId.Bytes(MachO())));
        Assert.Equal(ElfId, NativeImageId.Read(new NativeImageId.Bytes(Elf())));
        Assert.Equal(PeId, NativeImageId.Read(new NativeImageId.Bytes(Pe())));
        Assert.Null(NativeImageId.Read(new NativeImageId.Bytes([1, 2, 3, 4, 5, 6, 7, 8])));
        Assert.Null(NativeImageId.Read(new NativeImageId.Bytes(MachO()[..40]))); // truncated: no id, no exception
    }

    [Fact]
    public void BuildIdsAreReadFromAMappedImage()
    {
        foreach (var (image, id) in new[] { (MachO(), MachOId), (Elf(), ElfId), (Pe(), PeId) })
        {
            var handle = GCHandle.Alloc(image, GCHandleType.Pinned);
            try
            {
                Assert.Equal(id, NativeImageId.FromMemory(handle.AddrOfPinnedObject()));
            }
            finally
            {
                handle.Free();
            }
        }

        Assert.Null(NativeImageId.FromMemory(0));
    }

    // ── Symbol files ─────────────────────────────────────────────────────────────────────────────────────────────

    private void WriteSymbols()
    {
        var dwarf = Path.Combine(_folder, "Game.Desktop.dSYM", "Contents", "Resources", "DWARF");
        Directory.CreateDirectory(dwarf);
        File.WriteAllBytes(Path.Combine(dwarf, "Game.Desktop"), MachO());
        File.WriteAllBytes(Path.Combine(_folder, "Game.Desktop.dbg"), Elf());
    }

    [Fact]
    public void TheSymbolsFolderIsIndexedByBuildId()
    {
        WriteSymbols();
        var symbols = new NativeSymbols(_folder);

        Assert.Equal(2, symbols.Files.Count);
        var macho = symbols.Find("Game", MachOId)!;
        Assert.Equal((NativeSymbolKind.MachO, 0x100000000UL, "arm64"), (macho.Kind, macho.ImageAddress, macho.Architecture));
        var elf = symbols.Find("Game", ElfId.ToUpperInvariant())!; // ids compare case-insensitively
        Assert.Equal((NativeSymbolKind.Elf, 0UL), (elf.Kind, elf.ImageAddress));
        Assert.Null(symbols.Find("Game", "ANOTHERBUILD")); // another build's symbols are never used
    }

    private sealed class FakeTools(Dictionary<string, string> outputs) : INativeToolRunner
    {
        public List<(string Path, IReadOnlyList<string> Arguments)> Runs { get; } = [];

        public string? Find(string tool) => outputs.ContainsKey(tool) ? "/usr/bin/" + tool : null;

        public (int ExitCode, string Output) Run(string path, IReadOnlyList<string> arguments)
        {
            Runs.Add((path, arguments));
            return (0, outputs[Path.GetFileName(path)]);
        }
    }

    private static CrashReport Report(string imageId, params string[] offsets)
    {
        var exception = new CrashException { Type = "System.InvalidOperationException", Message = "boom" };
        foreach (var offset in offsets)
            exception.Frames.Add(new CrashFrame { Method = "Game.Spinner.OnProcess", NativeImage = "Game", NativeOffset = offset, NativeImageId = imageId });
        return new CrashReport { Exception = [exception] };
    }

    [Fact]
    public void AtosResolvesDsymFramesAtTheirTextAddress()
    {
        WriteSymbols();
        var tools = new FakeTools(new()
        {
            ["atos"] = "Game_Spinner__OnProcess (in Game.Desktop) (Spinner.cs:42)\n0x100004000 (in Game.Desktop)\n",
        });
        var report = Report(MachOId, "0x1a2b", "0x4000");

        var resolved = new NativeSymbolizer(new NativeSymbols(_folder), tools).Symbolicate(report);

        Assert.Equal(1, resolved);
        var run = Assert.Single(tools.Runs);
        Assert.Equal(["-o", Path.Combine(_folder, "Game.Desktop.dSYM", "Contents", "Resources", "DWARF", "Game.Desktop"), "-arch", "arm64",
            "-l", "0x100000000", "0x100001a2b", "0x100004000"], run.Arguments);
        Assert.Equal(("Spinner.cs", 42), (report.Exception[0].Frames[0].File, report.Exception[0].Frames[0].Line));
        Assert.Null(report.Exception[0].Frames[1].File);
    }

    [Fact]
    public void Addr2LineResolvesDbgFrames()
    {
        WriteSymbols();
        var tools = new FakeTools(new() { ["addr2line"] = "Game_Spinner__OnProcess\n/src/Game/Spinner.cs:42 (discriminator 1)\n??\n??:0\n" });
        var report = Report(ElfId, "0x10", "0x20");

        Assert.Equal(1, new NativeSymbolizer(new NativeSymbols(_folder), tools).Symbolicate(report));
        Assert.Equal(["-e", Path.Combine(_folder, "Game.Desktop.dbg"), "-f", "-C", "0x10", "0x20"], tools.Runs.Single().Arguments);
        Assert.Equal(("/src/Game/Spinner.cs", 42), (report.Exception[0].Frames[0].File, report.Exception[0].Frames[0].Line));
    }

    [Fact]
    public void LlvmSymbolizerIsPreferredWhenInstalled()
    {
        WriteSymbols();
        var tools = new FakeTools(new()
        {
            ["llvm-symbolizer"] = "Game_Spinner__OnProcess\nC:\\src\\Game\\Spinner.cs:42:13\n\n??\n??:0:0\n\n",
            ["atos"] = "unused",
        });
        var report = Report(MachOId, "0x1a2b", "0x4000");

        Assert.Equal(1, new NativeSymbolizer(new NativeSymbols(_folder), tools).Symbolicate(report));
        Assert.Equal("/usr/bin/llvm-symbolizer", tools.Runs.Single().Path);
        Assert.Contains("0x100001a2b", tools.Runs.Single().Arguments);
        Assert.Equal(("C:\\src\\Game\\Spinner.cs", 42), (report.Exception[0].Frames[0].File, report.Exception[0].Frames[0].Line));
    }

    [Fact]
    public void FramesOfAnotherBuildOrWithoutAToolStayUnresolved()
    {
        WriteSymbols();
        var tools = new FakeTools([]);
        var symbolizer = new NativeSymbolizer(new NativeSymbols(_folder), tools);
        var other = Report("00000000-0000-0000-0000-000000000000", "0x10");
        Assert.Equal(0, symbolizer.Symbolicate(other));
        Assert.Contains(symbolizer.Notes, n => n.Contains("no symbol file of their build", StringComparison.Ordinal));

        Assert.Equal(0, symbolizer.Symbolicate(Report(MachOId, "0x10")));
        Assert.Contains(symbolizer.Notes, n => n.Contains("atos", StringComparison.Ordinal));
        Assert.Empty(tools.Runs);
    }

    [Fact]
    public void NativeFrameFieldsRoundTripThroughTheReportJson()
    {
        var report = Report(ElfId, "0xbeef");
        var again = CrashReport.FromJson(report.ToJson());
        var frame = again.Exception[0].Frames[0];
        Assert.Equal(("Game", "0xbeef", ElfId), (frame.NativeImage, frame.NativeOffset, frame.NativeImageId));
        Assert.Equal(0xbeefUL, NativeSymbolizer.ParseOffset(frame.NativeOffset));
        Assert.Null(NativeSymbolizer.ParseOffset("beef"));
    }
}
