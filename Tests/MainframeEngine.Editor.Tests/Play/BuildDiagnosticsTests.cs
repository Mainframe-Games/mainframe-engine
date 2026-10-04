namespace MainframeEngine.Editor.Tests;

public sealed class PlayMsBuildOutputParserTests
{
    public static TheoryData<string, BuildDiagnostic> Lines => new()
    {
        {
            "/abs/Foo.cs(12,5): error CS1002: ; expected [/abs/MyGame.csproj]",
            new BuildDiagnostic(BuildDiagnosticSeverity.Error, "CS1002", "; expected", "/abs/Foo.cs", 12, 5, "/abs/MyGame.csproj")
        },
        {
            "Foo.cs(3): warning CS0168: The variable 'e' is declared but never used",
            new BuildDiagnostic(BuildDiagnosticSeverity.Warning, "CS0168", "The variable 'e' is declared but never used", "Foo.cs", 3, 0, null)
        },
        {
            "/x/y.csproj : error NU1101: Unable to find package Foo. No packages exist with this id in source(s): nuget.org [/x/y.csproj]",
            new BuildDiagnostic(BuildDiagnosticSeverity.Error, "NU1101", "Unable to find package Foo. No packages exist with this id in source(s): nuget.org",
                "/x/y.csproj", 0, 0, "/x/y.csproj")
        },
        {
            "MSBUILD : error MSB1009: Project file does not exist.",
            new BuildDiagnostic(BuildDiagnosticSeverity.Error, "MSB1009", "Project file does not exist.", null, 0, 0, null)
        },
        {
            "CSC : error CS2001: Source file '/abs/Gone.cs' could not be found. [/abs/MyGame.csproj]",
            new BuildDiagnostic(BuildDiagnosticSeverity.Error, "CS2001", "Source file '/abs/Gone.cs' could not be found.", null, 0, 0, "/abs/MyGame.csproj")
        },
        {
            "    /abs/Player.cs(40,13): warning CS8618: Non-nullable field 'x' must contain a non-null value. [/abs/MyGame.csproj]",
            new BuildDiagnostic(BuildDiagnosticSeverity.Warning, "CS8618", "Non-nullable field 'x' must contain a non-null value.", "/abs/Player.cs", 40, 13, "/abs/MyGame.csproj")
        },
        {
            @"C:\src\My Game\Foo.cs(7,21,7,30): error CS0103: The name 'y' does not exist in the current context [C:\src\My Game\MyGame.csproj::TargetFramework=net10.0]",
            new BuildDiagnostic(BuildDiagnosticSeverity.Error, "CS0103", "The name 'y' does not exist in the current context", @"C:\src\My Game\Foo.cs", 7, 21,
                @"C:\src\My Game\MyGame.csproj")
        },
        {
            "/usr/share/dotnet/sdk/10.0.100/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.TargetFrameworkInference.targets(166,5): error NETSDK1045: The current .NET SDK does not support targeting .NET 11.0. [/abs/MyGame.csproj]",
            new BuildDiagnostic(BuildDiagnosticSeverity.Error, "NETSDK1045", "The current .NET SDK does not support targeting .NET 11.0.",
                "/usr/share/dotnet/sdk/10.0.100/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.TargetFrameworkInference.targets", 166, 5, "/abs/MyGame.csproj")
        },
        {
            "/abs/Foo.cs(1,1): info IDE0005: Using directive is unnecessary. [/abs/MyGame.csproj]",
            new BuildDiagnostic(BuildDiagnosticSeverity.Info, "IDE0005", "Using directive is unnecessary.", "/abs/Foo.cs", 1, 1, "/abs/MyGame.csproj")
        },
        {
            "/abs/MyGame.csproj : error : The project file could not be loaded.",
            new BuildDiagnostic(BuildDiagnosticSeverity.Error, "", "The project file could not be loaded.", "/abs/MyGame.csproj", 0, 0, "/abs/MyGame.csproj")
        },
        {
            "error CS5001: Program does not contain a static 'Main' method suitable for an entry point",
            new BuildDiagnostic(BuildDiagnosticSeverity.Error, "CS5001", "Program does not contain a static 'Main' method suitable for an entry point", null, 0, 0, null)
        },
    };

