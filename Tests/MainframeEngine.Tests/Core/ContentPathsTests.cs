namespace MainframeEngine.Tests.Core;

public sealed class ContentPathsTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "mf-app");

    [Fact]
    public void RootIsTheContentFolderNextToTheApplicationNotTheWorkingDirectory()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Content"), ContentPaths.Root);
        Assert.Equal(AppContext.BaseDirectory, ContentPaths.BaseDirectory);
    }

    [Theory]
    [InlineData("Shaders/Sky/Sky.vk.vert.spv")]
    [InlineData("Shaders\\Sky\\Sky.vk.vert.spv")]
    public void ContentRelativePathsResolveUnderRoot(string path)
    {
        var resolved = ContentPaths.Resolve(path, Base);

        Assert.Equal(Path.GetFullPath(Path.Combine(Base, "Content", "Shaders", "Sky", "Sky.vk.vert.spv")),
            resolved.Replace('\\', Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData("Content/Sky/sky.png")]
    [InlineData("Content\\Sky\\sky.png")]
    public void PathsStartingWithTheContentFolderResolveAgainstTheBaseDirectory(string path)
    {
        var resolved = ContentPaths.Resolve(path, Base).Replace('\\', Path.DirectorySeparatorChar);

        Assert.Equal(Path.GetFullPath(Path.Combine(Base, "Content", "Sky", "sky.png")), resolved);
    }

    [Fact]
    public void AFolderMerelyStartingWithContentIsNotTheContentFolder()
    {
        var resolved = ContentPaths.Resolve("ContentPacks/a.bin", Base);

        Assert.Equal(Path.GetFullPath(Path.Combine(Base, "Content", "ContentPacks", "a.bin")), resolved);
    }

    [Fact]
    public void RootedPathsAreReturnedUnchanged()
    {
        var rooted = Path.Combine(Path.GetTempPath(), "elsewhere", "file.png");

        Assert.Equal(Path.GetFullPath(rooted), ContentPaths.Resolve(rooted, Base));
    }

    [Fact]
    public void EmptyPathsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ContentPaths.Resolve(" ", Base));
        Assert.Throws<ArgumentException>(() => ContentPaths.Resolve("a", ""));
    }

    [Fact]
    public void EngineShadersResolveToFilesThatExist()
    {
        // The engine's content flows to every referencing app's output (here: the test output).
        Assert.True(File.Exists(ContentPaths.Resolve("Shaders/Sky/Sky.vk.vert.spv")));
        Assert.True(File.Exists(ContentPaths.Resolve("Content/Shaders/Shadows/Shadow2D.vk.vert.spv")));
    }

    [Fact]
    public void ContentPathsResolveInTheProjectDirectoryFirstWhenTheFileExistsThere()
    {
        var project = Directory.CreateTempSubdirectory("mf-project").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(project, "Content", "Sky"));
            File.WriteAllText(Path.Combine(project, "Content", "Sky", "only-in-project.txt"), "x");
            ContentPaths.ProjectDirectory = project;

            Assert.Equal(Path.Combine(project, "Content", "Sky", "only-in-project.txt"),
                ContentPaths.Resolve("Content/Sky/only-in-project.txt"));
            // Missing in the project: next to the application, as before.
            Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Content", "Sky", "missing.txt")),
                ContentPaths.Resolve("Content/Sky/missing.txt"));
            // Engine-relative paths never look in the project.
            Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Content", "Sky", "only-in-project.txt")),
                ContentPaths.Resolve("Sky/only-in-project.txt"));
        }
        finally
        {
            ContentPaths.ProjectDirectory = null;
            Directory.Delete(project, recursive: true);
        }
    }
}
