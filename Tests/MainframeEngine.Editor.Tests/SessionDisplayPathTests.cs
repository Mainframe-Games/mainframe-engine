namespace MainframeEngine.Editor.Tests;

[Collection(nameof(SerialEditor))]
public sealed class SessionDisplayPathTests
{
    [Fact]
    public void ProjectFilesShowRelativeToTheProjectAndOthersInFull()
    {
        using var host = new SessionHost();
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "x.mscene");
        Assert.Equal(Path.GetFullPath(outside), host.Session.DisplayPath(outside)); // no project yet

        new ProjectSettings { Name = "Shown" }.Save(host.ProjectDirectory);
        host.Session.OpenProject(host.ProjectDirectory);
        Assert.Equal("Content/Scenes/Main.mscene", host.Session.DisplayPath(host.ScenePath("Main.mscene")));
        Assert.Equal(ProjectSettings.FileName, host.Session.DisplayPath(Path.Combine(host.ProjectDirectory, ProjectSettings.FileName)));
        Assert.Equal(Path.GetFullPath(outside), host.Session.DisplayPath(outside));
        Assert.Equal(Path.GetFullPath(host.ProjectDirectory), host.Session.DisplayPath(host.ProjectDirectory));
        Assert.Equal("", host.Session.DisplayPath(null));
    }
}