    [Theory]
    [MemberData(nameof(Lines))]
    public void CanonicalLinesParse(string line, BuildDiagnostic expected)
    {
        Assert.True(MsBuildOutputParser.TryParse(line, out var diagnostic));
        Assert.Equal(expected, diagnostic);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Build succeeded.")]
    [InlineData("Build FAILED.")]
    [InlineData("    0 Warning(s)")]
    [InlineData("    1 Error(s)")]
    [InlineData("Time Elapsed 00:00:01.23")]
    [InlineData("  MyGame -> /abs/bin/Debug/net10.0/MyGame.dll")]
    [InlineData("  Determining projects to restore...")]
    [InlineData("info : Restoring packages for /abs/MyGame.csproj...")]
    [InlineData("Program output: error happened: badly")]
    [InlineData("The errors: none")]
    [InlineData("warning")]
    public void OtherLinesAreNotDiagnostics(string line) =>
        Assert.False(MsBuildOutputParser.TryParse(line, out _));

    [Fact]
    public void ParseAllDeduplicatesKeepingFirstOrder()
    {
        string[] lines =
        [
            "  Determining projects to restore...",
            "/abs/B.cs(2,1): warning CS0168: unused [/abs/MyGame.csproj]",
            "/abs/A.cs(1,1): error CS1002: ; expected [/abs/MyGame.csproj]",
            "Build FAILED.",
            "/abs/B.cs(2,1): warning CS0168: unused [/abs/MyGame.csproj]",
            "/abs/A.cs(1,1): error CS1002: ; expected [/abs/MyGame.csproj]",
            "/abs/A.cs(1,1): error CS1002: ; expected [/abs/Other.csproj]",
        ];

        var all = MsBuildOutputParser.ParseAll(lines);

        Assert.Equal(3, all.Count);
        Assert.Equal(("CS0168", "/abs/MyGame.csproj"), (all[0].Code, all[0].Project));
        Assert.Equal(("CS1002", "/abs/MyGame.csproj"), (all[1].Code, all[1].Project));
        Assert.Equal(("CS1002", "/abs/Other.csproj"), (all[2].Code, all[2].Project));
    }
}

public sealed class PlayDotnetGameBuilderTests
{
    [Fact]
    public void ArgumentsMatchTheEditorBuild() =>
        Assert.Equal(["build", "/p/My.sln", "-c", "Release", "-nologo", "-v:minimal", "-clp:NoSummary", "-p:GenerateFullPaths=true"],
            DotnetGameBuilder.BuildArguments("/p/My.sln", "Release"));

    [Fact]
    public async Task MissingDotnetIsAnErrorNamingThePath()
    {
        var project = Path.Combine(Directory.CreateTempSubdirectory("mf-play-build").FullName, "MyGame.csproj");
        await File.WriteAllTextAsync(project, "<Project />", TestContext.Current.CancellationToken);
        var dotnet = Path.Combine(Path.GetDirectoryName(project)!, "no-such-dotnet");
        try
        {
            var result = await new DotnetGameBuilder(dotnet).BuildAsync(project, null, TestContext.Current.CancellationToken);

            Assert.False(result.Succeeded);
            Assert.False(result.Cancelled);
            Assert.Contains(dotnet, result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(project)!, recursive: true);
        }
    }

    [Fact]
    public async Task MissingProjectIsAnError()
    {
        var result = await new DotnetGameBuilder("dotnet").BuildAsync("/definitely/not/here/MyGame.csproj", null, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("does not exist", result.Error, StringComparison.Ordinal);
    }
}
