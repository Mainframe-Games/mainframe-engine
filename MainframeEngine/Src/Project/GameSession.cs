using System.Reflection;
using MainframeEngine.EditorLink;
using MainframeEngine.Serialization;

namespace MainframeEngine;

/// <summary>
/// What <see cref="GameHost"/> does with a project, on any <see cref="SceneTree"/> (so it runs headless in tests):
/// registers the game assemblies' types, installs the input map, adds the autoloads, starts the main (or
/// <c>--scene</c>) scene, and — with <c>--editor-port</c> — talks to the editor: streams the log, reports status and
/// applies its commands (stop, pause/resume, reload scene) from the game loop. The editor link connects in the
/// constructor (in the background), so start-up logging already reaches the editor.
/// </summary>
public sealed class GameSession : IDisposable
{
    /// <summary>How often status is reported to the editor (also sent on every change).</summary>
    public const float StatusInterval = 0.5f;

    private readonly List<Node> _autoloads = [];
    private readonly List<PackedScene> _autoloadScenes = [];
    private EditorLinkLogSink? _linkSink;
    private float _sinceStatus;
    private float _framesPerSecond;
    private GameRunState _reportedState = GameRunState.Starting;
    private bool _disposed;

    public GameSession(SceneTree tree, ProjectSettings settings, GameHostOptions? options = null)
    {
        Tree = tree ?? throw new ArgumentNullException(nameof(tree));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Options = options ?? new GameHostOptions();
        // Connect first, so everything logged from here on reaches the editor's Output panel.
        if (Options.EditorPort is { } port)
            ConnectEditor(port);
    }

    public SceneTree Tree { get; }

    public ProjectSettings Settings { get; }

    public GameHostOptions Options { get; }

    /// <summary>The scene started by <see cref="Start"/>: <c>--scene</c>, else <see cref="ProjectSettings.MainScene"/>.</summary>
    public string? StartScene => Options.Scene ?? Settings.MainScene;

    /// <summary>The autoload nodes, in project order.</summary>
    public IReadOnlyList<Node> Autoloads => _autoloads;

    /// <summary>The editor link (null without <c>--editor-port</c>).</summary>
    public EditorLinkClient? Link { get; private set; }

    /// <summary>Raised when the editor asks the game to stop; the host quits with the code.</summary>
    public event Action<ExitCode>? QuitRequested;

    /// <summary>
    /// Loads <see cref="ProjectSettings.Assemblies"/> (plus <paramref name="extra"/>) into the default context and runs
    /// their type registration, so scenes can name their node types. Unloadable names are logged.
    /// </summary>
    public static IReadOnlyList<Assembly> LoadGameAssemblies(ProjectSettings settings, params ReadOnlySpan<Assembly> extra)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var loaded = new List<Assembly>();
        foreach (var assembly in extra)
        {
            TypeRegistry.EnsureRegistered(assembly);
            loaded.Add(assembly);
        }

        foreach (var name in settings.Assemblies)
        {
            try
            {
                var assembly = Assembly.Load(new AssemblyName(name));
                TypeRegistry.EnsureRegistered(assembly);
                if (!loaded.Contains(assembly))
                    loaded.Add(assembly);
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException or ArgumentException)
            {
                Log.Error($"[Project] Game assembly '{name}' could not be loaded: {e.Message}");
            }
        }

