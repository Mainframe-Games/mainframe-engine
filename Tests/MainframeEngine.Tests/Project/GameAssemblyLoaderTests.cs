extern alias generators;

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using generators::MainframeEngine.Generators;
using MainframeEngine.Networking;
using MainframeEngine.Serialization;
using MainframeEngine.Tests.Scene;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace MainframeEngine.Tests.Project;

/// <summary>Compiles game-like sources with the engine's source generator into a dll on disk (the editor's build output).</summary>
internal static class GameCompiler
{
    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)),
        MetadataReference.CreateFromFile(typeof(Node).Assembly.Location),
    ];

    public static string Compile(string source, string assemblyName, string directory, params string[] referencePaths)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, path: assemblyName + ".cs", encoding: Encoding.UTF8)],
            [.. References, .. referencePaths.Select(p => MetadataReference.CreateFromFile(p))],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new NodeRegistrationGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        Assert.Empty(generatorDiagnostics);

        Directory.CreateDirectory(directory);
        var dll = Path.Combine(directory, assemblyName + ".dll");
        using (var image = File.Create(dll))
        using (var symbols = File.Create(Path.ChangeExtension(dll, ".pdb")))
        {
            var result = output.Emit(image, symbols, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            Assert.True(result.Success, string.Join('\n', result.Diagnostics));
        }

        return dll;
    }
}

/// <summary>
/// Collectible game-assembly loading: load → use (instantiate, tick, save/load scenes with inline game resources) →
/// unload → the context is collected → reload a changed build; scenes survive as <see cref="MissingNode"/> while the
/// assembly is gone. Serial: the type registry, loader cache and asset database are process-wide.
/// </summary>
[Collection(nameof(SerialResources))]
public sealed class GameAssemblyLoaderTests : IDisposable
{
    internal const string GameV1 = """
        using MainframeEngine;
        using MainframeEngine.Networking;

        namespace ReloadGame;

        public enum Mood { Calm, Angry }

        public class ReloadPlayer : Node3D
        {
            [Export] public float Speed { get; set; } = 5;
            [Export] public Mood Mood { get; set; }
            [Export] public ReloadStats? Stats { get; set; }
            [Replicated] public int Health { get; set; } = 100;
            public int Ticks;

            protected override void OnProcess(in GameTime gameTime) => Ticks++;
        }

        public sealed class ReloadStats : Resource
        {
            [Export] public int Armor { get; set; }
        }
        """;

