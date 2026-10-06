using System.Diagnostics;
using System.Globalization;

namespace MainframeEngine.Editor.Tests.Projects;

/// <summary>
/// The whole New Project path with the real <c>dotnet</c>: create a game from this checkout's template (private hive),
/// build it warnings-as-errors, find the game library's build output, and open its main scene in an editor session.
/// Slow (a cold engine build when nothing is built yet); skipped without a .NET 10 SDK.
/// </summary>
[Trait("Category", "Slow")]
[Collection(nameof(SerialEditor))]
public sealed class ProjectCreationIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);

    private readonly string _directory = Directory.CreateTempSubdirectory("mf-newgame").FullName;
    private readonly AssetDatabase _previousDatabase = AssetDatabase.Current;
    private readonly string? _previousProject = ContentPaths.ProjectDirectory;

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousDatabase;
        ContentPaths.ProjectDirectory = _previousProject;
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
    public async Task CreatesBuildsAndOpensANewGame()
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
        Assert.False(creator.IsTemplateInstalled());

        // First creation installs the template; the second one reuses the installation (its time is creation only).
        var first = await creator.CreateAsync(new NewProjectRequest("CreatedGame", _directory, checkout), ct: ct);
        Assert.True(first.Succeeded, first.Error + "\n" + first.Log);
        Assert.True(creator.IsTemplateInstalled());
        var second = await creator.CreateAsync(new NewProjectRequest("SecondGame", _directory, checkout), ct: ct);
        Assert.True(second.Succeeded, second.Error + "\n" + second.Log);
        Assert.Contains("Template already installed", second.Log, StringComparison.Ordinal);
        Assert.DoesNotContain(" new install ", second.Log, StringComparison.Ordinal);
        output.WriteLine($"install + create: {Seconds(first.Duration)}, create only: {Seconds(second.Duration)}, " +
                         $"template install ≈ {Seconds(first.Duration - second.Duration)}");

        var project = first.ProjectDirectory;
        Assert.Equal(Path.Combine(GameProjectLayout.RealPath(_directory), "CreatedGame"), project); // temp folders are symlinked on macOS
        var settings = ProjectSettings.Load(project);
        Assert.Equal("CreatedGame", settings.Name);
        Assert.Equal("Content/Scenes/Main.mscene", settings.MainScene);
        Assert.Equal(["CreatedGame"], settings.Assemblies);
        Assert.Equal(EngineInfo.Version, settings.EngineVersion);

        var solution = GameProjectLayout.SolutionOf(project);
        Assert.Equal(Path.Combine(project, "CreatedGame.slnx"), solution);
        Assert.Equal(Path.Combine(project, "CreatedGame.Desktop", "CreatedGame.Desktop.csproj"), GameProjectLayout.DesktopProjectOf(project));
        var library = GameProjectLayout.GameLibraryProjectOf(project, settings);
        Assert.Equal(Path.Combine(project, "CreatedGame", "CreatedGame.csproj"), library);

        // The same build Play runs; warnings are errors, as for the template smoke test, which also uses the committed
        // SPIR-V (CompileShaders=false): without glslc (CI runners) the shader step would warn and fail the build. A fresh
        // handshake salt makes MSBuild start new worker nodes (a cold editor's first Play), which inherit the output and
        // outlive the build.
        var stopwatch = Stopwatch.StartNew();
        var build = await ProcessRunner.RunAsync(sdk.DotnetPath!, ["build", solution!, "-warnaserror", "-nologo", "-p:CompileShaders=false"], workingDirectory: project,
            environment: new Dictionary<string, string?> { ["MSBUILDNODEHANDSHAKESALT"] = Guid.NewGuid().ToString("N") },
            timeout: BuildTimeout, cancellationToken: ct);
        output.WriteLine($"build: {Seconds(stopwatch.Elapsed)} (exit {build.ExitCode})");
        Assert.True(build.Succeeded, string.Join('\n', build.Output.TakeLast(40)));

        var dll = GameAssemblyLoader.FindBuildOutput(library!);
        Assert.NotNull(dll);
        Assert.Equal("CreatedGame.dll", Path.GetFileName(dll));

        OpenInTheEditor(project);
    }

    // The scene opens in the editor model without the game assembly: its Spinner is a MissingNode until it loads.
    private static void OpenInTheEditor(string project)
    {
        var tree = new SceneTree { EditMode = true };
        var host = new Node { Name = "Host" };
        tree.Root.AddChild(host);
        using (var session = new EditorSession(host))
        {
            var scene = session.Open(Path.Combine(project, "Content", "Scenes", "Main.mscene"));
            Assert.Equal(project, session.ProjectRoot);
            Assert.Equal("CreatedGame", session.Project?.Name);
            Assert.Equal("Main", scene.Root.Name);
            var spinner = Assert.IsType<MissingNode>(scene.Root.Children.Single(c => c.Name == "Spinner"));
            Assert.Equal("Spinner", spinner.OriginalType);
            Assert.Contains(scene.Root.Children, c => c is Camera3D);
        }

        tree.Shutdown();
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.0 s", CultureInfo.InvariantCulture);
}
