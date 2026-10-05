namespace MainframeEngine.Editor.Tests.Projects.Demo;

public sealed class DemoZipValidationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-demo-cli").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static (int Exit, string Out, string Error) Run(params string[] args) => RunWith(null, args);

    private static (int Exit, string Out, string Error) RunWith(DemoZipValidation.BuildRunner? build, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = DemoZipValidation.Run(args, output, error, build);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>A folder that passes for an engine checkout (it only has to hold the engine project).</summary>
    private string FakeEngine()
    {
        var engine = Path.Combine(_directory, "engine");
        Directory.CreateDirectory(Path.Combine(engine, "MainframeEngine"));
        File.WriteAllText(Path.Combine(engine, "MainframeEngine", "MainframeEngine.csproj"), "<Project />");
        return engine;
    }

    private string DemoZipWithSolution() =>
        DemoZips.Write(Path.Combine(_directory, "demo.zip"), new Dictionary<string, string>(DemoZips.ValidProject())
        {
            ["MainframeEngine.Demo/Demo.slnx"] = "<Solution />",
        });

    [Fact]
    public void GoodZipIsOk()
    {
        var zip = DemoZips.Write(Path.Combine(_directory, "ok.zip"), DemoZips.ValidProject());
        var (exit, output, _) = Run("--validate-demo-zip", zip);
        Assert.Equal(0, exit);
        Assert.StartsWith("ok ", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output["ok ".Length..].Trim())); // the temp folder is gone
    }

    [Fact]
    public void BadZipExitsOneWithTheReason()
    {
        var files = DemoZips.ValidProject();
        files.Remove("MainframeEngine.Demo/project.mfproj");
        var zip = DemoZips.Write(Path.Combine(_directory, "bad.zip"), files);
        var (exit, _, error) = Run("--validate-demo-zip", zip);
        Assert.Equal(1, exit);
        Assert.Contains("project.mfproj", error, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFileExitsOne() =>
        Assert.Equal(1, Run("--validate-demo-zip", Path.Combine(_directory, "nope.zip")).Exit);

    [Fact]
    public void MissingArgumentIsAUsageError() => Assert.Equal(2, Run("--validate-demo-zip").Exit);

    [Theory]
    [InlineData(new[] { "--validate-demo-zip", "z.zip", "--build" }, true, true, null)]
    [InlineData(new[] { "--validate-demo-zip", "z.zip", "--build", "--engine", "/e" }, true, true, "/e")]
    [InlineData(new[] { "--validate-demo-zip", "z.zip" }, true, false, null)]
    [InlineData(new[] { "--validate-demo-zip", "z.zip", "--engine", "/e" }, false, false, null)] // --engine needs --build
    [InlineData(new[] { "--validate-demo-zip", "z.zip", "--build", "--engine" }, false, false, null)]
    [InlineData(new[] { "--validate-demo-zip", "z.zip", "--bogus" }, false, false, null)]
    [InlineData(new[] { "--validate-demo-zip", "--build" }, false, false, null)]
    public void ParsesTheBuildOptions(string[] args, bool valid, bool build, string? engine)
    {
        Assert.Equal(valid, DemoZipValidation.TryParse(args, out var options));
        if (!valid)
            return;
        Assert.Equal(build, options.Build);
        Assert.Equal(engine, options.Engine);
    }

    [Fact]
    public void BuildPointsTheDemoAtTheEngineAndRunsTheBuildOnItsSolution()
    {
        var engine = FakeEngine();
        string? solution = null, enginePath = null;
        var (exit, output, error) = RunWith((sln, _, _) =>
        {
            solution = sln;
            enginePath = File.ReadAllText(Path.Combine(Path.GetDirectoryName(sln)!, "Directory.Build.props"));
            return 0;
        }, "--validate-demo-zip", DemoZipWithSolution(), "--build", "--engine", engine);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Equal("Demo.slnx", Path.GetFileName(solution));
        Assert.Contains(GameProjectLayout.RealPath(engine), enginePath, StringComparison.Ordinal);
        Assert.Contains("building Demo.slnx", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedBuildExitsOne()
    {
        var (exit, _, error) = RunWith((_, _, _) => 7, "--validate-demo-zip", DemoZipWithSolution(), "--build", "--engine", FakeEngine());
        Assert.Equal(1, exit);
        Assert.Contains("does not build", error, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWithoutASolutionOrAnEngineExitsOneWithoutBuilding()
    {
        var called = false;
        DemoZipValidation.BuildRunner build = (_, _, _) => { called = true; return 0; };
        var noSolution = RunWith(build, "--validate-demo-zip", DemoZips.Write(Path.Combine(_directory, "nosln.zip"), DemoZips.ValidProject()), "--build", "--engine", FakeEngine());
        Assert.Equal(1, noSolution.Exit);
        Assert.Contains(".slnx", noSolution.Error, StringComparison.Ordinal);

        var noEngine = RunWith(build, "--validate-demo-zip", DemoZipWithSolution(), "--build", "--engine", Path.Combine(_directory, "missing"));
        Assert.Equal(1, noEngine.Exit);
        Assert.Contains("engine checkout", noEngine.Error, StringComparison.Ordinal);
        Assert.False(called);
    }

    [Fact]
    public void ValidationFailureSkipsTheBuild()
    {
        var files = DemoZips.ValidProject();
        files.Remove("MainframeEngine.Demo/project.mfproj");
        var called = false;
        var (exit, _, _) = RunWith((_, _, _) => { called = true; return 0; }, "--validate-demo-zip",
            DemoZips.Write(Path.Combine(_directory, "bad.zip"), files), "--build", "--engine", FakeEngine());
        Assert.Equal(1, exit);
        Assert.False(called);
    }
}
