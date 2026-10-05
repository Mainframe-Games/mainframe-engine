namespace MainframeEngine.Editor;

/// <summary>
/// <c>--validate-demo-zip &lt;zip&gt; [--build [--engine &lt;checkout&gt;]]</c>: unpacks a Demo zip into a temporary folder with
/// <see cref="DemoArchive"/> (the check the editor's Download Demo runs) and prints <c>ok &lt;root&gt;</c> (exit 0) or the reason
/// (exit 1). With <c>--build</c> it then points the unpacked Demo at an engine checkout (<c>--engine</c>, else
/// <see cref="TemplateLocator.FindEngineCheckout"/>) like Download Demo does and runs <c>dotnet build</c> on its solution, so a
/// Demo that only builds inside the repo fails CI. CI runs it on the packaged Demo. No window, SDL or engine is created.
/// </summary>
public static class DemoZipValidation
{
    public const string Flag = "--validate-demo-zip";
    public const string BuildFlag = "--build";
    public const string EngineFlag = "--engine";

    /// <summary>Parsed arguments; <see cref="Build"/> is false unless <c>--build</c> was given.</summary>
    public readonly record struct Options(string Zip, bool Build, string? Engine);

    /// <summary>Builds <paramref name="solution"/>; returns the exit code (0 = built). Output goes to the given writers.</summary>
    public delegate int BuildRunner(string solution, TextWriter output, TextWriter error);

    public static bool IsValidateDemoZip(IReadOnlyList<string> args) =>
        args is { Count: > 0 } && string.Equals(args[0], Flag, StringComparison.Ordinal);

    public static bool TryParse(IReadOnlyList<string> args, out Options options)
    {
        options = default;
        if (!IsValidateDemoZip(args) || args.Count < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            return false;
        var build = false;
        string? engine = null;
        for (var i = 2; i < args.Count; i++)
        {
            switch (args[i])
            {
                case BuildFlag:
                    build = true;
                    break;
                case EngineFlag when i + 1 < args.Count:
                    engine = args[++i];
                    break;
                default:
                    return false;
            }
        }

        if (engine is not null && !build)
            return false; // --engine only means something with --build
        options = new Options(args[1], build, engine);
        return true;
    }

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, BuildRunner? build = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!TryParse(args, out var options))
        {
            error.WriteLine($"Usage: MainframeEngine.Editor {Flag} <zip> [{BuildFlag} [{EngineFlag} <engine checkout>]]");
            return 2;
        }

        // A real path: MSBuild cannot resolve the engine's project references from a symlinked folder (macOS temp).
        var work = GameProjectLayout.RealPath(Directory.CreateTempSubdirectory("mf-demo-validate").FullName);
        try
        {
            var root = DemoArchive.ExtractAndValidate(Path.GetFullPath(options.Zip), work);
            output.WriteLine($"ok {root}");
            return options.Build ? BuildDemo(root, options.Engine, output, error, build ?? DotnetBuild) : 0;
        }
        catch (DemoDownloadException e)
        {
            error.WriteLine(e.Message);
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"Could not delete {work}: {e.Message}");
            }
        }
    }

    private static int BuildDemo(string root, string? engine, TextWriter output, TextWriter error, BuildRunner build)
    {
        var checkout = engine is null ? TemplateLocator.FindEngineCheckout() : Path.GetFullPath(engine);
        if (checkout is null || !File.Exists(Path.Combine(checkout, "MainframeEngine", "MainframeEngine.csproj")))
        {
            error.WriteLine($"No engine checkout to build the demo against (pass {EngineFlag} <checkout> or set the engine path).");
            return 1;
        }

        var solutions = Directory.GetFiles(root, "*.slnx");
        if (solutions.Length != 1)
        {
            error.WriteLine($"The demo must contain exactly one .slnx solution in its root (found {solutions.Length}).");
            return 1;
        }

        EnginePathRewriter.Rewrite(Path.Combine(root, "Directory.Build.props"), GameProjectLayout.RealPath(checkout));
        output.WriteLine($"building {Path.GetFileName(solutions[0])} against {checkout}");
        var exit = build(solutions[0], output, error);
        if (exit != 0)
            error.WriteLine($"The demo does not build (dotnet build exited {exit}).");
        return exit == 0 ? 0 : 1;
    }

    private static int DotnetBuild(string solution, TextWriter output, TextWriter error)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(solution)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("build");
        start.ArgumentList.Add(solution);
        start.ArgumentList.Add("--nologo");
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new DemoDownloadException("dotnet could not be started.");
        var stderr = process.StandardError.ReadToEndAsync();
        output.Write(process.StandardOutput.ReadToEnd());
        process.WaitForExit();
        error.Write(stderr.GetAwaiter().GetResult());
        return process.ExitCode;
    }
}
