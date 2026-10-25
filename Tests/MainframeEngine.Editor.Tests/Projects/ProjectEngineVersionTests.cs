namespace MainframeEngine.Editor.Tests.Projects;

public sealed class ProjectEngineVersionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mf-engine-version").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_root, relative));

    // The mfgame template's package layout: the version in Directory.Build.props, the reference uses the property.
    private void PackageProject(string version, string mfprojVersion)
    {
        Write("project.mfproj", $$"""
            {
              "format": 4,
              "name": "Game",
              "engineVersion": "{{mfprojVersion}}",
              "mainScene": "Assets/Scenes/Main.mscene"
            }
            """);
        Write("Directory.Build.props", $"""
            <Project>
              <PropertyGroup>
                <MainframeEngineVersion>{version}</MainframeEngineVersion>
              </PropertyGroup>
            </Project>
            """);
        Write("Game/Game.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="MainframeEngine" Version="$(MainframeEngineVersion)" />
              </ItemGroup>
            </Project>
            """);
    }

    [Fact]
    public void APackageProjectReportsItsPackageVersionNotTheProjectFiles()
    {
        PackageProject("1.1.4", mfprojVersion: "0.0.0-dev"); // project.mfproj can be stale (Crash Site Defense)

        var engine = ProjectEngineVersion.Read(_root);

        Assert.Equal(ProjectEngineSource.Package, engine.Source);
        Assert.Equal("1.1.4", engine.Version);
    }

    [Fact]
    public void ALiteralPackageReferenceVersionIsRead()
    {
        Write("project.mfproj", """{ "format": 4, "name": "Old" }""");
        Write("Old/Old.csproj", """<PackageReference Include="MainframeEngine" Version="1.0.3" />""");

        var engine = ProjectEngineVersion.Read(_root);

        Assert.Equal(ProjectEngineSource.Package, engine.Source);
        Assert.Equal("1.0.3", engine.Version);
    }

    [Fact]
    public void AnEngineCheckoutIsSource()
    {
        Write("project.mfproj", """{ "format": 4, "name": "Range", "engineVersion": "0.0.0-dev" }""");
        Write("Range/Range.csproj", """<ProjectReference Include="$(MainframeEnginePath)/MainframeEngine/MainframeEngine.csproj" />""");

        var engine = ProjectEngineVersion.Read(_root);

        Assert.Equal(ProjectEngineSource.Checkout, engine.Source);
        Assert.Null(engine.Version);
    }

    [Fact]
    public void AFolderWithoutAnEngineReferenceIsUnknown()
    {
        Write("project.mfproj", """{ "format": 4, "name": "Bare" }""");
        Assert.Equal(ProjectEngineSource.Unknown, ProjectEngineVersion.Read(_root).Source);
        Assert.Equal(ProjectEngineSource.Unknown, ProjectEngineVersion.Read(Path.Combine(_root, "missing")).Source);
    }

    [Fact]
    public void BuildOutputIsIgnored()
    {
        Write("project.mfproj", """{ "format": 4, "name": "Game" }""");
        Write("Game/obj/Game.csproj.nuget.g.props", """<PackageReference Include="MainframeEngine" Version="9.9.9" />""");
        Assert.Equal(ProjectEngineSource.Unknown, ProjectEngineVersion.Read(_root).Source);
    }

    [Theory]
    [InlineData("1.1.4", "1.1.5", ProjectEngineStatus.Older)]
    [InlineData("1.1.5", "1.1.5", ProjectEngineStatus.Current)]
    [InlineData("1.2.0", "1.1.5", ProjectEngineStatus.Newer)]
    [InlineData("1.1.4", "0.0.0-dev", ProjectEngineStatus.Unversioned)] // a development editor never upgrades
    [InlineData("1.1.4-preview", "1.1.5", ProjectEngineStatus.Unversioned)]
    public void APackageVersionIsComparedWithTheEditors(string project, string editor, ProjectEngineStatus expected)
    {
        var engine = new ProjectEngineVersion(ProjectEngineSource.Package, project);
        Assert.Equal(expected, engine.StatusAgainst(editor));
    }

    [Fact]
    public void ACheckoutIsNeverOlder() =>
        Assert.Equal(ProjectEngineStatus.Unversioned, new ProjectEngineVersion(ProjectEngineSource.Checkout, null).StatusAgainst("1.1.5"));

    [Fact]
    public void UpgradeRewritesThePackageVersionAndTheProjectFile()
    {
        PackageProject("1.1.4", mfprojVersion: "1.1.4");

        var files = ProjectEngineVersion.Upgrade(_root, "1.1.5");

        Assert.Equal(["Directory.Build.props", "project.mfproj"], files);
        Assert.Contains("<MainframeEngineVersion>1.1.5</MainframeEngineVersion>", Read("Directory.Build.props"));
        Assert.Contains("\"engineVersion\": \"1.1.5\"", Read("project.mfproj"));
        Assert.Contains("\"mainScene\": \"Assets/Scenes/Main.mscene\"", Read("project.mfproj")); // the rest is untouched
        Assert.Equal("1.1.5", ProjectEngineVersion.Read(_root).Version);
    }

    [Fact]
    public void UpgradeRewritesALiteralReference()
    {
        Write("project.mfproj", """{ "format": 4, "name": "Old" }""");
        Write("Old/Old.csproj", """<PackageReference Include="MainframeEngine" Version="1.0.3" />""");

        var files = ProjectEngineVersion.Upgrade(_root, "1.1.5");

        Assert.Equal(["Old/Old.csproj"], files); // no engineVersion to update in that project file
        Assert.Contains("Version=\"1.1.5\"", Read("Old/Old.csproj"));
    }

    [Fact]
    public void UpgradeLeavesCurrentNewerAndCheckoutProjectsAlone()
    {
        PackageProject("1.2.0", mfprojVersion: "1.2.0");
        Assert.Empty(ProjectEngineVersion.Upgrade(_root, "1.1.5"));
        Assert.Empty(ProjectEngineVersion.Upgrade(_root, "1.2.0"));
        Assert.Empty(ProjectEngineVersion.Upgrade(_root, "0.0.0-dev"));
        Assert.Contains("<MainframeEngineVersion>1.2.0</MainframeEngineVersion>", Read("Directory.Build.props"));
    }

    [Theory]
    [InlineData(ProjectEngineSource.Package, "1.1.4", "v1.1.4", "old")]
    [InlineData(ProjectEngineSource.Package, "1.1.5", "v1.1.5", "")]
    [InlineData(ProjectEngineSource.Package, "1.2.0", "v1.2.0", "new")]
    [InlineData(ProjectEngineSource.Checkout, null, "source", "source")]
    [InlineData(ProjectEngineSource.Unknown, null, "", "")]
    public void TheBadgeShowsTheVersionAndWhatOpeningDoes(ProjectEngineSource source, string? version, string text, string cssClass)
    {
        var (badge, badgeClass, tooltip) = ProjectManager.EngineBadge(new ProjectEngineVersion(source, version), "1.1.5");
        Assert.Equal(text, badge);
        Assert.Equal(cssClass, badgeClass);
        Assert.Equal(text.Length > 0, tooltip.Length > 0);
        if (cssClass == "old")
            Assert.Contains("updates its MainframeEngine package to 1.1.5", tooltip);
    }
}
