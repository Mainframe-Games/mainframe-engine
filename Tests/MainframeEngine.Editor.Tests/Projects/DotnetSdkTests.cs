namespace MainframeEngine.Editor.Tests.Projects;

public sealed class DotnetSdkParsingTests
{
    [Fact]
    public void NoSdksIsUnsupportedWithTheDownloadLink()
    {
        var info = DotnetSdk.FromListSdksOutput("/usr/local/share/dotnet/dotnet", []);
        Assert.False(info.IsSupported);
        Assert.Empty(info.Versions);
        Assert.Null(info.BestVersion);
        Assert.Contains("no .NET SDK is installed", info.Error, StringComparison.Ordinal);
        Assert.EndsWith($"{DotnetSdk.DownloadUrl} and restart the editor.", info.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyOlderSdksAreUnsupported()
    {
        var info = DotnetSdk.FromListSdksOutput("dotnet", ["6.0.428 [/usr/share/dotnet/sdk]", "8.0.404 [/usr/share/dotnet/sdk]"]);
        Assert.False(info.IsSupported);
        Assert.Equal(["6.0.428", "8.0.404"], info.Versions);
        Assert.Equal("8.0.404", info.BestVersion);
        Assert.StartsWith("The .NET 10 SDK is required to create and build game projects", info.Error, StringComparison.Ordinal);
        Assert.Contains("8.0.404", info.Error, StringComparison.Ordinal);
        Assert.Contains(DotnetSdk.DownloadUrl, info.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Net10IsSupported()
    {
        var info = DotnetSdk.FromListSdksOutput("dotnet", ["8.0.404 [/usr/local/share/dotnet/sdk]", "10.0.401 [/usr/local/share/dotnet/sdk]", "10.0.100 [/usr/local/share/dotnet/sdk]"]);
        Assert.True(info.IsSupported);
        Assert.Null(info.Error);
        Assert.Equal("10.0.401", info.BestVersion);
        Assert.Equal(3, info.Versions.Count);
        Assert.Equal("dotnet", info.DotnetPath);
    }

    [Theory]
    [InlineData("10.0.100-rc.2.25502.107", "10.0.100-rc.2.25502.107")]
    [InlineData("10.0.100-preview.7.25380.108|10.0.100-rc.1.25451.107", "10.0.100-rc.1.25451.107")]
    [InlineData("10.0.100-rc.2.25502.107|10.0.100", "10.0.100")]
    [InlineData("10.0.100-rc.10.1|10.0.100-rc.9.1", "10.0.100-rc.10.1")]
    [InlineData("11.0.100-preview.1.1|10.0.401", "11.0.100-preview.1.1")]
    public void PrereleasesCountAndSortBelowTheirRelease(string versions, string best)
    {
        var info = DotnetSdk.FromListSdksOutput(null, versions.Split('|').Select(v => $"{v} [C:\\Program Files\\dotnet\\sdk]"));
        Assert.True(info.IsSupported);
        Assert.Equal(best, info.BestVersion);
    }

    [Fact]
    public void GarbageLinesAreIgnored()
    {
        string[] output =
        [
            "",
            "Welcome to .NET!",
            "warning: something [odd]",
            "10.0 [missing patch is fine]",
            "abc.def.ghi [/x]",
            "10.0.401-",
            "   10.0.401 [/usr/local/share/dotnet/sdk]   ",
            "10.0.401 [/usr/local/share/dotnet/sdk]", // duplicate
            "1.2.3.4 [/too/many/parts]",
        ];
        var info = DotnetSdk.FromListSdksOutput("dotnet", output);
        Assert.Equal(["10.0", "10.0.401"], info.Versions);
        Assert.Equal("10.0.401", info.BestVersion);
        Assert.True(info.IsSupported);
    }

    [Fact]
    public void GuardsArguments() =>
        Assert.Throws<ArgumentNullException>(() => DotnetSdk.FromListSdksOutput(null, null!));
}

/// <summary>Environment-variable tests (process-wide state) and the real <c>dotnet</c> on this machine.</summary>
[Collection(nameof(SerialEditor))]
public sealed class DotnetSdkDiscoveryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-dotnet").FullName;
    private readonly string? _previousOverride = Environment.GetEnvironmentVariable(DotnetSdk.OverrideVariable);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DotnetSdk.OverrideVariable, _previousOverride);
        Directory.Delete(_directory, recursive: true);
    }

    private string FakeDotnet()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_directory, "custom dotnet")).FullName;
        var file = Path.Combine(folder, DotnetSdk.ExecutableName);
        File.WriteAllText(file, "");
        return file;
    }

    [Fact]
    public void OverrideVariableWinsAsAFileOrAFolder()
    {
        var fake = FakeDotnet();
        Environment.SetEnvironmentVariable(DotnetSdk.OverrideVariable, fake);
        Assert.Equal(fake, DotnetSdk.FindDotnet());

        Environment.SetEnvironmentVariable(DotnetSdk.OverrideVariable, Path.GetDirectoryName(fake));
        Assert.Equal(fake, DotnetSdk.FindDotnet());
    }

    [Fact]
    public void BrokenOverrideFallsBackToTheNormalSearch()
    {
        Environment.SetEnvironmentVariable(DotnetSdk.OverrideVariable, Path.Combine(_directory, "nowhere"));
        var found = DotnetSdk.FindDotnet();
        Environment.SetEnvironmentVariable(DotnetSdk.OverrideVariable, null);
        Assert.Equal(DotnetSdk.FindDotnet(), found);
    }

    [Fact]
    public void FindsTheDotnetRunningTheseTests()
    {
        Environment.SetEnvironmentVariable(DotnetSdk.OverrideVariable, null);
        var found = DotnetSdk.FindDotnet();
        Assert.NotNull(found); // the tests were built with an SDK, so one is installed
        Assert.True(File.Exists(found));
        Assert.Equal(DotnetSdk.ExecutableName, Path.GetFileName(found));
    }

    [Fact]
    public void WellKnownFoldersIncludeTheUserInstall()
    {
        var folders = DotnetSdk.WellKnownInstallDirectories();
        Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"), folders);
        if (OperatingSystem.IsMacOS())
            Assert.Equal("/usr/local/share/dotnet", folders[0]);
    }

    [Fact]
    public async Task DetectReportsAMissingOrBrokenDotnet()
    {
        var missing = await DotnetSdk.DetectAsync(Path.Combine(_directory, "no-such-dotnet"), TestContext.Current.CancellationToken);
        Assert.False(missing.IsSupported);
        Assert.Contains("could not be run", missing.Error, StringComparison.Ordinal);
        Assert.Contains(DotnetSdk.DownloadUrl, missing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetectFindsTheInstalledSdk()
    {
        Environment.SetEnvironmentVariable(DotnetSdk.OverrideVariable, null);
        var info = await DotnetSdk.DetectAsync(ct: TestContext.Current.CancellationToken);
        Assert.NotNull(info.DotnetPath);
        Assert.NotEmpty(info.Versions);
        Assert.True(info.IsSupported, info.Error); // these tests target net10.0, so a .NET 10 SDK built them
    }
}
