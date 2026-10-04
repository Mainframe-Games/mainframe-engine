using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Xml.Linq;
using MainframeEngine.Networking;
using MainframeEngine.Serialization;

namespace MainframeEngine;

/// <summary>
/// Loads a game assembly (a project's <c>MyGame.dll</c>) into a <b>collectible</b> <see cref="AssemblyLoadContext"/> so a
/// tool — the editor — can use its node types and later unload it and load a rebuilt copy without restarting
/// (code reload). The engine and everything already loaded by the host are shared with the default context; the
/// game's private dependencies load into its context from its build output. Files are read into memory, so the build
/// can overwrite them while they are loaded.
/// </summary>
/// <remarks>
/// <para>Reload cycle (docs/design/project-and-gamehost.md): serialize open scenes → free every node of game types and
/// drop every reference to game types/instances → <see cref="Unload"/> (unregisters the types, forgets cached
/// resources of them, unloads the context and waits until the GC has collected it) → <see cref="Load"/> → re-instantiate.
/// Scenes naming a type the new build no longer has load as <see cref="MissingNode"/>, keeping its data.</para>
/// <para>Not thread-safe: use from one thread (the editor's main thread).</para>
/// </remarks>
public sealed class GameAssemblyLoader : IDisposable
{
    private GameLoadContext? _context;
    private WeakReference? _lastContext;

    /// <param name="assemblyPath">The game assembly (<c>bin/Debug/net10.0/MyGame.dll</c>).</param>
    public GameAssemblyLoader(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        AssemblyPath = Path.GetFullPath(assemblyPath);
    }

    public string AssemblyPath { get; }

    /// <summary>The loaded game assembly, or null.</summary>
    public Assembly? Assembly { get; private set; }

    /// <summary>Incremented by every successful <see cref="Load"/> (1 after the first).</summary>
    public int Generation { get; private set; }

    public bool IsLoaded => Assembly is not null;

    /// <summary>
    /// A weak reference to the most recently unloaded context: alive until the GC has collected it (tools can show
    /// "game assembly still referenced" when it stays alive).
    /// </summary>
    public WeakReference? LastUnloadedContext => _lastContext;

    /// <summary>
    /// Loads <see cref="AssemblyPath"/> into a new collectible context and registers its node and resource types (the
    /// assembly is <see cref="Assembly"/>). Returns nothing on purpose: a caller's local holding the assembly (even an
    /// unused return value in a Debug build) would keep the context alive past <see cref="Unload"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Load()
    {
        if (Assembly is not null)
            throw new InvalidOperationException("The game assembly is already loaded; Unload it first (or call Reload).");
        if (!File.Exists(AssemblyPath))
            throw new FileNotFoundException($"Game assembly not found: '{AssemblyPath}'. Build the project first.", AssemblyPath);

        var context = new GameLoadContext(AssemblyPath, $"Game:{Path.GetFileNameWithoutExtension(AssemblyPath)}#{Generation + 1}");
        Assembly? assembly = null;
        try
        {
            assembly = context.LoadFromFileInMemory(AssemblyPath);
            TypeRegistry.EnsureRegistered(assembly);
        }
        catch
        {
            // e.g. a type name clashing with a loaded one: leave nothing of this load behind.
            if (assembly is not null)
            {
                TypeRegistry.UnregisterAssembly(assembly);
                ReplicationRegistry.UnregisterAssembly(assembly);
            }

            context.Unload();
            throw;
        }

        _context = context;
        Assembly = assembly;
        Generation++;
        Log.Info($"[GameAssembly] Loaded {assembly.GetName().Name} (generation {Generation}).");
    }

    /// <summary>
    /// Unregisters the game's types, forgets cached resources of them and unloads the context, then runs the GC until
    /// the context is collected or <paramref name="timeout"/> (default 5 s) passes. Returns true when it was collected;
    /// false means something still references game code (a node, a delegate, a static) — the context stays in memory.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool Unload(TimeSpan? timeout = null)
    {
        // Never read Assembly/_context here: in a Debug build this frame would keep them alive while the GC runs below.
        if (!BeginUnload())
            return true;
        return WaitForCollection(_lastContext!, timeout ?? TimeSpan.FromSeconds(5));
    }

    /// <summary><see cref="Unload"/> then <see cref="Load"/>; the new types replace the old ones in the registry.</summary>
    public void Reload(TimeSpan? timeout = null)
    {
        if (!Unload(timeout))
            Log.Warning($"[GameAssembly] The previous load of {Path.GetFileName(AssemblyPath)} is still referenced and stays in memory.");
        Load();
    }

    public void Dispose() => BeginUnload();

