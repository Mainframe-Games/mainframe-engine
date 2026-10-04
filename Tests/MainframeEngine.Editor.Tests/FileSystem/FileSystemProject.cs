namespace MainframeEngine.Editor.Tests.FileSystem;

/// <summary>
/// A temporary game project for the FileSystem tests:
/// <code>
/// project.mfproj                       mainScene + window.icon as paths
/// Content/Textures/wood.png (+ .meta)  a real 8×4 PNG
/// Content/Materials/Settings.mres      TestSettings, saved by ResourceSaver
/// Content/Materials/Sky.mres           a Sky with Panorama = wood.png
/// Content/Scenes/Player.mscene         AllHintsNode root, Texture = "Content/Textures/wood.png"
/// Content/Scenes/Main.mscene           Node3D root, instances Player, a child referencing Settings.mres externally
/// bin/, obj/, .git/                    hidden by default
/// </code>
/// Assigns <see cref="AssetDatabase.Current"/> and <see cref="ContentPaths.ProjectDirectory"/> (restored on dispose).
/// </summary>
public sealed class FileSystemProject : IDisposable
{
    private readonly AssetDatabase _previousDatabase = AssetDatabase.Current;
    private readonly string? _previousProject = ContentPaths.ProjectDirectory;

    public FileSystemProject()
    {
        Root = Path.TrimEndingDirectorySeparator(Directory.CreateTempSubdirectory("mf-fs-project").FullName);
        Database = new AssetDatabase(Root);
        AssetDatabase.Current = Database;
        ContentPaths.ProjectDirectory = Root;
        foreach (var folder in new[] { "Content/Scenes", "Content/Textures", "Content/Materials", "bin", "obj", ".git" })
            Directory.CreateDirectory(Abs(folder));
        File.WriteAllText(Abs("bin/Game.dll"), "x");
        File.WriteAllText(Abs(".gitignore"), "bin/\n");

        WritePng(Abs(WoodPath), 8, 4);
        Database.Scan(createMissingMeta: true);
        WoodUid = Database.GetUid(WoodPath)!;

        var settings = new TestSettings { Strength = 2f, Label = "über" };
        SettingsUid = ResourceSaver.Save(settings, SettingsPath);
        ResourceSaver.Save(new Sky { Mode = SkyEnvironmentType.Panoramic, Panorama = WoodPath }, SkyPath);

        var player = new AllHintsNode { Name = "Player", Texture = WoodPath };
        PlayerUid = SceneSaver.Save(player, PlayerPath);
        player.Free();

        var main = new Node3D { Name = "Main" };
        var instance = ResourceLoader.Load<PackedScene>(PlayerPath).Instantiate();
        main.AddChild(instance);
        instance.Owner = main;
        var hinted = new AllHintsNode { Name = "Hinted", Settings = ResourceLoader.Load<TestSettings>(SettingsPath) };
        main.AddChild(hinted);
        hinted.Owner = main;
        MainUid = SceneSaver.Save(main, MainPath);
        main.Free();

        File.WriteAllText(Abs("project.mfproj"),
            $$"""
              {
                "name": "FsTest",
                "mainScene": "{{MainPath}}",
                "window": { "title": "Test", "icon": "{{WoodPath}}" }
              }

              """.Replace("\r\n", "\n", StringComparison.Ordinal));
        ResourceLoader.ClearCache();
    }

    public const string WoodPath = "Content/Textures/wood.png";
    public const string SettingsPath = "Content/Materials/Settings.mres";
    public const string SkyPath = "Content/Materials/Sky.mres";
    public const string PlayerPath = "Content/Scenes/Player.mscene";
    public const string MainPath = "Content/Scenes/Main.mscene";

    public string Root { get; }
    public AssetDatabase Database { get; }
    public string WoodUid { get; }
    public string SettingsUid { get; }
    public string PlayerUid { get; }
    public string MainUid { get; }

    public string Abs(string projectPath) => Path.Combine(Root, projectPath.Replace('/', Path.DirectorySeparatorChar));

    public string Read(string projectPath) => File.ReadAllText(Abs(projectPath));

    public static void WritePng(string path, int width, int height, byte seed = 0)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i * 7 + seed);
            pixels[i + 1] = (byte)(i * 3);
            pixels[i + 2] = (byte)(255 - i);
            pixels[i + 3] = 255;
        }

        Png.WriteRgba8(path, width, height, pixels);
    }

    /// <summary>Polls <paramref name="condition"/> (pumping on this thread) until it holds or the deadline passes.</summary>
    public static bool WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(10);
        }

        return condition();
    }

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousDatabase;
        ContentPaths.ProjectDirectory = _previousProject;
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>An <see cref="ITrash"/> that records what it was given (and moves it aside so it is "gone"), or always fails.</summary>
public sealed class FakeTrash(bool fail = false) : ITrash, IDisposable
{
    private readonly string _bin = Directory.CreateTempSubdirectory("mf-fake-trash").FullName;

    public List<string> Trashed { get; } = [];

    public bool IsSupported => !fail;

    public bool TryMoveToTrash(string path, out string? error)
    {
        if (fail)
        {
            error = $"The trash is full ('{Path.GetFileName(path)}').";
            return false;
        }

        Trashed.Add(path);
        var target = Path.Combine(_bin, $"{Trashed.Count}-{Path.GetFileName(path)}");
        if (Directory.Exists(path))
            Directory.Move(path, target);
        else
            File.Move(path, target);
        error = null;
        return true;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_bin, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
