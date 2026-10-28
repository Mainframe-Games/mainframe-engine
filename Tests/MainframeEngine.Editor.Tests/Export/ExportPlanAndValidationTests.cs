namespace MainframeEngine.Editor.Tests.Export;

/// <summary><see cref="ExportPlan"/> (paths, effective values, publish arguments) and every <see cref="ExportValidator"/> code.</summary>
public sealed class ExportPlanAndValidationTests : IDisposable
{
    private readonly ExportFixture _game = new();

    public void Dispose() => _game.Dispose();

    private ExportPlan Plan(ExportPreset preset, ExportHost host = ExportHost.Linux, Dictionary<string, string>? variables = null) =>
        ExportPlan.Create(_game.Root, _game.Settings, preset, _game.Environment(host, variables));

    private IReadOnlyList<ExportIssue> Validate(ExportPreset preset, ExportHost host = ExportHost.Linux, ExportEnvironment? environment = null) =>
        ExportValidator.Validate(_game.Root, _game.Settings, preset, environment ?? _game.Environment(host));

    private static string[] Codes(IReadOnlyList<ExportIssue> issues, ExportIssueSeverity severity) =>
        [.. issues.Where(i => i.Severity == severity).Select(i => i.Code)];

    // ── Plan ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePlanDerivesNamesAndFolders()
    {
        var plan = Plan(ExportPreset.Create("Windows (Steam demo)", ExportPlatform.Windows));

        Assert.Equal(("win-x64", "1.2.0", "TinyGame", "Tiny Game"), (plan.Rid, plan.Version, plan.Exe, plan.AppName));
        Assert.Equal("TinyGame.Desktop", plan.AssemblyName);
        Assert.Equal(("TinyGame.exe", "TinyGame.Desktop.exe"), (plan.ExecutableFile, plan.PublishedApphost));
        Assert.Equal(Path.Combine(_game.Root, "export", "windows-steam-demo", "TinyGame-1.2.0-win-x64.zip"), plan.ArtifactPath);
        Assert.Equal(Path.Combine(_game.Root, ".mainframe", "export", "obj", "win-x64-full"), plan.ArtifactsPath);
        Assert.Equal(Path.Combine(_game.Root, ".mainframe", "export", "stage", "windows-steam-demo"), plan.StageDirectory);
        Assert.Equal(_game.PathOf("Assets/icon.png"), plan.IconPng);
        Assert.Equal("com.mainframegames.tinygame", plan.BundleId);
        Assert.Equal("-", plan.SignIdentity);
    }

