using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Text;

namespace MainframeEngine.Symbolicate;

/// <summary>The kind of a native symbol file.</summary>
public enum NativeSymbolKind
{
    /// <summary>The DWARF Mach-O inside a <c>.dSYM</c> bundle (macOS).</summary>
    MachO,

    /// <summary>A <c>.dbg</c> ELF debug file (Linux).</summary>
    Elf,

    /// <summary>A Windows <c>.pdb</c>, symbolicated through its executable beside it.</summary>
    Pdb,
}

/// <summary>
/// One native symbol file of a symbols folder (ADR 0260).
/// </summary>
/// <param name="Path">The file a symbolizer reads (the DWARF file of a dSYM, the <c>.dbg</c>, or the executable of a PDB).</param>
/// <param name="Kind">Its format.</param>
/// <param name="Id">Its build id, formatted as <see cref="CrashFrame.NativeImageId"/> is; null when it has none.</param>
/// <param name="ImageName">The name of the image it describes (the executable's file name, or the symbol file's stem).</param>
/// <param name="ImageAddress">The address the image's first byte has in the symbol file (Mach-O <c>__TEXT</c> vmaddr, the first ELF load segment, the PE image base).</param>
/// <param name="Architecture">The Mach-O architecture (<c>arm64</c>, <c>x86_64</c>) for <c>atos</c>; null elsewhere.</param>
public sealed record NativeSymbolFile(string Path, NativeSymbolKind Kind, string? Id, string ImageName, ulong ImageAddress, string? Architecture);

/// <summary>
/// The native symbol files of a symbols folder (a NativeAOT export's <c>symbols: separate</c> zip, unpacked): every
/// <c>*.dSYM</c> bundle, <c>*.dbg</c> file and Windows <c>*.pdb</c> with its executable, indexed by build id.
/// </summary>
public sealed class NativeSymbols
{
    private readonly List<NativeSymbolFile> _files = [];

    /// <summary>Indexes <paramref name="symbolsFolder"/>.</summary>
    public NativeSymbols(string symbolsFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsFolder);
        foreach (var dsym in Directory.EnumerateDirectories(symbolsFolder, "*.dSYM", SearchOption.AllDirectories))
        {
            var dwarf = Path.Combine(dsym, "Contents", "Resources", "DWARF");
            if (!Directory.Exists(dwarf))
                continue;
            foreach (var file in Directory.EnumerateFiles(dwarf))
                if (ReadMachO(file) is { } entry)
                    _files.Add(entry);
        }