    // v2: a new property; everything v1 saved still loads.
    private const string GameV2 = """
        using MainframeEngine;

        namespace ReloadGame;

        public enum Mood { Calm, Angry, Sleepy }

        public class ReloadPlayer : Node3D
        {
            [Export] public float Speed { get; set; } = 5;
            [Export] public Mood Mood { get; set; }
            [Export] public ReloadStats? Stats { get; set; }
            [Export] public float Jump { get; set; } = 2;
        }

        public sealed class ReloadStats : Resource
        {
            [Export] public int Armor { get; set; }
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mf-alc-tests", Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;

    public GameAssemblyLoaderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "project", AssetDatabase.ContentFolder));
        AssetDatabase.Current = new AssetDatabase(Path.Combine(_root, "project"));
        ResourceLoader.ClearCache();
    }

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousAssets;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string Build(string source, string version, string name = "ReloadGame") =>
        GameCompiler.Compile(source, name, Path.Combine(_root, "bin", version));

    private string ScenePath => Path.Combine(_root, "project", "Content", "Level.mscene");

    // Everything that touches game types lives in non-inlined helpers, so no stack slot of the test method keeps them.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private string UseGameTypes()
    {
        var info = TypeRegistry.Get("ReloadPlayer")!;
        Assert.NotNull(info);
        Assert.NotNull(ReplicationRegistry.Get(info.Type));
        Assert.True(info.Type.Assembly.IsCollectible);

        var tree = new SceneTree();
        var root = new Node3D { Name = "Level" };
        var player = TypeRegistry.CreateNode("ReloadPlayer");
        player.Name = "Player";
        info.FindProperty("Speed")!.SetValue(player, 9f);
        info.FindProperty("Mood")!.SetValue(player, Enum.Parse(info.FindProperty("Mood")!.ValueType, "Angry"));
        var stats = (Resource)TypeRegistry.Get("ReloadStats")!.CreateInstance();
        TypeRegistry.Get("ReloadStats")!.FindProperty("Armor")!.SetValue(stats, 7);
        info.FindProperty("Stats")!.SetValue(player, stats);
        root.AddChild(player);
        player.Owner = root;
        tree.Root.AddChild(root);
        tree.Tick(new GameTime { DeltaTime = 1f / 60f });
        Assert.Equal(1, (int)player.GetType().GetField("Ticks")!.GetValue(player)!);

        var uid = SceneSaver.Save(root, ScenePath);

        // Load through the cache (its inline-resource table now holds a game-typed resource) and instantiate.
        var scene = ResourceLoader.Load<PackedScene>(uid);
        var copy = scene.Instantiate();
        Assert.Equal(9f, info.FindProperty("Speed")!.GetValue(copy.GetNode("Player")));
        copy.Free();
        scene.Release();
        tree.Shutdown();
        return uid;
    }

    // Loads the saved scene through the loader cache, instantiates it (so the cached scene's inline-resource table holds
    // a game-typed resource) and keeps the cache reference: the scene stays cached across the unload.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InstantiateAndKeepCached(string uid) => ResourceLoader.Load<PackedScene>(uid).Instantiate().Free();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertGameTypesGone()
    {
        Assert.Null(TypeRegistry.Get("ReloadPlayer"));
        Assert.Null(TypeRegistry.Get("ReloadStats"));
        Assert.DoesNotContain(TypeRegistry.All, i => i.Type.Assembly.IsCollectible);
    }

    // The v1 scene with the v2 types: values carry over, the new property has its default.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UseV2(string uid)
    {
        var info = TypeRegistry.Get("ReloadPlayer")!;
        Assert.NotNull(info.FindProperty("Jump"));
        Assert.Contains("Sleepy", Enum.GetNames(info.FindProperty("Mood")!.ValueType));

        var scene = ResourceLoader.Load<PackedScene>(uid);
        var root = scene.Instantiate();
        var player = root.GetNode("Player");
        Assert.Same(info.Type, player.GetType());
        Assert.Equal(9f, info.FindProperty("Speed")!.GetValue(player));
        Assert.Equal("Angry", info.FindProperty("Mood")!.GetValue(player)!.ToString());
        Assert.Equal(7, TypeRegistry.Get("ReloadStats")!.FindProperty("Armor")!.GetValue(info.FindProperty("Stats")!.GetValue(player)!));
        Assert.Equal(2f, info.FindProperty("Jump")!.GetValue(player));
        root.Free();
        scene.Release();
        scene.Release(); // the reference InstantiateAndKeepCached kept
        Assert.False(ResourceLoader.IsCached(uid));
    }

    [Fact]
    public void LoadUseUnloadCollectAndReloadAChangedBuild()
    {
        var v1 = Build(GameV1, "v1");
        using var loader = new GameAssemblyLoader(v1);

        loader.Load();
        Assert.Equal(1, loader.Generation);
        Assert.True(loader.IsLoaded);
        var uid = UseGameTypes();
        InstantiateAndKeepCached(uid);

        Assert.True(loader.Unload(TimeSpan.FromSeconds(10)), "the unloaded game assembly was not collected");
        Assert.False(loader.IsLoaded);
        AssertGameTypesGone();
        Assert.True(ResourceLoader.IsCached(uid)); // the scene itself stays cached (only its game resources were dropped)

        // Rebuilt in place, over the loaded file (the loader read it into memory), with a new property.
        Assert.Equal(v1, GameCompiler.Compile(GameV2, "ReloadGame", Path.GetDirectoryName(v1)!));
        loader.Load();
        Assert.Equal(2, loader.Generation);
        UseV2(uid);

        Assert.True(loader.Unload(TimeSpan.FromSeconds(10)));
        AssertGameTypesGone();
    }

    // No game assembly: the player is a MissingNode holding its type and values; re-saving changes nothing.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AssertMissingNodeRoundTrip(string uid, byte[] original)
    {
        var scene = ResourceLoader.Load<PackedScene>(uid);
        var root = scene.Instantiate();
        var missing = Assert.IsType<MissingNode>(root.GetNode("Player"));
        Assert.Equal("ReloadPlayer", missing.OriginalType);
        Assert.Equal(9, missing.RawProperties.GetProperty("Speed").GetSingle());
        Assert.Equal("Angry", missing.RawProperties.GetProperty("Mood").GetString());
        SceneSaver.Save(root, ScenePath);
        Assert.Equal(Encoding.UTF8.GetString(original), Encoding.UTF8.GetString(File.ReadAllBytes(ScenePath)));
        root.Free();
        scene.Release();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertRealTypeRestored(string uid)
    {
        var scene = ResourceLoader.Load<PackedScene>(uid);
        var root = scene.Instantiate();
        var player = root.GetNode("Player");
        Assert.Equal("ReloadPlayer", player.GetType().Name);
        Assert.Equal(9f, TypeRegistry.Get("ReloadPlayer")!.FindProperty("Speed")!.GetValue(player));
        root.Free();
        scene.Release();
    }

    [Fact]
    public void ScenesKeepGameDataAsMissingNodesWhileTheAssemblyIsUnloaded()
    {
        using var loader = new GameAssemblyLoader(Build(GameV1, "missing"));
        loader.Load();
        var uid = UseGameTypes();
        var original = File.ReadAllBytes(ScenePath);
        Assert.True(loader.Unload(TimeSpan.FromSeconds(10)));

        AssertMissingNodeRoundTrip(uid, original);
        ResourceLoader.ClearCache();

        // The game comes back: the same file loads as the real type again.
        loader.Load();
        AssertRealTypeRestored(uid);
        Assert.True(loader.Unload(TimeSpan.FromSeconds(10)));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LeakInto(List<object> holder) => holder.Add(TypeRegistry.CreateNode("ReloadPlayer"));

    [Fact]
    public void AReferenceToGameCodeKeepsTheContextAliveAndIsReported()
    {
        using var loader = new GameAssemblyLoader(Build(GameV1, "leak"));
        loader.Load();
        var holder = new List<object>();
        LeakInto(holder);

        Assert.False(loader.Unload(TimeSpan.FromMilliseconds(500)));
        Assert.True(loader.LastUnloadedContext!.IsAlive);

        holder.Clear();
        Assert.True(GameAssemblyLoader.WaitForCollection(loader.LastUnloadedContext, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void PrivateDependenciesLoadFromTheGameFolderIntoTheSameContext()
    {
        var bin = Path.Combine(_root, "bin", "deps");
        var dependency = GameCompiler.Compile("namespace Helpers; public static class Damage { public static int Double(int x) => x * 2; } public class HelperNode : MainframeEngine.Node { }", "ReloadHelpers", bin);
        var game = GameCompiler.Compile("""
            using MainframeEngine;
            namespace DepGame;
            public class DepNode : Node { [Export] public int Power { get; set; } = Helpers.Damage.Double(21); }
            """, "DepGame", bin, dependency);
        using var loader = new GameAssemblyLoader(game);
        loader.Load();

        Assert.Equal(42, UsePower());
        Assert.True(loader.Unload(TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "ReloadHelpers" && !a.IsCollectible);
        Assert.Null(TypeRegistry.Get("HelperNode")); // the dependency's types went too, and a later scan does not bring them back
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int UsePower()
    {
        var node = TypeRegistry.CreateNode("DepNode");
        var power = (int)TypeRegistry.Get("DepNode")!.FindProperty("Power")!.GetValue(node)!;
        Assert.NotNull(TypeRegistry.Get("HelperNode")); // registered by a lookup-miss scan of the loaded dependency
        Assert.True(node.GetType().Assembly.IsCollectible);
        node.Free();
        return power;
    }

    [Fact]
    public void MisuseIsReported()
    {
        using var missing = new GameAssemblyLoader(Path.Combine(_root, "nope", "Nope.dll"));
        Assert.Throws<FileNotFoundException>(() => missing.Load());
        Assert.True(missing.Unload()); // nothing loaded: nothing to do

        using var loader = new GameAssemblyLoader(Build(GameV1, "twice"));
        loader.Load();
        Assert.Throws<InvalidOperationException>(() => loader.Load());
        Assert.True(loader.Unload(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void BuildOutputDiscoveryFindsTheNewestAssembly()
    {
        var project = Path.Combine(_root, "MyGame");
        Directory.CreateDirectory(project);
        var csproj = Path.Combine(project, "MyGame.csproj");
        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Assert.Null(GameAssemblyLoader.FindBuildOutput(csproj));

        var old = Touch(Path.Combine(project, "bin", "Debug", "net9.0", "MyGame.dll"), DateTime.UtcNow.AddHours(-1));
        var current = Touch(Path.Combine(project, "bin", "Debug", "net10.0", "MyGame.dll"), DateTime.UtcNow);
        Touch(Path.Combine(project, "bin", "Debug", "net10.0", "ref", "MyGame.dll"), DateTime.UtcNow.AddHours(1));
        Touch(Path.Combine(project, "bin", "Release", "net10.0", "MyGame.dll"), DateTime.UtcNow.AddHours(2));

        Assert.Equal(current, GameAssemblyLoader.FindBuildOutput(csproj));
        Assert.Equal(current, GameAssemblyLoader.FindBuildOutput(project)); // the folder works too
        Assert.NotEqual(old, current);
        Assert.EndsWith(Path.Combine("Release", "net10.0", "MyGame.dll"), GameAssemblyLoader.FindBuildOutput(csproj, "Release"), StringComparison.Ordinal);

        File.WriteAllText(csproj, "<Project><PropertyGroup><AssemblyName>Renamed</AssemblyName></PropertyGroup></Project>");
        var renamed = Touch(Path.Combine(project, "bin", "Debug", "net10.0", "Renamed.dll"), DateTime.UtcNow);
        Assert.Equal(renamed, GameAssemblyLoader.FindBuildOutput(csproj));
    }

    private static string Touch(string path, DateTime time)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        File.SetLastWriteTimeUtc(path, time);
        return path;
    }
}

public sealed class DebouncerTests
{
    private static readonly DateTime T0 = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ABurstIsReleasedOnceAfterTheQuietPeriod()
    {
        var debouncer = new Debouncer(TimeSpan.FromMilliseconds(300));
        debouncer.Notify("b.dll", T0);
        debouncer.Notify("a.dll", T0.AddMilliseconds(100));
        debouncer.Notify("b.dll", T0.AddMilliseconds(200));

        Assert.False(debouncer.TryFlush(T0.AddMilliseconds(450), out _)); // 250 ms since the last change
        Assert.True(debouncer.TryFlush(T0.AddMilliseconds(500), out var paths));
        Assert.Equal(["a.dll", "b.dll"], paths);
        Assert.False(debouncer.HasPending);
        Assert.False(debouncer.TryFlush(T0.AddSeconds(10), out _));
    }

    [Fact]
    public void ChangesDuringTheQuietPeriodPostponeIt()
    {
        var debouncer = new Debouncer(TimeSpan.FromMilliseconds(100));
        for (var i = 0; i < 10; i++)
        {
            debouncer.Notify("x", T0.AddMilliseconds(i * 50));
            Assert.False(debouncer.TryFlush(T0.AddMilliseconds(i * 50 + 60), out _));
        }

        Assert.True(debouncer.TryFlush(T0.AddMilliseconds(9 * 50 + 100), out var paths));
        Assert.Equal(["x"], paths);
    }

    [Fact]
    public void TheWatcherReportsABurstOfFileChangesOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mf-watch", Guid.NewGuid().ToString("N"));
        var batches = new List<IReadOnlyList<string>>();
        try
        {
            using var watcher = new DebouncedFileWatcher(directory, "*.dll", delay: TimeSpan.FromMilliseconds(400));
            watcher.Changed += paths =>
            {
                lock (batches)
                    batches.Add(paths);
            };
            Thread.Sleep(100); // let the OS watcher start (FSEvents on macOS)
            for (var i = 0; i < 5; i++)
                File.WriteAllText(Path.Combine(directory, "Game.dll"), "build " + i);
            File.WriteAllText(Path.Combine(directory, "Game.pdb"), "filtered out");

            Assert.True(Wait.Until(() =>
            {
                lock (batches)
                    return batches.Count > 0;
            }, 10));
            Thread.Sleep(800);
            lock (batches)
            {
                Assert.Single(batches);
                Assert.Contains(batches[0], p => p.EndsWith("Game.dll", StringComparison.Ordinal));
                Assert.DoesNotContain(batches[0], p => p.EndsWith(".pdb", StringComparison.Ordinal));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
