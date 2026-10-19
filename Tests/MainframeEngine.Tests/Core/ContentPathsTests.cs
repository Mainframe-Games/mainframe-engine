namespace MainframeEngine.Tests.Core;

public sealed class ContentPathsTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "mf-app");

    [Fact]
    public void RootIsTheContentFolderNextToTheApplicationNotTheWorkingDirectory()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Assets"), ContentPaths.Root);
        Assert.Equal(AppContext.BaseDirectory, ContentPaths.BaseDirectory);
    }

    [Theory]
    [InlineData("Shaders/Sky/Sky.vk.vert.spv")]
    [InlineData("Shaders\\Sky\\Sky.vk.vert.spv")]
    public void ContentRelativePathsResolveUnderRoot(string path)
    {
        var resolved = ContentPaths.Resolve(path, Base);

        Assert.Equal(Path.GetFullPath(Path.Combine(Base, "Assets", "Shaders", "Sky", "Sky.vk.vert.spv")),
            resolved.Replace('\\', Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData("Assets/Sky/sky.png")]
    [InlineData("Assets\\Sky\\sky.png")]
    public void PathsStartingWithTheContentFolderResolveAgainstTheBaseDirectory(string path)
    {
        var resolved = ContentPaths.Resolve(path, Base).Replace('\\', Path.DirectorySeparatorChar);

        Assert.Equal(Path.GetFullPath(Path.Combine(Base, "Assets", "Sky", "sky.png")), resolved);
    }

    [Fact]
    public void AFolderMerelyStartingWithContentIsNotTheContentFolder()
    {
        var resolved = ContentPaths.Resolve("ContentPacks/a.bin", Base);

        Assert.Equal(Path.GetFullPath(Path.Combine(Base, "Assets", "ContentPacks", "a.bin")), resolved);
    }

    [Theory]
    [InlineData("Content/Sky/sky.png")]
    [InlineData("Content\\Sky\\sky.png")]
    public void LegacyContentPathsResolveIntoTheAssetsFolder(string path)
    {
        var resolved = ContentPaths.Resolve(path, Base).Replace('\\', Path.DirectorySeparatorChar);

        Assert.Equal(Path.GetFullPath(Path.Combine(Base, "Assets", "Sky", "sky.png")), resolved);
    }

    [Theory]
    [InlineData("Content", "Assets")]
    [InlineData("Content/UI/hud.rml", "Assets/UI/hud.rml")]
    [InlineData("Content\\UI\\hud.rml", "Assets\\UI\\hud.rml")]
    [InlineData("ContentPacks/a.bin", "ContentPacks/a.bin")]
    [InlineData("content/a.bin", "content/a.bin")]
    [InlineData("Assets/UI/hud.rml", "Assets/UI/hud.rml")]
    public void UpgradeLegacyPathRenamesOnlyTheLegacyFolder(string path, string expected) =>
        Assert.Equal(expected, ContentPaths.UpgradeLegacyPath(path));

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
        Assert.True(File.Exists(ContentPaths.Resolve("Assets/Shaders/Shadows/Shadow2D.vk.vert.spv")));
    }

    [Fact]
    public void ContentPathsResolveInTheProjectDirectoryFirstWhenTheFileExistsThere()
    {
        var project = Directory.CreateTempSubdirectory("mf-project").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(project, "Assets", "Sky"));
            File.WriteAllText(Path.Combine(project, "Assets", "Sky", "only-in-project.txt"), "x");
            ContentPaths.ProjectDirectory = project;

            Assert.Equal(Path.Combine(project, "Assets", "Sky", "only-in-project.txt"),
                ContentPaths.Resolve("Assets/Sky/only-in-project.txt"));
            // Missing in the project: next to the application, as before.
            Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", "Sky", "missing.txt")),
                ContentPaths.Resolve("Assets/Sky/missing.txt"));
            // Engine-relative paths never look in the project.
            Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", "Sky", "only-in-project.txt")),
                ContentPaths.Resolve("Sky/only-in-project.txt"));
        }
        finally
        {
            ContentPaths.ProjectDirectory = null;
            Directory.Delete(project, recursive: true);
        }
    }
}
