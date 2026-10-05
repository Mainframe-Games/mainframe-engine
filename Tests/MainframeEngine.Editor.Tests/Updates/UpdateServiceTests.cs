namespace MainframeEngine.Editor.Tests.Updates;

public sealed class UpdateServiceTests
{
    [Fact]
    public void TheApplierStartsFromTheStagedEditorWithEveryArgumentIntact()
    {
        var service = new GitHubUpdateService(Path.Combine(Path.GetTempPath(), "mf-unused-updates"));
        var staged = new StagedUpdate(new ReleaseVersion(1, 1, 0), "/u/1.1.0", "/u/1.1.0/app/Mainframe Engine.app",
            "/u/1.1.0/app/Mainframe Engine.app/Contents/MacOS/MainframeEngine.Editor");

        var info = service.ApplierStartInfo(staged, "/Users/me/My Games/Space Game");

        Assert.Equal(staged.Executable, info.FileName);
        Assert.False(info.UseShellExecute);
        var request = ApplyUpdateRequest.Parse(info.ArgumentList.ToArray());
        Assert.Equal(Environment.ProcessId, request.WaitPid);
        Assert.Equal(EngineInfo.Version, request.From);
        Assert.Equal("/Users/me/My Games/Space Game", request.Project);
        Assert.Equal(Path.GetFullPath(service.Install.Root), request.InstallRoot);
    }

    [Fact]
    public async Task DevelopmentBuildsDoNotCleanUp()
    {
        // Test builds are 0.0.0-dev: clean-up must not touch anything.
        var updates = Directory.CreateTempSubdirectory("mf-dev-updates").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(updates, "1.0.0"));
            Assert.Null(await new GitHubUpdateService(updates).CleanUpAsync());
            Assert.True(Directory.Exists(Path.Combine(updates, "1.0.0")));
        }
        finally
        {
            Directory.Delete(updates, recursive: true);
        }
    }

    [Fact]
    public void OnlyTheEditorExecutableGetsAnUpdateService()
    {
        Assert.IsType<GitHubUpdateService>(EditorCommandLine.Parse([]).Workspace.Updates);
        Assert.Null(EditorCommandLine.Parse(["--hidden"]).Workspace.Updates);
        Assert.Null(EditorCommandLine.Parse(["--smoke", Path.GetTempPath()]).Workspace.Updates);
    }
}
