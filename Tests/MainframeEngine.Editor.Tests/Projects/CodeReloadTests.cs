extern alias generators;

using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using generators::MainframeEngine.Generators;
using MainframeEngine.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace MainframeEngine.Editor.Tests.Projects;

/// <summary>Compiles game sources with the engine's generator into a dll (the game library's build output).</summary>
internal static class GameCompiler
{
    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)),
        MetadataReference.CreateFromFile(typeof(Node).Assembly.Location),
    ];

    public static string Compile(string source, string assemblyName, string directory)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, path: assemblyName + ".cs", encoding: Encoding.UTF8)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new NodeRegistrationGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Assert.Empty(diagnostics);
        Directory.CreateDirectory(directory);
        var dll = Path.Combine(directory, assemblyName + ".dll");
        using (var image = File.Create(dll))
        using (var symbols = File.Create(Path.ChangeExtension(dll, ".pdb")))
        {
            var result = output.Emit(image, symbols, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            Assert.True(result.Success, string.Join('\n', result.Diagnostics));
        }

        // A later build must look newer than the loaded one even within the file system's timestamp resolution.
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddSeconds(Interlocked.Increment(ref _generation)));
        return dll;
    }

    private static int _generation;
}

/// <summary>
/// E4 code reload end to end, headless: a project whose game library is compiled with Roslyn is opened in the editor
/// (game types load instead of missing nodes), the game code changes, the editor reloads it — open scenes are
/// re-created with their unsaved edits, selection and view, the inspector shows the new property, scenes without game
/// code keep their history — and the previous assembly is collected (no leak). A removed type comes back as a missing
/// node that keeps its data.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class CodeReloadTests : IDisposable
{
    private const string Name = "ReloadGame";

    private const string GameV1 = """
        using MainframeEngine;
        namespace ReloadGame;
        public class Wobbler : Node3D
        {
            [Export] public float Speed { get; set; } = 1.5f;
            [Signal] public event System.Action? Wobbled;
            public void Wobble() => Wobbled?.Invoke();
            // Process callbacks put the node in the tree's process list (kept while editing, never run).
            protected override void OnProcess(in GameTime gameTime) => Speed += 0f;
        }
        """;

    private const string GameV2 = """
        using MainframeEngine;
        namespace ReloadGame;
        public class Wobbler : Node3D
        {
            [Export] public float Speed { get; set; } = 1.5f;
            [Export] public float Amplitude { get; set; } = 2f;
            [Signal] public event System.Action? Wobbled;
            public void Wobble() => Wobbled?.Invoke();
        }
        """;

    private const string GameV3 = """
        using MainframeEngine;
        namespace ReloadGame;
        public class Other : Node3D
        {
        }
        """;

    private readonly string _project;
    private readonly string _output;
    private HeadlessEditor? _editor;

    public CodeReloadTests()
    {
        _project = GameProjectLayout.RealPath(Directory.CreateTempSubdirectory("mf-reload").FullName);
        var library = Path.Combine(_project, Name);
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, Name + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _output = Path.Combine(library, "bin", "Debug", "net10.0");
        var settings = new ProjectSettings { Name = Name, MainScene = "Content/Scenes/Main.mscene" };
        settings.Assemblies.Add(Name);
        settings.Save(_project);
        Directory.CreateDirectory(Path.Combine(_project, "Content", "Scenes"));
        File.WriteAllText(Path.Combine(_project, "Content", "Scenes", "Main.mscene"), """
            {
              "format": 1,
              "uid": "scn_7e10ad000001",
              "root": {
                "type": "Node3D",
                "name": "Main",
                "children": [
                  { "type": "Wobbler", "name": "W", "props": { "Speed": 3 } },
                  { "type": "Node3D", "name": "Plain" }
                ]
              }
            }
            """);
        File.WriteAllText(Path.Combine(_project, "Content", "Scenes", "Plain.mscene"), """
            { "format": 1, "uid": "scn_7e10ad000002", "root": { "type": "Node3D", "name": "Plain" } }
            """);
    }

    public void Dispose()
    {
        _editor?.Dispose();
        try
        {
            Directory.Delete(_project, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private HeadlessEditor Open()
    {
        _editor = new HeadlessEditor(configure: o => o with { InitialProject = _project });
        _editor.Tick(3);
        return _editor;
    }

    private static EditedScene SceneNamed(EditorWorkspace w, string file) => w.Session.Scenes.Single(s => Path.GetFileName(s.FilePath) == file);

    [Fact]
    public void OpeningAProjectLoadsItsGameCodeSoScenesUseTheRealTypes()
    {
        GameCompiler.Compile(GameV1, Name, _output);
        var editor = Open();
        var w = editor.Workspace;

        Assert.True(w.Project.IsGameLoaded);
        Assert.Equal(_project, w.Session.ProjectRoot);
        var scene = Assert.Single(w.Session.Scenes);
        Assert.False(scene.IsDirty, "A scene just opened is not dirty.");
        Assert.Empty(scene.History.Actions);
        var wobbler = scene.Root.GetNode("W");
        Assert.Equal("Wobbler", wobbler.GetType().Name);
        Assert.Equal(3f, TypeRegistry.GetNearest(wobbler.GetType())!.FindProperty("Speed")!.GetValue(wobbler));
        scene.Selection.Set(wobbler);
        editor.Tick();
        Assert.Contains(w.Inspector.Rows, r => r.Name == "Speed");
        Assert.Contains(w.Inspector.SignalRows, r => r.Name == "Wobbled");
    }

    [Fact]
    public void ReloadRecreatesScenesWithTheirEditsSelectionAndViewAndTheOldCodeIsCollected()
    {
        GameCompiler.Compile(GameV1, Name, _output);
        var editor = Open();
        var w = editor.Workspace;
        w.Session.Open(Path.Combine(_project, "Content", "Scenes", "Plain.mscene"));
        var plain = SceneNamed(w, "Plain.mscene");
        plain.AddNode(new Node3D { Name = "Added" }, plain.Root);
        var plainHistory = plain.History.Actions.Count;

        var (oldNode, oldAssembly) = EditAndRemember(w);
        editor.Tick();

        GameCompiler.Compile(GameV2, Name, _output);
        Assert.True(w.Project.ReloadGameAssembly());
        editor.Tick(2);

        if (w.Project.LastUnloadCollected != true && oldAssembly.Target is System.Reflection.Assembly leaked)
            Assert.Fail("The previous game assembly is still referenced: " +
                        (ReferencePathFinder.PathTo(leaked, w, editor.Tree, editor.Server) ?? "no path found from the editor's roots"));
        Assert.False(oldNode.IsAlive);
        Assert.False(oldAssembly.IsAlive);

        // The game scene: re-created at the same tab, unsaved edit kept, selection and view restored.
        Assert.Equal(2, w.Session.Scenes.Count);
        var reloaded = SceneNamed(w, "Main.mscene");
        Assert.Same(reloaded, w.Session.Scenes[0]);
        Assert.Same(reloaded, w.Session.Active);
        Assert.True(reloaded.IsDirty);
        var wobbler = reloaded.Root.GetNode<Node3D>("W");
        Assert.Equal(new Vector3(1, 2, 3), wobbler.Position);
        Assert.Same(wobbler, reloaded.Selection.Primary);
        Assert.Equal(new Vector3(4, 5, 6), reloaded.Camera.Pivot);
        Assert.Contains(w.Inspector.Rows, r => r.Name == "Amplitude");
        Assert.Equal(2f, TypeRegistry.GetNearest(wobbler.GetType())!.FindProperty("Amplitude")!.GetValue(wobbler));

        // The scene without game code was left alone: same object, same history.
        Assert.Same(plain, SceneNamed(w, "Plain.mscene"));
        Assert.Equal(plainHistory, plain.History.Actions.Count);
        Assert.Equal(1, w.Project.ReloadCount);
    }

    // Kept out of the test method: Debug builds keep locals alive to the end of the method.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Node, WeakReference Assembly) EditAndRemember(EditorWorkspace w)
    {
        var scene = SceneNamed(w, "Main.mscene");
        w.Session.Activate(scene);
        scene.Camera.Pivot = new Vector3(4, 5, 6);
        var node = scene.Root.GetNode<Node3D>("W");
        scene.SetProperty(node, TypeRegistry.GetRequired(typeof(Node3D)).FindProperty("Position")!, new Vector3(1, 2, 3));
        scene.Selection.Set(node);
        return (new WeakReference(node), new WeakReference(node.GetType().Assembly));
    }

    [Fact]
    public void ARemovedTypeComesBackAsAMissingNodeThatKeepsItsData()
    {
        GameCompiler.Compile(GameV1, Name, _output);
        var editor = Open();
        var w = editor.Workspace;

        GameCompiler.Compile(GameV3, Name, _output);
        Assert.True(w.Project.ReloadGameAssembly());
        editor.Tick();

        var scene = Assert.Single(w.Session.Scenes);
        var missing = Assert.IsType<MissingNode>(scene.Root.GetNode("W"));
        Assert.Equal("Wobbler", missing.OriginalType);
        w.Session.Save(scene);
        var json = File.ReadAllText(scene.FilePath!);
        Assert.Contains("\"type\": \"Wobbler\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Speed\": 3", json, StringComparison.Ordinal);

        // Bringing the type back restores it with its data.
        GameCompiler.Compile(GameV1, Name, _output);
        Assert.True(w.Project.ReloadGameAssembly());
        editor.Tick();
        Assert.Equal("Wobbler", Assert.Single(w.Session.Scenes).Root.GetNode("W").GetType().Name);
    }

    [Fact]
    public void ABuildOutputChangeReloadsAutomatically()
    {
        GameCompiler.Compile(GameV1, Name, _output);
        var editor = Open();
        var w = editor.Workspace;
        Assert.Equal(0, w.Project.ReloadCount);

        GameCompiler.Compile(GameV2, Name, _output);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (w.Project.ReloadCount == 0 && DateTime.UtcNow < deadline)
        {
            editor.Tick();
            Thread.Sleep(20);
        }

        Assert.Equal(1, w.Project.ReloadCount);
        var wobbler = Assert.Single(w.Session.Scenes).Root.GetNode("W");
        Assert.NotNull(TypeRegistry.GetNearest(wobbler.GetType())!.FindProperty("Amplitude"));
    }
}