    /// <summary>
    /// Runs full blocking collections until <paramref name="weak"/> is dead or <paramref name="timeout"/> passes.
    /// </summary>
    public static bool WaitForCollection(WeakReference weak, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(weak);
        var deadline = DateTime.UtcNow + timeout;
        for (var i = 0; weak.IsAlive; i++)
        {
            if (DateTime.UtcNow > deadline)
                return false;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (weak.IsAlive && i > 2)
                Thread.Sleep(Math.Min(100, 5 * i));
        }

        return true;
    }

    // Kept out of Unload so no local of the JIT'd caller frame holds the context or assembly.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool BeginUnload()
    {
        if (Assembly is not { } assembly || _context is not { } context)
            return false;
        var name = assembly.GetName().Name;
        ResourceLoader.ReleaseTypesOf(assembly);
        TypeRegistry.UnregisterAssembly(assembly);
        ReplicationRegistry.UnregisterAssembly(assembly);
        Assembly = null;
        _context = null;
        _lastContext = new WeakReference(context, trackResurrection: false);
        context.Unload();
        Log.Info($"[GameAssembly] Unloaded {name} (generation {Generation}).");
        return true;
    }

    /// <summary>
    /// The newest build of <paramref name="projectFile"/> (a <c>.csproj</c>, or a folder holding exactly one):
    /// <c>bin/{configuration}/{tfm}/{AssemblyName}.dll</c>, using <c>&lt;AssemblyName&gt;</c> from the project when set.
    /// Null when it has not been built.
    /// </summary>
    public static string? FindBuildOutput(string projectFile, string configuration = "Debug")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        if (Directory.Exists(projectFile))
        {
            var projects = Directory.GetFiles(projectFile, "*.csproj");
            if (projects.Length != 1)
                throw new ArgumentException($"'{projectFile}' holds {projects.Length} .csproj files; pass the project file.", nameof(projectFile));
            projectFile = projects[0];
        }

        projectFile = Path.GetFullPath(projectFile);
        var assemblyName = ReadAssemblyName(projectFile) ?? Path.GetFileNameWithoutExtension(projectFile);
        var bin = Path.Combine(Path.GetDirectoryName(projectFile)!, "bin", configuration);
        if (!Directory.Exists(bin))
            return null;

        string? newest = null;
        var newestTime = DateTime.MinValue;
        foreach (var candidate in Directory.EnumerateFiles(bin, assemblyName + ".dll", SearchOption.AllDirectories))
        {
            // bin/Debug/net10.0/MyGame.dll or bin/Debug/net10.0/<rid>/MyGame.dll; never ref/ or publish/ copies.
            var relative = Path.GetRelativePath(bin, candidate).Replace('\\', '/');
            if (relative.Contains("/ref/", StringComparison.Ordinal) || relative.Contains("/publish/", StringComparison.Ordinal))
                continue;
            var time = File.GetLastWriteTimeUtc(candidate);
            if (time > newestTime)
            {
                newest = candidate;
                newestTime = time;
            }
        }

        return newest;
    }

    private static string? ReadAssemblyName(string projectFile)
    {
        try
        {
            var name = XDocument.Load(projectFile).Descendants().FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value.Trim();
            return string.IsNullOrEmpty(name) || name.Contains("$(", StringComparison.Ordinal) ? null : name;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private sealed class GameLoadContext(string mainAssemblyPath, string name) : AssemblyLoadContext(name, isCollectible: true)
    {
        private readonly AssemblyDependencyResolver? _resolver = CreateResolver(mainAssemblyPath);
        private readonly string _directory = Path.GetDirectoryName(mainAssemblyPath)!;

        // The resolver reads MyGame.deps.json; assemblies without one (hand-built, tests) fall back to folder probing.
        private static AssemblyDependencyResolver? CreateResolver(string path)
        {
            try
            {
                return new AssemblyDependencyResolver(path);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public Assembly LoadFromFileInMemory(string path)
        {
            // In memory, so the build can replace the files; symbols too, for stack traces with line numbers.
            using var image = new MemoryStream(File.ReadAllBytes(path));
            var pdb = Path.ChangeExtension(path, ".pdb");
            if (!File.Exists(pdb))
                return LoadFromStream(image);
            using var symbols = new MemoryStream(File.ReadAllBytes(pdb));
            return LoadFromStream(image, symbols);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Shared with the host: the engine (one TypeRegistry, one Node type) and anything the host already has.
            foreach (var loaded in Default.Assemblies)
                if (AssemblyName.ReferenceMatchesDefinition(assemblyName, loaded.GetName()))
                    return null;
            try
            {
                if (Default.LoadFromAssemblyName(assemblyName) is not null)
                    return null; // resolvable by the host (framework, the host's own dependencies)
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                // Only the game has it: load the game's copy below.
            }

            var path = _resolver?.ResolveAssemblyToPath(assemblyName);
            if (path is null)
            {
                var probe = Path.Combine(_directory, assemblyName.Name + ".dll");
                path = File.Exists(probe) ? probe : null;
            }

            return path is null ? null : LoadFromFileInMemory(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
