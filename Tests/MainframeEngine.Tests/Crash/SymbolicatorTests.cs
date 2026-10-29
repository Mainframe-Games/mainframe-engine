using MainframeEngine.Symbolicate;

namespace MainframeEngine.Tests.Crash;

/// <summary>G15.8: <c>mf-symbolicate</c> maps a frame's MVID, method token and IL offset back to its file and line.</summary>
[Collection(nameof(CrashReporterState))]
public sealed class SymbolicatorTests
{
    private static InvalidOperationException ThrowHere()
    {
        try
        {
            throw new InvalidOperationException("symbolicate me");
        }
        catch (InvalidOperationException e)
        {
            return e;
        }
    }

    [Fact]
    public void FramesWithoutLinesGetThemFromThePdb()
    {
        using var scratch = new CrashScratch();
        CrashReporter.Install(scratch.Options());
        var report = CrashReporter.LoadFolder(CrashReporter.WriteReport(ThrowHere(), CrashKind.ManagedException)!)!;
        var frame = report.Exception[0].Frames[0];
        var expectedLine = frame.Line;
        Assert.NotNull(expectedLine);
        frame.File = null; // as a build shipped without PDBs reports it
        frame.Line = null;

        // The symbols folder holds this build's assembly and PDB (a symbols zip, unpacked).
        var symbols = Path.Combine(scratch.Root, "symbols");
        Directory.CreateDirectory(symbols);
        var assembly = typeof(SymbolicatorTests).Assembly.Location;
        File.Copy(assembly, Path.Combine(symbols, Path.GetFileName(assembly)));
        File.Copy(Path.ChangeExtension(assembly, ".pdb"), Path.Combine(symbols, Path.GetFileNameWithoutExtension(assembly) + ".pdb"));

        using var symbolicator = new Symbolicator(symbols);
        Assert.True(symbolicator.Symbolicate(report) >= 1);

        Assert.EndsWith("SymbolicatorTests.cs", frame.File, StringComparison.Ordinal);
        Assert.Equal(expectedLine, frame.Line);
    }

    [Fact]
    public void FramesOfOtherBuildsStayUnresolved()
    {
        using var scratch = new CrashScratch();
        var symbols = Path.Combine(scratch.Root, "empty");
        Directory.CreateDirectory(symbols);
        using var symbolicator = new Symbolicator(symbols);

        Assert.Null(symbolicator.Resolve(new CrashFrame { Method = "X.Y()", Mvid = Guid.NewGuid().ToString("D"), Token = 0x06000001, IlOffset = 4 }));
        Assert.Null(symbolicator.Resolve(new CrashFrame { Method = "X.Y()" })); // nothing to go on
    }
}
