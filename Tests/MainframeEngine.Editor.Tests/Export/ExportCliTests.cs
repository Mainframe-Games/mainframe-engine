namespace MainframeEngine.Editor.Tests.Export;

/// <summary><c>mf-export</c> and the editor's <c>--export-*</c> flags: parsing (including the legacy package-game form), exit codes, output.</summary>
public sealed class ExportCliTests : IDisposable
{
    private readonly ExportFixture _game = new();

    public void Dispose() => _game.Dispose();

    [Fact]
    public void PresetAllAndListParse()
    {
        Assert.Equal(new ExportCommand(ExportCommandKind.Preset, "/p", "Windows (Steam)", "/out"),
            ExportCli.Parse(["--project", "/p", "--preset", "Windows (Steam)", "--output", "/out"]));
        Assert.Equal(new ExportCommand(ExportCommandKind.All, "/p"), ExportCli.Parse(["--project", "/p", "--all"]));
        Assert.Equal(new ExportCommand(ExportCommandKind.List, "/p"), ExportCli.Parse(["--list", "--project", "/p"]));
        Assert.Equal(ExportCommandKind.Help, ExportCli.Parse([]).Kind);
        Assert.Equal(ExportCommandKind.Help, ExportCli.Parse(["--help"]).Kind);
    }

    [Theory]
    [InlineData("--project", "/p", "--all", "--list")]
    [InlineData("--project", "/p")]
    [InlineData("--project", "/p", "--all", "--output", "/o")]
    [InlineData("--project", "/p", "--bogus")]
    [InlineData("--project")]
    [InlineData("a.csproj", "linux-x64")]
    [InlineData("a.csproj", "android-arm64", "/o")]
    [InlineData("a.csproj", "linux-x64", "/o", "--symbols", "both")]
    public void UsageErrorsThrow(params string[] args) => Assert.Throws<ArgumentException>(() => ExportCli.Parse(args));

    [Fact]
    public void TheLegacyFormMapsOntoAnUnsavedPreset()
    {
        var command = ExportCli.Parse(["/g/MyGame.Desktop/MyGame.Desktop.csproj", "osx-arm64", "/out", "--name", "My Game", "--exe", "MyGame",
            "--version", "1.0.0", "--bundle-id", "com.x.y", "--copyright", "(c)", "--icon", "/i.png", "--sign", "Developer ID Application: X",
            "--notarize", "prof", "--demo", "--steam", "--symbols", "separate"]);

        Assert.Equal(ExportCommandKind.Legacy, command.Kind);
        Assert.Equal(Path.GetFullPath("/g"), command.ProjectDirectory);
        Assert.Equal(Path.GetFullPath("/out"), command.Output);
        Assert.Equal("My Game", command.AppName);
        var preset = command.LegacyPreset!;
        Assert.Equal((ExportPlatform.MacOS, "osx-arm64", "MyGame", "1.0.0"), (preset.Platform, preset.Desktop.Rid, preset.Desktop.Exe, preset.Version));
        Assert.Equal((true, true, ExportSymbols.Separate, Path.GetFullPath("/i.png")), (preset.Demo, preset.Desktop.Steam, preset.Desktop.Symbols, preset.Icon));
        var mac = preset.Desktop.MacOs!;
        Assert.Equal(("com.x.y", "(c)", "Developer ID Application: X", "prof"), (mac.BundleId, mac.Copyright, mac.Sign, mac.NotaryProfile));
        Assert.True(preset.Archive);

        var linux = ExportCli.Parse(["/g/MyGame.Desktop/MyGame.Desktop.csproj", "linux-x64", "/out", "--folder"]).LegacyPreset!;
        Assert.Null(linux.Desktop.MacOs);
        Assert.False(linux.Archive);
    }

    [Fact]
    public void TheEditorFlagsParse()
    {
        Assert.True(ExportCli.IsEditorExport(["--project", "/p", "--export-all"]));
        Assert.False(ExportCli.IsEditorExport(["--project", "/p"]));
        Assert.Equal(new ExportCommand(ExportCommandKind.Preset, "/p", "Linux", "/o", "Release"),
            ExportCli.ParseEditor(["--project", "/p", "--export-release", "Linux", "/o"]));
        Assert.Equal(new ExportCommand(ExportCommandKind.Preset, "/p", "Linux", null, "Debug"),
            ExportCli.ParseEditor(["--export-debug", "Linux", "--project", "/p"]));
        Assert.Equal(new ExportCommand(ExportCommandKind.All, "/p"), ExportCli.ParseEditor(["--project", "/p", "--export-all"]));
        Assert.Throws<ArgumentException>(() => ExportCli.ParseEditor(["--export-all"]));
        Assert.Throws<ArgumentException>(() => ExportCli.ParseEditor(["--project", "/p", "--export-all", "--export-release", "L"]));
    }

