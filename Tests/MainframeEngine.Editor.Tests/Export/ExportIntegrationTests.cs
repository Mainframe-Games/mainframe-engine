using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MainframeEngine.Editor.Tests.Export;

/// <summary>
/// A real export with the real <c>dotnet</c>: a game created from this checkout's template (private hive, temp folder), a
/// preset for this machine's RID exported to a folder, its layout checked (renamed executable, the exported
/// <c>project.mfproj</c> without presets, the asset index, no other platform's natives, no <c>.po</c>), then run with
/// <c>--headless</c>; and a demo export whose game assembly says <c>MainframeDemo=true</c>. Slow (two Release publishes of
/// the engine); skipped without a .NET 10 SDK.
/// </summary>
[Trait("Category", "Slow")]
[Collection(nameof(SerialEditor))]
public sealed class ExportIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = GameProjectLayout.RealPath(Directory.CreateTempSubdirectory("mf-export-e2e").FullName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Build servers may still hold files for a moment; the temp folder is cleaned eventually.
        }
    }

    [Fact]
    public async Task ExportsATemplateGameForThisMachineAndRunsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var sdk = await DotnetSdk.DetectAsync(ct: ct);
        if (!sdk.IsSupported)
            Assert.Skip(sdk.Error ?? "No .NET 10 SDK.");
        var checkout = TemplateLocator.FindEngineCheckout();
        Assert.NotNull(checkout);
        var template = TemplateLocator.FindTemplate(checkout);
        Assert.NotNull(template);
        var creator = new ProjectCreator(sdk.DotnetPath!, template, Path.Combine(_directory, "hive"));
        var created = await creator.CreateAsync(new NewProjectRequest("ExportedGame", _directory, checkout), ct: ct);
        Assert.True(created.Succeeded, created.Error + "\n" + created.Log);
        var project = created.ProjectDirectory;
        File.WriteAllText(Path.Combine(project, "Assets", "notes.po"), "msgid \"\"\nmsgstr \"\"\n");

        var rid = ExportTargets.HostRid(ExportTargets.CurrentHost, ExportTargets.CurrentRid);
        var platform = ExportTargets.PlatformOf(rid)!.Value;
        var settings = ProjectSettings.Load(project);
        var full = ExportPreset.Create("Here", platform);
        full.Desktop.Rid = rid;
        full.Desktop.ReadyToRun = false; // faster; ReadyToRun is the same publish with one property
        full.Archive = false;
        full.Output = Path.Combine(_directory, "out");
        var demo = full.Clone();
        demo.Name = "Here (demo)";
        demo.Demo = true;
        demo.Output = Path.Combine(_directory, "out-demo");
        settings.ExportPresets.AddRange([full, demo]);
        settings.Save(project);

        var environment = new ExportEnvironment { DotnetPath = sdk.DotnetPath, Sdk = sdk };
        var lines = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        var results = await new ExportRunner(environment).RunAllAsync(project, settings.ExportPresets, null, line =>
        {
            lock (lines)
                lines.Add(line);
        }, ct);
        output.WriteLine($"two exports: {stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s");
        foreach (var result in results)
            Assert.True(result.Succeeded, result.Summary + "\n" + string.Join('\n', lines.TakeLast(40)));

        var folder = results[0].ArtifactPath!;
        Assert.Equal(Path.Combine(_directory, "out", $"ExportedGame-0.0.0-{rid}"), folder);
        var app = platform == ExportPlatform.MacOS ? Path.Combine(folder, "ExportedGame.app", "Contents", "MonoBundle") : folder;
        var exe = platform switch
        {
            ExportPlatform.MacOS => Path.Combine(folder, "ExportedGame.app", "Contents", "MacOS", "ExportedGame"),
            ExportPlatform.Windows => Path.Combine(folder, "ExportedGame.exe"),
            _ => Path.Combine(folder, "ExportedGame"),
        };
        Assert.True(File.Exists(exe), exe);
        Assert.False(File.Exists(Path.Combine(app, platform == ExportPlatform.Windows ? "ExportedGame.Desktop.exe" : "ExportedGame.Desktop")));
        var exported = ProjectSettings.Load(Path.Combine(app, ProjectSettings.FileName));
        Assert.Empty(exported.ExportPresets);
        Assert.True(File.Exists(Path.Combine(app, "Assets", AssetDatabase.IndexFileName)));
        Assert.False(File.Exists(Path.Combine(app, "Assets", "notes.po")));
        var foreign = EngineNatives.Rids.Where(r => ExportTargets.PlatformOf(r) != platform).SelectMany(EngineNatives.FilesOf)
            .Except(EngineNatives.FilesOf(rid)).Where(f => File.Exists(Path.Combine(app, f))).ToArray();
        Assert.Empty(foreign);
        Assert.False(Directory.Exists(Path.Combine(app, "steamworks", NativesPruner.SteamworksVariant(rid) == "win64" ? "posix" : "win64")));
        Assert.False(IsDemoBuild(Path.Combine(app, "ExportedGame.dll")));

        var demoApp = platform == ExportPlatform.MacOS
            ? Path.Combine(results[1].ArtifactPath!, "ExportedGame.app", "Contents", "MonoBundle")
            : results[1].ArtifactPath!;
        Assert.True(IsDemoBuild(Path.Combine(demoApp, "ExportedGame.dll")));
        Assert.True(ProjectSettings.Load(Path.Combine(demoApp, ProjectSettings.FileName)).IsDemo);

        // The export isolates its intermediates: the editor's Debug bin/ and obj/ are untouched by it.
        Assert.False(Directory.Exists(Path.Combine(project, "ExportedGame.Desktop", "bin", "Release")));
        Assert.True(Directory.Exists(Path.Combine(project, ExportPlan.WorkFolder, "obj", $"{rid}-full")));

        // The build runs on its own, headless.
        var run = await ProcessRunner.RunAsync(exe, ["--headless", "--max-frames", "10"], folder,
            environment: new Dictionary<string, string?> { ["MAINFRAME_USER_DATA"] = Path.Combine(_directory, "userdata") },
            timeout: TimeSpan.FromMinutes(2), cancellationToken: ct);
        Assert.True(run.Succeeded, run.OutputText);
        Assert.DoesNotContain("[ERROR]", run.OutputText, StringComparison.Ordinal);
    }

    // The game assembly's AssemblyMetadata("MainframeDemo", "true") blob (build/MainframeGame.props): SerString name, SerString value.
    private static bool IsDemoBuild(string assembly)
    {
        var bytes = File.ReadAllBytes(assembly);
        var needle = new List<byte> { 13 };
        needle.AddRange(Encoding.UTF8.GetBytes("MainframeDemo"));
        needle.Add(4);
        needle.AddRange(Encoding.UTF8.GetBytes("true"));
        return bytes.AsSpan().IndexOf(needle.ToArray()) >= 0;
    }
}
