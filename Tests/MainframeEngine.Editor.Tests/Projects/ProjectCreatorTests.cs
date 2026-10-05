namespace MainframeEngine.Editor.Tests.Projects;

/// <summary>ProjectCreator failures that need no real <c>dotnet</c>.</summary>
public sealed class ProjectCreatorTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-creator").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string EngineCheckout()
    {
        var engine = Directory.CreateDirectory(Path.Combine(_directory, "engine", "MainframeEngine")).FullName;
        File.WriteAllText(Path.Combine(engine, "MainframeEngine.csproj"), "<Project />");
        return Path.GetDirectoryName(engine)!;
    }

    private string Template()
    {
        var config = Directory.CreateDirectory(Path.Combine(_directory, "template", ".template.config")).FullName;
        File.WriteAllText(Path.Combine(config, "template.json"), "{}");
        return Path.GetDirectoryName(config)!;
    }

    private ProjectCreator Creator(string? dotnet = null, string? template = null) =>
        new(dotnet ?? Path.Combine(_directory, "no-such-dotnet"), template ?? Template(), Path.Combine(_directory, "hive"));

    [Fact]
    public void VerifyRejectsAProjectWhoseIconIsMissing()
    {
        var target = Path.Combine(_directory, "Game");
        Directory.CreateDirectory(Path.Combine(target, "Content", "Scenes"));
        File.WriteAllText(Path.Combine(target, "Content", "Scenes", "Main.mscene"), "{}");
        File.WriteAllText(Path.Combine(target, ProjectSettings.FileName),
            """{ "format": 1, "name": "Game", "window": { "icon": "Content/icon.png" } }""");

        Assert.Contains("icon", ProjectCreator.Verify(target), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidRequestsFailBeforeRunningAnything()
    {
        var output = new List<string>();
        var result = await Creator().CreateAsync(new NewProjectRequest("bad name", _directory, EngineCheckout()), output.Add, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("cannot contain spaces", result.Error, StringComparison.Ordinal);
        Assert.Contains(result.Error!, output);
        Assert.DoesNotContain("> ", result.Log, StringComparison.Ordinal); // no command ran
        Assert.False(Directory.Exists(Path.Combine(_directory, "hive")));
    }

    [Fact]
    public async Task MissingTemplateFails()
    {
        var creator = Creator(template: Path.Combine(_directory, "nowhere"));
        var result = await creator.CreateAsync(new NewProjectRequest("Game", _directory, EngineCheckout()), ct: TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.Contains("template was not found", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingDotnetFailsWithAClearMessageAndLeavesNothingBehind()
    {
        var creator = Creator();
        var result = await creator.CreateAsync(new NewProjectRequest("Game", _directory, EngineCheckout()), ct: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.StartsWith("Installing the project template (dotnet new install) failed", result.Error, StringComparison.Ordinal);
        Assert.Contains(DotnetSdk.DownloadUrl, result.Error, StringComparison.Ordinal);
        Assert.Contains("> " + creator.DotnetPath + " new install", result.Log, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(GameProjectLayout.RealPath(_directory), "Game"), result.ProjectDirectory);
        Assert.False(Directory.Exists(result.ProjectDirectory));
        Assert.False(creator.IsTemplateInstalled());
    }

    [Fact]
    public async Task CancellingKillsTheCommandAndFails()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Uses a shell script as a stand-in for dotnet.");
        var fake = Path.Combine(_directory, "slow-dotnet");
        File.WriteAllText(fake, "#!/bin/sh\nsleep 60\n");
        if (!OperatingSystem.IsWindows()) // skipped above; for the platform analyzer
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(300));
        var result = await Creator(fake).CreateAsync(new NewProjectRequest("Game", _directory, EngineCheckout()), ct: cancel.Token);

        Assert.False(result.Succeeded);
        Assert.Equal("Creating the project was cancelled.", result.Error);
        Assert.True(result.Duration < TimeSpan.FromSeconds(30), $"took {result.Duration}");
        Assert.False(Directory.Exists(result.ProjectDirectory));
    }

    [Fact]
    public async Task DefaultsAndGuards()
    {
        var creator = new ProjectCreator("dotnet", Template() + Path.DirectorySeparatorChar);
        Assert.Equal(ProjectCreator.DefaultHiveDirectory, creator.HiveDirectory);
        Assert.EndsWith(Path.Combine(".mainframe", "templates"), ProjectCreator.DefaultHiveDirectory, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_directory, "template"), creator.TemplateDirectory);
        Assert.Throws<ArgumentException>(() => new ProjectCreator("", Template()));
        Assert.Throws<ArgumentNullException>(() => new ProjectCreator("dotnet", null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => creator.CreateAsync(null!, ct: TestContext.Current.CancellationToken));
    }
}
