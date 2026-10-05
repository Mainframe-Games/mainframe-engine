namespace MainframeEngine.Editor.Tests.Projects;

public sealed class ProjectIconResolverTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-icons").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Project(string? icon, bool writeIcon = true, string? json = null)
    {
        var root = Directory.CreateDirectory(Path.Combine(_directory, Guid.NewGuid().ToString("N"))).FullName;
        var window = icon is null ? "" : $", \"window\": {{ \"icon\": \"{icon}\" }}";
        File.WriteAllText(Path.Combine(root, ProjectSettings.FileName), json ?? $"{{ \"format\": 1, \"name\": \"P\"{window} }}");
        if (icon is not null && writeIcon && !icon.Contains(".."))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, icon))!);
            File.WriteAllBytes(Path.Combine(root, icon), [137, 80, 78, 71]);
        }

        return root;
    }

    [Fact]
    public void IconInsideTheProjectResolvesToAnAbsolutePath()
    {
        var root = Project("Content/icon.png");
        var icon = new ProjectIconResolver().Resolve(root);
        Assert.Equal(Path.Combine(root, "Content", "icon.png"), icon.Path);
    }

    [Fact]
    public void NoIconSettingMeansNoIcon() => Assert.Null(new ProjectIconResolver().Resolve(Project(null)).Path);

    [Fact]
    public void MissingIconFileMeansNoIcon() => Assert.Null(new ProjectIconResolver().Resolve(Project("Content/icon.png", writeIcon: false)).Path);

    [Fact]
    public void NonPngIconIsIgnored() => Assert.Null(new ProjectIconResolver().Resolve(Project("Content/icon.jpg")).Path);

    [Fact]
    public void IconOutsideTheProjectIsIgnored()
    {
        File.WriteAllBytes(Path.Combine(_directory, "outside.png"), [1]);
        Assert.Null(new ProjectIconResolver().Resolve(Project("../outside.png")).Path);
    }

    [Fact]
    public void MalformedProjectFileHasNoIcon() =>
        Assert.Null(new ProjectIconResolver().Resolve(Project(null, json: "{ not json")).Path);

    [Fact]
    public void ChangedIconIsReportedOnce()
    {
        var root = Project("Content/icon.png");
        var resolver = new ProjectIconResolver();
        Assert.True(resolver.Resolve(root).Changed);   // first sight
        Assert.False(resolver.Resolve(root).Changed);
        File.SetLastWriteTimeUtc(Path.Combine(root, "Content", "icon.png"), DateTime.UtcNow.AddMinutes(1));
        Assert.True(resolver.Resolve(root).Changed);
        Assert.False(resolver.Resolve(root).Changed);
    }

    [Fact]
    public void IconCreatedAfterFirstResolveIsPickedUp()
    {
        var root = Project("Content/icon.png", writeIcon: false);
        var resolver = new ProjectIconResolver();

        // First resolve: icon doesn't exist
        var first = resolver.Resolve(root);
        Assert.Null(first.Path);
        Assert.True(first.Changed);  // first sight

        // Create the icon file and update project.mfproj mtime
        Directory.CreateDirectory(Path.Combine(root, "Content"));
        File.WriteAllBytes(Path.Combine(root, "Content", "icon.png"), [137, 80, 78, 71]);
        File.SetLastWriteTimeUtc(Path.Combine(root, ProjectSettings.FileName), DateTime.UtcNow.AddMinutes(1));

        // Second resolve: icon now exists
        var second = resolver.Resolve(root);
        Assert.NotNull(second.Path);
        Assert.True(second.Changed);
    }

    [Fact]
    public void DeletedIconIsReported()
    {
        var root = Project("Content/icon.png");
        var resolver = new ProjectIconResolver();

        // First resolve
        var first = resolver.Resolve(root);
        Assert.NotNull(first.Path);
        Assert.True(first.Changed);

        // Delete the icon file
        File.Delete(Path.Combine(root, "Content", "icon.png"));

        // Second resolve: icon is deleted
        var second = resolver.Resolve(root);
        Assert.Null(second.Path);
        Assert.True(second.Changed);

        // Third resolve: still deleted
        var third = resolver.Resolve(root);
        Assert.Null(third.Path);
        Assert.False(third.Changed);
    }

    [Fact]
    public void RemovingWindowIconFromProjectIsReported()
    {
        var root = Project("Content/icon.png");
        var resolver = new ProjectIconResolver();

        // First resolve
        var first = resolver.Resolve(root);
        Assert.NotNull(first.Path);

        // Edit project.mfproj to remove window.icon
        File.WriteAllText(Path.Combine(root, ProjectSettings.FileName), """{"format": 1, "name": "P"}""");

        // Second resolve
        var second = resolver.Resolve(root);
        Assert.Null(second.Path);
        Assert.True(second.Changed);
    }

    [Fact]
    public void SiblingPathTraversalIsIgnored()
    {
        var root = Project("../icon.png");
        var icon = new ProjectIconResolver().Resolve(root);
        Assert.Null(icon.Path);
    }

    [Fact]
    public void UppercaseExtensionIsAccepted()
    {
        var root = Project("Content/icon.PNG");
        var icon = new ProjectIconResolver().Resolve(root);
        Assert.NotNull(icon.Path);
    }

    [Fact]
    public void NullCharacterInPathDoesNotThrow()
    {
        var root = Directory.CreateDirectory(Path.Combine(_directory, Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(root, ProjectSettings.FileName), """{"format": 1, "name": "P", "window": {"icon": "Content\0/icon.png"}}""");
        var icon = new ProjectIconResolver().Resolve(root);
        Assert.Null(icon.Path);
    }

    [Fact]
    public void NoProjectFileReturnsDefaultImmediately()
    {
        var root = Directory.CreateDirectory(Path.Combine(_directory, Guid.NewGuid().ToString("N"))).FullName;
        // No project file created, so project.mfproj doesn't exist
        var icon = new ProjectIconResolver().Resolve(root);
        Assert.Null(icon.Path);
        Assert.False(icon.Changed);
    }
}
