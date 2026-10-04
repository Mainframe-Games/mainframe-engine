namespace MainframeEngine.Editor.Tests;

/// <summary>
/// Tests that touch process-wide state — the asset database, content paths, RmlUi — run one at a time.
/// </summary>
[CollectionDefinition(nameof(SerialEditor), DisableParallelization = true)]
public sealed class SerialEditor;

/// <summary>An editor session in an edit-mode scene tree (no GPU), with a temporary project folder.</summary>
public sealed class SessionHost : IDisposable
{
    private readonly AssetDatabase _previousDatabase = AssetDatabase.Current;
    private readonly string? _previousProject = ContentPaths.ProjectDirectory;

    public SessionHost()
    {
        ProjectDirectory = Directory.CreateTempSubdirectory("mf-editor-project").FullName;
        Directory.CreateDirectory(Path.Combine(ProjectDirectory, "Content", "Scenes"));
        Tree = new SceneTree { EditMode = true };
        Host = new Node { Name = "Host" };
        Tree.Root.AddChild(Host);
        Session = new EditorSession(Host);
    }

    public string ProjectDirectory { get; }
    public SceneTree Tree { get; }
    public Node Host { get; }
    public EditorSession Session { get; }

    public string ScenePath(string name) => Path.Combine(ProjectDirectory, "Content", "Scenes", name);

    public void Tick(int frames = 1)
    {
        for (var i = 0; i < frames; i++)
            Tree.Tick(new GameTime { DeltaTime = 1f / 60f });
    }

    public void Dispose()
    {
        Session.Dispose();
        Tree.Shutdown();
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousDatabase;
        ContentPaths.ProjectDirectory = _previousProject;
        try
        {
            Directory.Delete(ProjectDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
