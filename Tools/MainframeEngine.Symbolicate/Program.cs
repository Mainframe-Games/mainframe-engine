using MainframeEngine;
using MainframeEngine.Symbolicate;

// mf-symbolicate <report.json> --symbols <folder> [--out <report.json>]
// Fills in the file and line of each frame from the symbols in <folder> (a symbols zip, unpacked) and prints the stacks:
// managed frames from the portable PDBs (by MVID), NativeAOT frames from the .dSYM/.dbg/.pdb with llvm-symbolizer, atos
// or addr2line (by image build id, ADR 0260).
string? reportPath = null, symbols = null, output = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--symbols" when i + 1 < args.Length:
            symbols = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
            output = args[++i];
            break;
        default:
            reportPath ??= args[i];
            break;
    }
}

if (reportPath is null || symbols is null || !File.Exists(reportPath) || !Directory.Exists(symbols))
{
    Console.Error.WriteLine("Usage: mf-symbolicate <report.json> --symbols <folder> [--out <report.json>]");
    return 2;
}

var report = CrashReport.FromJson(File.ReadAllBytes(reportPath));
using var symbolicator = new Symbolicator(symbols);
var resolved = symbolicator.Symbolicate(report);
var native = new NativeSymbolizer(new NativeSymbols(symbols), new NativeToolRunner());
var nativeResolved = native.Symbolicate(report);
Console.WriteLine($"{report.Summary} ({resolved} frame(s) resolved from {symbolicator.AssemblyCount} assemblies, {nativeResolved} native)");
foreach (var note in native.Notes)
    Console.WriteLine($"note: {note}");
foreach (var exception in report.Exception)
{
    Console.WriteLine($"{exception.Type}: {exception.Message}");
    foreach (var frame in exception.Frames)
        Console.WriteLine(frame.File is { } file ? $"   at {frame.Method} in {file}:{frame.Line}"
            : frame.NativeOffset is { } offset ? $"   at {frame.Method} ({frame.NativeImage}+{offset})" : $"   at {frame.Method}");
}

if (output is not null)
    File.WriteAllBytes(output, report.ToJson());
return 0;