        foreach (var file in Directory.EnumerateFiles(symbolsFolder, "*.dbg", SearchOption.AllDirectories))
            if (ReadElf(file) is { } entry)
                _files.Add(entry);
        foreach (var pdb in Directory.EnumerateFiles(symbolsFolder, "*.pdb", SearchOption.AllDirectories))
            if (ReadPdb(pdb) is { } entry)
                _files.Add(entry);
    }

    /// <summary>The symbol files found.</summary>
    public IReadOnlyList<NativeSymbolFile> Files => _files;

    /// <summary>
    /// The symbol file of the image <paramref name="imageId"/> (<paramref name="imageName"/>): the one with that build id,
    /// never one of another build. A frame without an id (or a PDB, which has none here) matches by image name.
    /// </summary>
    public NativeSymbolFile? Find(string? imageName, string? imageId)
    {
        if (imageId is { Length: > 0 })
        {
            if (_files.FirstOrDefault(f => string.Equals(f.Id, imageId, StringComparison.OrdinalIgnoreCase)) is { } byId)
                return byId;
            return _files.FirstOrDefault(f => f.Id is null && Matches(f, imageName));
        }

        return _files.FirstOrDefault(f => Matches(f, imageName));
    }

    private static bool Matches(NativeSymbolFile file, string? imageName) =>
        imageName is { Length: > 0 } && (string.Equals(file.ImageName, imageName, StringComparison.Ordinal)
                                          || string.Equals(Path.GetFileNameWithoutExtension(file.ImageName), Path.GetFileNameWithoutExtension(imageName), StringComparison.Ordinal));

    /// <summary>A thin 64-bit Mach-O file's UUID, <c>__TEXT</c> address and architecture; null for anything else.</summary>
    public static NativeSymbolFile? ReadMachO(string path)
    {
        var data = ReadHead(path, 1 << 16);
        if (data.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0xFEEDFACF)
            return null;
        var architecture = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) switch
        {
            0x0100000C => "arm64",
            0x01000007 => "x86_64",
            _ => null,
        };
        var commands = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        ulong text = 0;
        var offset = 32;
        for (var i = 0; i < commands && offset + 8 <= data.Length; i++)
        {
            var cmd = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            if (cmd == 0x19 && offset + 32 <= data.Length // LC_SEGMENT_64: segname[16], vmaddr
                && Encoding.ASCII.GetString(data, offset + 8, 16).TrimEnd('\0') == "__TEXT")
                text = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset + 24));
            if (size < 8)
                break;
            offset += size;
        }

        var image = System.IO.Path.GetFileName(path);
        return new NativeSymbolFile(path, NativeSymbolKind.MachO, NativeImageId.Read(new NativeImageId.Bytes(data)), image, text, architecture);
    }

    /// <summary>An ELF debug file's GNU build id (from its note sections) and first load address; null for anything else.</summary>
    public static NativeSymbolFile? ReadElf(string path)
    {
        var file = new FileImage(path);
        Span<byte> header = stackalloc byte[64];
        if (!file.TryRead(0, header) || header[0] != 0x7F || header[1] != (byte)'E' || header[4] != 2 || header[5] != 1)
            return null;
        var phoff = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[0x20..]);
        var shoff = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[0x28..]);
        var phentsize = BinaryPrimitives.ReadUInt16LittleEndian(header[0x36..]);
        var phnum = BinaryPrimitives.ReadUInt16LittleEndian(header[0x38..]);
        var shentsize = BinaryPrimitives.ReadUInt16LittleEndian(header[0x3A..]);
        var shnum = BinaryPrimitives.ReadUInt16LittleEndian(header[0x3C..]);
        ulong load = 0;
        Span<byte> entry = stackalloc byte[64];
        for (var i = 0; i < phnum && phentsize >= 56; i++)
        {
            if (file.TryRead(phoff + ((long)i * phentsize), entry[..56]) && BinaryPrimitives.ReadUInt32LittleEndian(entry) == 1) // PT_LOAD
            {
                load = BinaryPrimitives.ReadUInt64LittleEndian(entry[0x10..]);
                break;
            }
        }

        string? id = null;
        for (var i = 0; i < shnum && shentsize >= 64 && id is null; i++)
        {
            if (!file.TryRead(shoff + ((long)i * shentsize), entry) || BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]) != 7) // SHT_NOTE
                continue;
            id = NativeImageId.Notes(file, (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[0x18..]), (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[0x20..]));
        }

        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        return new NativeSymbolFile(path, NativeSymbolKind.Elf, id, name, load, null);
    }

    /// <summary>A Windows PDB with its executable beside it (same stem, or the only <c>.exe</c> there); null without one.</summary>
    public static NativeSymbolFile? ReadPdb(string pdb)
    {
        if (IsPortablePdb(pdb))
            return null; // a managed PDB: Symbolicator's
        var folder = System.IO.Path.GetDirectoryName(pdb)!;
        var stem = System.IO.Path.GetFileNameWithoutExtension(pdb);
        var exe = System.IO.Path.Combine(folder, stem + ".exe");
        if (!File.Exists(exe))
            exe = Directory.EnumerateFiles(folder, "*.exe").FirstOrDefault(e => !IsManaged(e)) ?? exe;
        if (!File.Exists(exe))
            return null;
        try
        {
            using var reader = new PEReader(File.OpenRead(exe));
            if (reader.HasMetadata)
                return null;
            string? id = null;
            foreach (var debug in reader.ReadDebugDirectory())
            {
                if (debug.Type != DebugDirectoryEntryType.CodeView)
                    continue;
                var codeView = reader.ReadCodeViewDebugDirectoryData(debug);
                id = codeView.Guid.ToString("N", CultureInfo.InvariantCulture).ToUpperInvariant() + codeView.Age.ToString("X", CultureInfo.InvariantCulture);
                break;
            }

            return new NativeSymbolFile(exe, NativeSymbolKind.Pdb, id, System.IO.Path.GetFileName(exe), reader.PEHeaders.PEHeader?.ImageBase ?? 0, null);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static bool IsPortablePdb(string pdb)
    {
        var head = ReadHead(pdb, 4);
        return head.Length == 4 && head.AsSpan().SequenceEqual("BSJB"u8);
    }

    private static bool IsManaged(string file)
    {
        try
        {
            using var reader = new PEReader(File.OpenRead(file));
            return reader.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static byte[] ReadHead(string path, int length)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[(int)Math.Min(length, stream.Length)];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private sealed class FileImage(string path) : NativeImageId.IImage
    {
        public bool TryRead(long offset, Span<byte> destination)
        {
            using var handle = File.OpenHandle(path);
            return offset >= 0 && RandomAccess.Read(handle, destination, offset) == destination.Length;
        }

        public long MapAddress(long virtualAddress) => virtualAddress;
    }
}

/// <summary>Runs the platform's symbolizers (an interface so tests can stand in for them).</summary>
public interface INativeToolRunner
{
    /// <summary>The full path of <paramref name="tool"/> on <c>PATH</c> (or a known location), or null.</summary>
    string? Find(string tool);

    /// <summary>Runs <paramref name="path"/> with <paramref name="arguments"/> and returns its exit code and standard output.</summary>
    (int ExitCode, string Output) Run(string path, IReadOnlyList<string> arguments);
}

/// <summary>Finds tools on <c>PATH</c> and runs them.</summary>
public sealed class NativeToolRunner : INativeToolRunner
{
    public string? Find(string tool)
    {
        var names = OperatingSystem.IsWindows() ? new[] { tool + ".exe", tool } : [tool];
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var folder in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
                if (File.Exists(System.IO.Path.Combine(folder, name)))
                    return System.IO.Path.Combine(folder, name);
        // Homebrew's LLVM is keg-only (not on PATH).
        foreach (var keg in new[] { "/opt/homebrew/opt/llvm/bin", "/usr/local/opt/llvm/bin" })
            if (File.Exists(System.IO.Path.Combine(keg, tool)))
                return System.IO.Path.Combine(keg, tool);
        return null;
    }

    public (int ExitCode, string Output) Run(string path, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {path}.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        _ = error.Result;
        return (process.ExitCode, output);
    }
}

/// <summary>
/// Fills in the files and lines of a crash report's NativeAOT frames (ADR 0260) from <see cref="NativeSymbols"/>, with
/// <c>llvm-symbolizer</c> when it is installed (any symbol format), else <c>atos</c> (macOS, dSYM) or <c>addr2line</c>
/// (Linux, <c>.dbg</c>). One tool run per symbol file, for all of its frames.
/// </summary>
public sealed class NativeSymbolizer(NativeSymbols symbols, INativeToolRunner tools)
{
    /// <summary>What happened to the frames that could not be resolved (no symbol file, no tool), for the summary.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>Resolves the report's native frames that have no file yet; returns how many it resolved.</summary>
    public int Symbolicate(CrashReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var byFile = new Dictionary<NativeSymbolFile, List<(CrashFrame Frame, ulong Address)>>();
        var unmatched = 0;
        foreach (var frame in report.Exception.SelectMany(e => e.Frames))
        {
            if (frame.File is not null || ParseOffset(frame.NativeOffset) is not { } offset)
                continue;
            if (symbols.Find(frame.NativeImage, frame.NativeImageId) is not { } file)
            {
                unmatched++;
                continue;
            }

            if (!byFile.TryGetValue(file, out var frames))
                byFile[file] = frames = [];
            frames.Add((frame, file.ImageAddress + offset));
        }

        if (unmatched > 0)
            Notes.Add($"{unmatched} native frame(s) have no symbol file of their build (image id) in the folder.");
        var resolved = 0;
        foreach (var (file, frames) in byFile)
        {
            if (Tool(file) is not { } tool)
            {
                Notes.Add($"No symbolizer for {System.IO.Path.GetFileName(file.Path)}: install LLVM (llvm-symbolizer)" +
                          (file.Kind == NativeSymbolKind.MachO ? " or Xcode's command line tools (atos)." : file.Kind == NativeSymbolKind.Elf ? " or binutils (addr2line)." : "."));
                continue;
            }

            var (exit, output) = tools.Run(tool.Path, tool.Arguments(file, frames.Select(f => f.Address).ToList()));
            if (exit != 0)
            {
                Notes.Add($"{System.IO.Path.GetFileName(tool.Path)} exited with {exit} for {System.IO.Path.GetFileName(file.Path)}.");
                continue;
            }

            var locations = tool.Parse(output);
            for (var i = 0; i < frames.Count && i < locations.Count; i++)
            {
                if (locations[i] is not { } location)
                    continue;
                frames[i].Frame.File = location.File;
                frames[i].Frame.Line = location.Line;
                resolved++;
            }
        }

        return resolved;
    }

    private SymbolizerTool? Tool(NativeSymbolFile file)
    {
        if (tools.Find("llvm-symbolizer") is { } llvm)
            return SymbolizerTool.LlvmSymbolizer(llvm);
        if (file.Kind == NativeSymbolKind.MachO && tools.Find("atos") is { } atos)
            return SymbolizerTool.Atos(atos);
        if (file.Kind == NativeSymbolKind.Elf && tools.Find("addr2line") is { } addr2line)
            return SymbolizerTool.Addr2Line(addr2line);
        return null;
    }

    /// <summary>A frame's <c>0x</c> hex offset, or null.</summary>
    public static ulong? ParseOffset(string? text) =>
        text is { Length: > 2 } && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}

/// <summary>One symbolizer: its command line for a symbol file's addresses, and the parser of what it prints.</summary>
public sealed class SymbolizerTool
{
    private readonly Func<NativeSymbolFile, IReadOnlyList<ulong>, IReadOnlyList<string>> _arguments;
    private readonly Func<string, IReadOnlyList<(string File, int Line)?>> _parse;

    private SymbolizerTool(string path, Func<NativeSymbolFile, IReadOnlyList<ulong>, IReadOnlyList<string>> arguments,
        Func<string, IReadOnlyList<(string File, int Line)?>> parse)
    {
        Path = path;
        _arguments = arguments;
        _parse = parse;
    }

    /// <summary>The tool's executable.</summary>
    public string Path { get; }

    /// <summary>The arguments that symbolize <paramref name="addresses"/> (addresses in the symbol file's own space).</summary>
    public IReadOnlyList<string> Arguments(NativeSymbolFile file, IReadOnlyList<ulong> addresses) => _arguments(file, addresses);

    /// <summary>One location per address, in order (null where the tool knew none).</summary>
    public IReadOnlyList<(string File, int Line)?> Parse(string output) => _parse(output);

    private static string Hex(ulong value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);

    /// <summary><c>llvm-symbolizer --obj=&lt;file&gt; &lt;addr&gt;…</c>: per address a function line, a <c>file:line:column</c> line, a blank line.</summary>
    public static SymbolizerTool LlvmSymbolizer(string path) => new(path,
        static (file, addresses) => [$"--obj={file.Path}", "--functions=linkage", .. addresses.Select(Hex)],
        static output =>
        {
            var result = new List<(string, int)?>();
            foreach (var block in output.Replace("\r", "", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                result.Add(lines.Length >= 2 ? FileLine(lines[1], columns: true) : null);
            }

            return result;
        });

    /// <summary><c>atos -o &lt;dwarf&gt; -arch &lt;arch&gt; -l &lt;__TEXT&gt; &lt;addr&gt;…</c>: per address <c>Function (in Image) (File.cs:12)</c>.</summary>
    public static SymbolizerTool Atos(string path) => new(path,
        static (file, addresses) =>
        {
            List<string> args = ["-o", file.Path];
            if (file.Architecture is { } arch)
                args.AddRange(["-arch", arch]);
            args.AddRange(["-l", Hex(file.ImageAddress), .. addresses.Select(Hex)]);
            return args;
        },
        static output => output.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var close = line.LastIndexOf(')');
            var open = close > 0 ? line.LastIndexOf('(', close) : -1;
            return open >= 0 ? FileLine(line[(open + 1)..close], columns: false) : null;
        }).ToList());

    /// <summary><c>addr2line -e &lt;dbg&gt; -f -C &lt;addr&gt;…</c>: per address a function line and a <c>file:line</c> line.</summary>
    public static SymbolizerTool Addr2Line(string path) => new(path,
        static (file, addresses) => ["-e", file.Path, "-f", "-C", .. addresses.Select(Hex)],
        static output =>
        {
            var lines = output.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var result = new List<(string, int)?>();
            for (var i = 1; i < lines.Length; i += 2)
                result.Add(FileLine(lines[i], columns: false));
            return result;
        });

    // "path:line" or "path:line:column" ("??:0" and "??:?" mean unknown). Paths may hold ':' (Windows drives).
    private static (string File, int Line)? FileLine(string text, bool columns)
    {
        text = text.Trim();
        var discriminator = text.IndexOf(" (discriminator", StringComparison.Ordinal);
        if (discriminator > 0)
            text = text[..discriminator];
        var last = text.LastIndexOf(':');
        if (last <= 0)
            return null;
        if (columns && int.TryParse(text.AsSpan(last + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            var before = text.LastIndexOf(':', last - 1);
            if (before > 0 && int.TryParse(text.AsSpan(before + 1, last - before - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var withColumn))
                return Located(text[..before], withColumn);
        }

        return int.TryParse(text.AsSpan(last + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var line) ? Located(text[..last], line) : null;
    }

    private static (string File, int Line)? Located(string file, int line) => file.Length == 0 || file.StartsWith("??", StringComparison.Ordinal) || line <= 0 ? null : (file, line);
}
