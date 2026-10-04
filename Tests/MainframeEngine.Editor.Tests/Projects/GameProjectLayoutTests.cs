namespace MainframeEngine.Editor.Tests.Projects;

public sealed class GameProjectLayoutTests : IDisposable
{
    // Real path: the finders return real paths (macOS temp folders are under a symlink).
    private readonly string _directory = GameProjectLayout.RealPath(Directory.CreateTempSubdirectory("mf-layoutproject").FullName);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_directory, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private static ProjectSettings Settings(params string[] assemblies)
    {
        var settings = new ProjectSettings { Name = "Game" };
        settings.Assemblies.AddRange(assemblies);
        return settings;
    }

    [Fact]
    public void FindsTheTemplateLayout()
    {
        var solution = Touch("MyGame.slnx");
        var library = Touch("MyGame", "MyGame.csproj");
        var launcher = Touch("MyGame.Launcher", "MyGame.Launcher.csproj");
        Touch("MyGame", "bin", "Debug", "net10.0", "Other.csproj"); // not one level down

        Assert.Equal(Path.Combine(_directory, "project.mfproj"), GameProjectLayout.ProjectFileOf(_directory));
        Assert.Equal(solution, GameProjectLayout.SolutionOf(_directory));
        Assert.Equal(library, GameProjectLayout.GameLibraryProjectOf(_directory, Settings("MyGame")));
        Assert.Equal(library, GameProjectLayout.GameLibraryProjectOf(_directory, Settings())); // the only non-launcher project
        Assert.Equal(library, GameProjectLayout.GameLibraryProjectOf(_directory, Settings("Renamed"))); // assembly renamed in the csproj
        Assert.Equal(launcher, GameProjectLayout.LauncherProjectOf(_directory));
    }

    [Fact]
    public void SolutionPrefersSlnxAndTheFolderName()
    {
        Assert.Null(GameProjectLayout.SolutionOf(_directory));
        var sln = Touch("Other.sln");
        Assert.Equal(sln, GameProjectLayout.SolutionOf(_directory));
        var slnx = Touch("Zeta.slnx");
        Assert.Equal(slnx, GameProjectLayout.SolutionOf(_directory));
        var named = Touch(Path.GetFileName(_directory) + ".slnx");
        Assert.Equal(named, GameProjectLayout.SolutionOf(_directory));
    }

    [Fact]
    public void AmbiguousOrMissingProjectsGiveNull()
    {
        Assert.Null(GameProjectLayout.GameLibraryProjectOf(_directory, Settings("MyGame")));
        Assert.Null(GameProjectLayout.LauncherProjectOf(_directory));
        Touch("One", "One.csproj");
        Touch("Two", "Two.csproj");
        Touch("A.Launcher", "A.Launcher.csproj");
        Touch("B.Launcher", "B.Launcher.csproj");
        Assert.Null(GameProjectLayout.GameLibraryProjectOf(_directory, Settings()));
        Assert.Equal(Path.Combine(_directory, "Two", "Two.csproj"), GameProjectLayout.GameLibraryProjectOf(_directory, Settings("Two")));
        Assert.Null(GameProjectLayout.LauncherProjectOf(_directory));

        var missing = Path.Combine(_directory, "missing");
        Assert.Null(GameProjectLayout.SolutionOf(missing));
        Assert.Null(GameProjectLayout.LauncherProjectOf(missing));
        Assert.Null(GameProjectLayout.GameLibraryProjectOf(missing, Settings()));
    }

    [Fact]
    public void RealPathResolvesLinksInAnyComponent()
    {
        var target = Directory.CreateDirectory(Path.Combine(_directory, "real", "inner")).FullName;
        var link = Path.Combine(_directory, "link");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(_directory, "real"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Cannot create symbolic links here: {e.Message}");
        }

        Assert.Equal(target, GameProjectLayout.RealPath(Path.Combine(link, "inner")));
        Assert.Equal(target, GameProjectLayout.RealPath(Path.Combine(link, "inner") + Path.DirectorySeparatorChar));
        Assert.Equal(Path.Combine(target, "missing", "file.txt"), GameProjectLayout.RealPath(Path.Combine(link, "inner", "missing", "file.txt")));
        Assert.Equal(_directory, GameProjectLayout.RealPath(_directory));

        // The finders hand out real paths, which MSBuild needs.
        Touch("real", "inner", "Game.slnx");
        Assert.Equal(Path.Combine(target, "Game.slnx"), GameProjectLayout.SolutionOf(Path.Combine(link, "inner")));
        if (OperatingSystem.IsMacOS())
            Assert.Equal("/private/tmp", GameProjectLayout.RealPath("/tmp"));
    }

    [Fact]
    public void GuardsArguments()
    {
        Assert.Throws<ArgumentException>(() => GameProjectLayout.ProjectFileOf(""));
        Assert.Throws<ArgumentNullException>(() => GameProjectLayout.GameLibraryProjectOf(_directory, null!));
    }
}