        return loaded;
    }

    /// <summary>Installs the input map, adds the autoloads and starts the scene. False when the scene failed to load.</summary>
    public bool Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (EngineInfo.IsDifferentRelease(Settings.EngineVersion))
            Log.Warning($"[Project] '{Settings.Name}' was made with engine {Settings.EngineVersion}; this is {EngineInfo.Version}. Upgrade the project if something misbehaves.");

        Tree.Input.Map = Settings.Input;
        Tree.MaxPhysicsStepsPerFrame = Settings.Physics.MaxStepsPerFrame;
        var window = Settings.Window;
        Tree.Root.ContentScaleMode = window.StretchMode;
        Tree.Root.ContentScaleAspect = window.StretchAspect;
        Tree.Root.ContentScaleSize = new System.Numerics.Vector2(window.Width, window.Height);
        Tree.Root.ContentScaleFactor = window.StretchScale;
        Tree.Root.ContentScaleStretch = window.StretchScaleMode;
        if (Tree.Servers.Get<CanvasServer>() is { } canvas)
            canvas.ClearColor = Settings.Rendering.CanvasClearColor;
        AddAutoloads();

        var ok = true;
        if (StartScene is { Length: > 0 } scene)
        {
            try
            {
                Tree.ChangeSceneToFile(scene);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or InvalidCastException or NotSupportedException
                                          or System.Text.Json.JsonException)
            {
                Log.Error($"[Project] Could not start scene '{scene}': {e.Message}");
                ok = false;
            }
        }
        else
        {
            Log.Warning($"[Project] '{Settings.Name}' has no main scene (set \"mainScene\" in {ProjectSettings.FileName} or pass --scene).");
        }

        ReportStatus(force: true);
        return ok;
    }

    /// <summary>The update on which <c>--bake-lighting</c> bakes: the scene and what it generates when ready are in the tree.</summary>
    public const int BakeLightingUpdate = 3;

    private int _updates;

    /// <summary>Applies editor commands and reports status; call once per frame from the game loop (before the tree ticks).</summary>
    public void Update(in GameTime gameTime)
    {
        if (Options.BakeLighting && ++_updates == BakeLightingUpdate)
        {
            QuitRequested?.Invoke(BakeLighting() ? ExitCode.Ok : ExitCode.Error);
            return;
        }

        if (Link is not { } link)
            return;
        _framesPerSecond = gameTime.FramesPerSecond;
        while (link.TryReceiveCommand(out var command))
            Apply(command);
        _sinceStatus += gameTime.DeltaTime;
        ReportStatus(force: false);
    }

    /// <summary>
    /// <c>--bake-lighting</c> (ADR 0170): bakes every <see cref="LightProbeVolume"/> in the tree and saves each one's data
    /// where its <see cref="LightProbeVolume.Data"/> already lives, else next to the scene as <c>&lt;scene&gt;-lighting.mres</c>
    /// (plus its <c>.probes</c> file), under <see cref="ProjectSettings.ProjectDirectory"/> (pass <c>--project</c> with the
    /// source folder to write the files to commit). False when there was nothing to bake or a save failed.
    /// </summary>
    public bool BakeLighting()
    {
        var volumes = new List<LightProbeVolume>();
        Collect(Tree.Root, volumes);
        if (volumes.Count == 0)
        {
            Log.Error("[Bake] --bake-lighting: the scene has no LightProbeVolume.");
            return false;
        }

        var scene = CurrentScenePath();
        var ok = true;
        for (var i = 0; i < volumes.Count; i++)
        {
            var volume = volumes[i];
            var target = volume.Data?.ResourcePath;
            if (string.IsNullOrEmpty(target))
            {
                var stem = string.IsNullOrEmpty(scene) ? "scene" : Path.ChangeExtension(scene, null);
                target = (i == 0 ? stem : $"{stem}-{i}") + "-lighting.mres";
            }

            // Into the project's folder (--project: the source tree, so the bake can be committed); a project-relative path
            // otherwise resolves against the application's folder, a build output copy.
            if (!Path.IsPathRooted(target) && Settings.ProjectDirectory is { } projectDirectory)
                target = Path.GetFullPath(target, projectDirectory);
            var result = volume.Bake();
            try
            {
                result.Data.Save(target);
                Log.Info($"[Bake] '{volume.Name}': {result.Probes} probes in {result.Elapsed.TotalSeconds:0.00} s, saved to {target} " +
                         $"(hash {result.Data.BakeHash[..12]}).");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error($"[Bake] '{volume.Name}': could not save {target}: {e.Message}");
                ok = false;
            }
        }

        return ok;

        static void Collect(Node node, List<LightProbeVolume> into)
        {
            if (node is LightProbeVolume volume)
                into.Add(volume);
            foreach (var child in node.Children)
                Collect(child, into);
        }
    }

    /// <summary>Executes one editor command (public so tools and tests can drive a session without a socket).</summary>
    public void Apply(in EditorLinkCommand command)
    {
        switch (command.Kind)
        {
            case EditorCommandKind.Stop:
                Log.Info("[EditorLink] Stop requested by the editor.");
                _reportedState = GameRunState.Stopping;
                QuitRequested?.Invoke(ExitCode.Ok);
                break;
            case EditorCommandKind.Pause:
                Tree.Paused = true;
                break;
            case EditorCommandKind.Resume:
                Tree.Paused = false;
                break;
            case EditorCommandKind.ReloadScene:
                ReloadScene(string.IsNullOrEmpty(command.Argument) ? null : command.Argument);
                break;
            case EditorCommandKind.Ping:
                break; // answered by the report below
            case EditorCommandKind.RequestTree:
                Link?.SendTree(SnapshotTree(Tree.Root, out var truncated), truncated);
                break;
        }

        ReportStatus(force: true);
    }

    /// <summary>
    /// The tree under <paramref name="root"/> depth-first (the root at depth 0), at most
    /// <see cref="EditorLinkProtocol.MaxTreeNodes"/> nodes: what the editor's remote scene tree shows.
    /// </summary>
    public static EditorLinkTreeNode[] SnapshotTree(Node root, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(root);
        var nodes = new List<EditorLinkTreeNode>(256);
        var stack = new Stack<(Node Node, int Depth)>();
        stack.Push((root, 0));
        truncated = false;
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (nodes.Count == EditorLinkProtocol.MaxTreeNodes)
            {
                truncated = true;
                break;
            }

            nodes.Add(new EditorLinkTreeNode(depth, node.Name, node.GetType().Name));
            var children = node.Children;
            for (var i = children.Count - 1; i >= 0; i--)
                stack.Push((children[i], depth + 1));
        }

        return nodes.ToArray();
    }

    /// <summary>
    /// Re-reads cached scenes from disk and restarts <paramref name="pathOrUid"/> (default: the current scene's file,
    /// else <see cref="StartScene"/>). Autoloads are kept. False when there is nothing to load or loading failed.
    /// </summary>
    public bool ReloadScene(string? pathOrUid = null)
    {
        var target = pathOrUid ?? Tree.CurrentScene?.SceneFilePath ?? StartScene;
        if (string.IsNullOrEmpty(target))
        {
            Log.Warning("[Project] Reload scene: no scene to reload.");
            return false;
        }

        ResourceLoader.RefreshCachedScenes();
        try
        {
            Tree.ChangeSceneToFile(target);
            Log.Info($"[Project] Reloaded scene '{target}'.");
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or InvalidCastException or NotSupportedException
                                      or System.Text.Json.JsonException)
        {
            Log.Error($"[Project] Could not reload scene '{target}': {e.Message}");
            return false;
        }
    }

    /// <summary>Tells the editor the game is exiting (flushes queued logs, waits briefly) and closes the link.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DisconnectEditor(ExitCode.Ok);
    }

    /// <summary>Like <see cref="Dispose"/> but reports <paramref name="exitCode"/> to the editor.</summary>
    public void Shutdown(ExitCode exitCode)
    {
        if (_disposed)
            return;
        _disposed = true;
        DisconnectEditor(exitCode);
    }

    private void ConnectEditor(int port)
    {
        Link = new EditorLinkClient(port, new EditorLinkHello(EditorLinkProtocol.Version, Environment.ProcessId, Settings.Name, EngineInfo.Version));
        _linkSink = new EditorLinkLogSink(Link);
        Log.AddSink(_linkSink);
        Log.Info($"[EditorLink] Connecting to the editor on localhost:{port}.");
    }

    private void DisconnectEditor(ExitCode exitCode)
    {
        if (Link is null)
            return;
        if (_linkSink is not null)
            Log.RemoveSink(_linkSink);
        _linkSink = null;
        Link.ReportStatus(new EditorLinkStatus(GameRunState.Stopping, Tree.ProcessFrames, 0f, CurrentScenePath()));
        Link.SendGoodbye((int)exitCode, TimeSpan.FromSeconds(1));
        Link.Dispose();
        Link = null;
    }

    private void AddAutoloads()
    {
        foreach (var autoload in Settings.Autoloads)
        {
            if (!autoload.Enabled)
                continue;
            if (Tree.Root.GetNodeOrNull(autoload.Name) is not null)
            {
                Log.Error($"[Project] Autoload '{autoload.Name}': /root/{autoload.Name} already exists.");
                continue;
            }

            try
            {
                var node = CreateAutoload(autoload);
                node.Name = autoload.Name;
                Tree.Root.AddChild(node);
                _autoloads.Add(node);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or InvalidCastException or NotSupportedException
                                          or System.Text.Json.JsonException)
            {
                Log.Error($"[Project] Autoload '{autoload.Name}' could not be created: {e.Message}");
            }
        }
    }

    private Node CreateAutoload(AutoloadSettings autoload)
    {
        if (autoload.Scene is { Length: > 0 } scene)
        {
            // The autoload holds its scene for the whole run (released with the cache at shutdown).
            var packed = ResourceLoader.Load<PackedScene>(scene);
            _autoloadScenes.Add(packed);
            return packed.Instantiate();
        }

        if (autoload.Type is { Length: > 0 } type)
            return TypeRegistry.CreateNode(type);
        throw new InvalidOperationException("needs a scene or a type");
    }

    private void ReportStatus(bool force)
    {
        if (Link is not { } link)
            return;
        var state = _reportedState == GameRunState.Stopping ? GameRunState.Stopping
            : Tree.Paused ? GameRunState.Paused : GameRunState.Running;
        if (!force && state == _reportedState && _sinceStatus < StatusInterval)
            return;
        _reportedState = state;
        _sinceStatus = 0f;
        link.ReportStatus(new EditorLinkStatus(state, Tree.ProcessFrames, _framesPerSecond, CurrentScenePath()));
    }

    private string CurrentScenePath() => Tree.CurrentScene?.SceneFilePath ?? string.Empty;
}