    private async Task<(int Exit, string Out, string Err)> Run(ExportCommand command)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await ExportCli.RunAsync(command, stdout, stderr, _game.Environment(), TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task ListPrintsEveryPresetWithItsValidation()
    {
        _game.Settings.ExportPresets.Add(ExportPreset.Create("Linux", ExportPlatform.Linux));
        _game.Settings.ExportPresets.Add(ExportPreset.Create("macOS", ExportPlatform.MacOS));
        _game.Save();

        var (exit, output, _) = await Run(new ExportCommand(ExportCommandKind.List, _game.Root));

        Assert.Equal(ExportCli.UsageError, exit); // the macOS preset cannot export from this (fake Linux) host
        Assert.Contains("✓ \"Linux\"  linux linux-x64  → export", output, StringComparison.Ordinal);
        Assert.Contains("✕ \"macOS\"", output, StringComparison.Ordinal);
        Assert.Contains("error host-unsupported", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APresetExportsAndPrintsItsArtifactLast()
    {
        _game.Settings.ExportPresets.Add(ExportPreset.Create("Linux", ExportPlatform.Linux));
        _game.Save();

        var (exit, output, error) = await Run(new ExportCommand(ExportCommandKind.Preset, _game.Root, "Linux"));

        Assert.Equal(ExportCli.Success, exit);
        Assert.Equal("", error);
        var last = output.TrimEnd().Split('\n')[^1].TrimEnd('\r');
        Assert.Equal(Path.Combine(_game.Root, "export", "linux", "TinyGame-1.2.0-linux-x64.tar.gz"), last);
    }

    [Fact]
    public async Task FailuresUseExitCodesAndMsBuildFormat()
    {
        var unknown = await Run(new ExportCommand(ExportCommandKind.Preset, _game.Root, "Nope"));
        Assert.Equal(ExportCli.UsageError, unknown.Exit);
        Assert.Contains("project.mfproj : error no-preset: No preset is named 'Nope'", unknown.Err, StringComparison.Ordinal);

        var noPresets = await Run(new ExportCommand(ExportCommandKind.All, _game.Root));
        Assert.Equal(ExportCli.UsageError, noPresets.Exit);

        var steam = ExportPreset.Create("Steam", ExportPlatform.Linux);
        steam.Desktop.Steam = true;
        _game.Settings.ExportPresets.Add(steam);
        _game.Save();
        var invalid = await Run(new ExportCommand(ExportCommandKind.Preset, _game.Root, "Steam"));
        Assert.Equal(ExportCli.UsageError, invalid.Exit);
        Assert.Contains("project.mfproj : error steam-app-id: [Steam]", invalid.Err, StringComparison.Ordinal);

        _game.Settings.ExportPresets.Add(ExportPreset.Create("Linux", ExportPlatform.Linux));
        _game.Save();
        _game.FailPublish = true;
        var failed = await Run(new ExportCommand(ExportCommandKind.Preset, _game.Root, "Linux"));
        Assert.Equal(ExportCli.ExportFailed, failed.Exit);
        Assert.Contains("Spinner.cs(3,5) : error CS1002: ; expected", failed.Err, StringComparison.Ordinal);

        var all = await Run(new ExportCommand(ExportCommandKind.All, _game.Root));
        Assert.Equal(ExportCli.ExportFailed, all.Exit);
        Assert.Contains("0 exported, 1 failed, 1 skipped (invalid)", all.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEditorsDebugFlagOverridesTheConfiguration()
    {
        _game.Settings.ExportPresets.Add(ExportPreset.Create("Linux", ExportPlatform.Linux));
        _game.Save();

        var (exit, _, _) = await Run(new ExportCommand(ExportCommandKind.Preset, _game.Root, "Linux", Configuration: "Debug"));

        Assert.Equal(ExportCli.Success, exit);
        var publish = _game.Calls.Single(c => c.File == "/fake/dotnet").Arguments;
        Assert.Equal("Debug", publish[publish.ToList().IndexOf("-c") + 1]);
        Assert.Equal("Release", ProjectSettings.Load(_game.Root).ExportPresets[0].Configuration); // the file is unchanged
    }
}