    [Fact]
    public void PresetValuesAndTheEnvironmentWin()
    {
        var preset = ExportPreset.Create("Mac", ExportPlatform.MacOS);
        preset.Version = "3.0.0";
        preset.Demo = true;
        preset.Output = "/abs/out";
        preset.Archive = false;
        preset.Desktop.Exe = "Tiny";
        preset.Desktop.Rid = "osx-x64";
        preset.Desktop.MacOs!.BundleId = "com.example.tiny";
        preset.Desktop.MacOs.Sign = "Developer ID Application: A";
        preset.Desktop.MacOs.NotaryProfile = "p";
        var plan = Plan(preset, ExportHost.MacOS, new() { ["MF_SIGN_IDENTITY"] = "Developer ID Application: B" });

        Assert.Equal(("osx-x64", "3.0.0", "Tiny", "com.example.tiny"), (plan.Rid, plan.Version, plan.Exe, plan.BundleId));
        Assert.Equal("Developer ID Application: B", plan.SignIdentity);
        Assert.Equal("p", plan.NotaryProfile);
        Assert.Equal(Path.Combine(Path.GetFullPath("/abs/out"), "Tiny-3.0.0-osx-x64"), plan.ArtifactPath); // C:\abs\out on Windows
        Assert.EndsWith("osx-x64-demo", plan.ArtifactsPath, StringComparison.Ordinal);

        _game.Settings.Version = "";
        Assert.Equal("0.0.0", Plan(ExportPreset.Create("L", ExportPlatform.Linux)).Version);
        Assert.EndsWith("TinyGame-0.0.0-linux-x64.tar.gz", Plan(ExportPreset.Create("L", ExportPlatform.Linux)).ArtifactPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePublishArgumentsFollowTheOptions()
    {
        var preset = ExportPreset.Create("W", ExportPlatform.Windows);
        var args = Plan(preset).PublishArguments("/stage/icon.ico");

        Assert.Equal(["publish", _game.PathOf("TinyGame.Desktop/TinyGame.Desktop.csproj"), "-c", "Release", "-r", "win-x64", "--self-contained", "true"], args.Take(8));
        Assert.Contains("-p:MainframeDemo=false", args);
        Assert.Contains("-p:PublishReadyToRun=true", args);
        Assert.Contains("-p:PublishSingleFile=false", args);
        Assert.Contains("-p:PublishTrimmed=false", args);
        Assert.Contains("-p:MainframeWindowsGui=true", args);
        Assert.Contains("-p:ApplicationIcon=/stage/icon.ico", args);
        Assert.DoesNotContain("-p:IncludeNativeLibrariesForSelfExtract=true", args);
        Assert.DoesNotContain("-p:MainframeProfilerDisabled=true", args);
        var artifacts = args.ToList().IndexOf("--artifacts-path");
        Assert.Equal(Plan(preset).ArtifactsPath, args[artifacts + 1]);
        Assert.Equal(Plan(preset).PublishDirectory, args[args.ToList().IndexOf("-o") + 1]);

        preset.Demo = true;
        preset.Configuration = "Debug";
        preset.Desktop.SelfContained = false;
        preset.Desktop.ReadyToRun = false;
        preset.Desktop.SingleFile = true;
        preset.Desktop.Trim = true;
        preset.Desktop.Profiler = false;
        var changed = Plan(preset).PublishArguments(null);
        Assert.Contains("-p:MainframeProfilerDisabled=true", changed);
        Assert.Equal(["-c", "Debug"], changed.Skip(2).Take(2));
        Assert.Equal("false", changed[changed.ToList().IndexOf("--self-contained") + 1]);
        Assert.Contains("-p:MainframeDemo=true", changed);
        Assert.Contains("-p:PublishReadyToRun=false", changed);
        Assert.Contains("-p:PublishSingleFile=true", changed);
        Assert.Contains("-p:IncludeNativeLibrariesForSelfExtract=true", changed);
        Assert.Contains("-p:PublishTrimmed=true", changed);
        Assert.DoesNotContain(changed, a => a.StartsWith("-p:ApplicationIcon", StringComparison.Ordinal));

        var mac = Plan(ExportPreset.Create("M", ExportPlatform.MacOS), ExportHost.MacOS).PublishArguments(null);
        Assert.Contains("-p:MacBundleAppDirectory=MonoBundle", mac);
        Assert.DoesNotContain("-p:MainframeWindowsGui=true", mac);
        Assert.Equal("osx-arm64", mac[mac.ToList().IndexOf("-r") + 1]);
    }

    [Fact]
    public void TheDesktopProjectsOwnIconsWinOverWindowIcon()
    {
        _game.Write("TinyGame.Desktop/TinyGame.Desktop.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><AssemblyName>Tiny.Launcher</AssemblyName>
            <ApplicationIcon>tiny.ico</ApplicationIcon><MacAppIcon>../Brand/tiny.icns</MacAppIcon></PropertyGroup></Project>
            """);
        var plan = Plan(ExportPreset.Create("W", ExportPlatform.Windows));
        Assert.Equal("Tiny.Launcher", plan.AssemblyName);
        Assert.True(plan.ProjectHasWindowsIcon);
        Assert.Equal(_game.PathOf("Brand/tiny.icns"), plan.ProjectMacIcns);

        var explicitIcon = ExportPreset.Create("W2", ExportPlatform.Windows);
        explicitIcon.Icon = "Assets/icon.png";
        Assert.False(Plan(explicitIcon).ProjectHasWindowsIcon);
        Assert.Null(Plan(explicitIcon).ProjectMacIcns);
    }

    // ── Validation ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AGoodPresetHasNoIssues()
    {
        Assert.Empty(Validate(ExportPreset.Create("Linux", ExportPlatform.Linux)));
        Assert.Empty(Validate(ExportPreset.Create("Windows", ExportPlatform.Windows)));
        Assert.Empty(Validate(ExportPreset.Create("macOS", ExportPlatform.MacOS), ExportHost.MacOS));
    }

    [Fact]
    public void MissingSdkDesktopProjectAndMainSceneAreErrors()
    {
        var noSdk = new ExportEnvironment
        {
            DotnetPath = null,
            Sdk = new DotnetSdkInfo(null, [], null, false, "The .NET 10 SDK is required … https://dotnet.microsoft.com/download/dotnet/10.0"),
            Host = ExportHost.Linux,
            HostRid = "linux-x64",
        };
        var issues = Validate(ExportPreset.Create("L", ExportPlatform.Linux), environment: noSdk);
        Assert.Equal(["sdk-missing"], Codes(issues, ExportIssueSeverity.Error));
        Assert.EndsWith(DotnetSdk.DownloadUrl, issues[0].Message, StringComparison.Ordinal);

        Directory.Delete(_game.PathOf("TinyGame.Desktop"), recursive: true);
        _game.Settings.MainScene = "Assets/Scenes/Nope.mscene";
        Assert.Equal(["no-desktop-project", "main-scene"], Codes(Validate(ExportPreset.Create("L", ExportPlatform.Linux)), ExportIssueSeverity.Error));
        _game.Settings.MainScene = null;
        Assert.Contains("main-scene", Codes(Validate(ExportPreset.Create("L", ExportPlatform.Linux)), ExportIssueSeverity.Error));
        _game.Settings.MainScene = "scn_aaaaaaaaaaaa"; // a UID resolves through the asset database
        Assert.DoesNotContain("main-scene", Codes(Validate(ExportPreset.Create("L", ExportPlatform.Linux)), ExportIssueSeverity.Error));
    }

    [Fact]
    public void ExcludingTheMainSceneAutoloadsOrTheBusLayoutIsAnError()
    {
        var preset = ExportPreset.Create("L", ExportPlatform.Linux);
        preset.Exclude.Add("Assets/Scenes/**");
        preset.Exclude.Add("Assets/Settings/**");
        _game.Write("Assets/Auto/Hud.mscene", """{ "uid": "scn_eeeeeeeeeeee" }""");
        _game.Settings.Autoloads.Add(new AutoloadSettings { Name = "Hud", Scene = "Assets/Auto/Hud.mscene" });
        preset.Exclude.Add("Assets/Auto/*");

        var errors = Validate(preset).Where(i => i.IsError).ToArray();
        Assert.Equal(3, errors.Count(e => e.Code == "main-scene-excluded"));
        Assert.Contains(errors, e => e.Message.Contains("Assets/Scenes/Main.mscene", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("autoload Hud", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("bus layout", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("osx-arm64", ExportPlatform.Windows)]
    [InlineData("win-arm64", ExportPlatform.Windows)]
    [InlineData("linux-arm64", ExportPlatform.Linux)]
    public void RidsWithoutEngineNativesAreRejected(string rid, ExportPlatform platform)
    {
        var preset = ExportPreset.Create("P", platform);
        preset.Desktop.Rid = rid;
        Assert.Contains("rid-unsupported", Codes(Validate(preset), ExportIssueSeverity.Error));
    }

    [Theory]
    [InlineData(ExportHost.Linux, true)]
    [InlineData(ExportHost.Windows, true)]
    [InlineData(ExportHost.MacOS, false)]
    public void MacOsNeedsAMac(ExportHost host, bool error) =>
        Assert.Equal(error, Codes(Validate(ExportPreset.Create("M", ExportPlatform.MacOS), host), ExportIssueSeverity.Error).Contains("host-unsupported"));

    [Fact]
    public void OutputInsideAssetsDuplicateNamesAndBadGlobsAreErrors()
    {
        var preset = ExportPreset.Create("Same", ExportPlatform.Linux);
        preset.Output = "Assets/Builds";
        preset.Include.Add("/abs/**");
        _game.Settings.ExportPresets.Add(preset);
        _game.Settings.ExportPresets.Add(ExportPreset.Create("Same", ExportPlatform.Windows));

        var errors = Codes(Validate(preset), ExportIssueSeverity.Error);
        Assert.Contains("output-in-content", errors);
        Assert.Contains("duplicate-name", errors);
        Assert.Contains("glob", errors);
    }

    [Fact]
    public void SteamChecksTheAppIdAndWarnsForDebug()
    {
        var preset = ExportPreset.Create("Steam", ExportPlatform.Windows);
        preset.Desktop.Steam = true;
        Assert.Contains("steam-app-id", Codes(Validate(preset), ExportIssueSeverity.Error));

        _game.Settings.Steam.AppId = 480;
        Assert.Empty(Validate(preset));
        preset.Demo = true; // the demo's id is still 0
        Assert.Contains("steam-app-id", Codes(Validate(preset), ExportIssueSeverity.Error));
        _game.Settings.Steam.DemoAppId = 481;
        preset.Configuration = "Debug";
        var issues = Validate(preset);
        Assert.Empty(Codes(issues, ExportIssueSeverity.Error));
        Assert.Equal(["steam-debug"], Codes(issues, ExportIssueSeverity.Warning));
    }

    [Fact]
    public void NotarizationNeedsASignature()
    {
        var preset = ExportPreset.Create("M", ExportPlatform.MacOS);
        preset.Desktop.MacOs!.NotaryProfile = "profile";
        Assert.Contains("notarize-needs-sign", Codes(Validate(preset, ExportHost.MacOS), ExportIssueSeverity.Error));
        preset.Desktop.MacOs.Sign = "Developer ID Application: X";
        Assert.DoesNotContain("notarize-needs-sign", Codes(Validate(preset, ExportHost.MacOS), ExportIssueSeverity.Error));
    }

    [Fact]
    public void ExperimentalOptionsVersionsAndIconsWarn()
    {
        var preset = ExportPreset.Create("L", ExportPlatform.Windows);
        preset.Desktop.SingleFile = true;
        preset.Desktop.Trim = true;
        preset.Version = "v2";
        var warnings = Codes(Validate(preset), ExportIssueSeverity.Warning);
        Assert.Equal(["single-file", "trim", "version"], warnings);

        var mac = ExportPreset.Create("M", ExportPlatform.MacOS);
        mac.Desktop.SingleFile = true;
        Assert.Contains("single-file", Codes(Validate(mac, ExportHost.MacOS), ExportIssueSeverity.Error));

        _game.WriteIcon("Assets/small.png", 64);
        var small = ExportPreset.Create("S", ExportPlatform.Windows);
        small.Icon = "Assets/small.png";
        Assert.Equal(["icon"], Codes(Validate(small), ExportIssueSeverity.Warning));
        small.Icon = "Assets/Textures/a.png"; // not a PNG
        Assert.Contains("not a PNG", Validate(small).Single().Message, StringComparison.Ordinal);
        small.Icon = "Assets/missing.png";
        Assert.Contains("does not exist", Validate(small).Single().Message, StringComparison.Ordinal);
        var linux = ExportPreset.Create("Linux has no icon", ExportPlatform.Linux);
        linux.Icon = "Assets/missing.png";
        Assert.Empty(Validate(linux));
    }

    [Fact]
    public void ExcludedReferencesAndMetasWarn()
    {
        var preset = ExportPreset.Create("L", ExportPlatform.Linux);
        preset.Exclude.Add("Assets/Music/**");
        preset.Exclude.Add("Assets/Textures/*.meta");
        var warnings = Validate(preset).Where(i => !i.IsError).ToArray();

        Assert.Equal(["broken-reference", "meta-excluded"], warnings.Select(w => w.Code).Order());
        Assert.Contains("Assets/Scenes/Main.mscene uses Assets/Music/theme.mres", warnings.Single(w => w.Code == "broken-reference").Message, StringComparison.Ordinal);
        Assert.Contains("Assets/Textures/a.png.meta", warnings.Single(w => w.Code == "meta-excluded").Message, StringComparison.Ordinal);
    }
}
