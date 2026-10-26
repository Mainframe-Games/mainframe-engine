using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text;
using MainframeEngine.Localization;
using MainframeEngine.Tests.Audio;
using MainframeEngine.Tests.Localization;
using Silk.NET.Maths;

namespace MainframeEngine.Tests.Saves;

public sealed class UserSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mf-settings-tests", Guid.NewGuid().ToString("N"));

    public UserSettingsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temp folder left behind is harmless.
        }
    }

    private string SettingsPath => Path.Combine(_directory, UserSettings.FileName);

    internal static ProjectSettings Project()
    {
        var project = new ProjectSettings { Name = "Settings Test" };
        project.Input.Bind("jump", "key:Space", "pad:A");
        project.Input.Bind("interact", "key:E");
        project.Window.VSync = true;
        project.Window.MaxFps = 0;
        project.Window.Width = 1280;
        project.Window.Height = 720;
        project.Rendering.Shadows = ShadowQuality.High;
        return project;
    }

    private UserSettings Load(string json, ProjectSettings project)
    {
        File.WriteAllText(SettingsPath, json);
        return UserSettings.Load(SettingsPath, project);
    }

    private static string Json(UserSettings settings) => Encoding.UTF8.GetString(settings.Config.ToJson());

    [Fact]
    public void OnlyValuesThePlayerChangedAreWritten()
    {
        var settings = UserSettings.Load(SettingsPath, Project());
        Assert.Equal("{\n  \"format\": 1\n}\n", Json(settings)); // nothing pinned

        settings.SetBusVolume("Music", 0.7f);
        settings.VSync = false;
        settings.Shadows = ShadowQuality.Medium;
        settings.WindowMode = WindowMode.ExclusiveFullscreen;
        settings.Save();

        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.Equal(["format", "audio", "display"], root.Select(p => p.Key));
        Assert.Equal(0.7, (double)root["audio"]!["volume"]!["Music"]!);
        Assert.Equal(["volume"], root["audio"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["vsync", "shadows", "mode"], root["display"]!.AsObject().Select(p => p.Key));
        Assert.Equal("Medium", (string)root["display"]!["shadows"]!);
        Assert.Equal("exclusiveFullscreen", (string)root["display"]!["mode"]!);

        // Back to the project's values: the keys go, and so do emptied sections.
        settings.SetBusVolume("Music", 1f);
        settings.VSync = true;
        settings.Shadows = ShadowQuality.High;
        settings.WindowMode = WindowMode.Maximized; // the project's (WindowSettings.Mode's default)
        Assert.Equal("{\n  \"format\": 1\n}\n", Json(settings));
    }

    [Fact]
    public void UnsetValuesAreTheProjectsDefaults()
    {
        var project = Project();
        project.Window.VSync = false;
        project.Window.MaxFps = 144;
        project.Rendering.Shadows = ShadowQuality.Low;
        var settings = new UserSettings(new ConfigFile(), project);

        Assert.False(settings.VSync);
        Assert.Equal(144, settings.MaxFps);
        Assert.Equal(ShadowQuality.Low, settings.Shadows);
        Assert.Equal(WindowMode.Maximized, settings.WindowMode); // the project's default
        project.Window.Mode = WindowMode.Windowed;
        Assert.Equal(WindowMode.Windowed, settings.WindowMode); // a project that opens windowed
        Assert.Equal(WindowMode.Windowed, settings.PatchEngineOptions(project.ToEngineOptions()).WindowMode);
        settings.WindowMode = WindowMode.Fullscreen;
        Assert.Equal(WindowMode.Fullscreen, settings.PatchEngineOptions(project.ToEngineOptions()).WindowMode); // the player's choice wins
        Assert.Null(settings.WindowSize);
        Assert.Equal(1f, settings.UiScale);
        Assert.Null(settings.Language);
        Assert.Equal(1f, settings.GetBusVolume("Master"));
        Assert.False(settings.IsBusMuted("Music"));
        Assert.Null(settings.FilePath);
        Assert.Throws<InvalidOperationException>(settings.Save);
    }

    [Fact]
    public void ValuesAreClampedAndValidated()
    {
        var settings = new UserSettings(new ConfigFile(), Project());
        settings.UiScale = 5f;
        Assert.Equal(UserSettings.MaxUiScale, settings.UiScale);
        settings.UiScale = 0.1f;
        Assert.Equal(UserSettings.MinUiScale, settings.UiScale);
        settings.SetBusVolume("SFX", -1f);
        Assert.Equal(0f, settings.GetBusVolume("SFX"));
        settings.SetBusVolume("SFX", float.NaN);
        Assert.Equal(1f, settings.GetBusVolume("SFX"));
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaxFps = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.WindowSize = new Vector2I(0, 600));
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.WindowMode = (WindowMode)9);
        Assert.Throws<ArgumentException>(() => settings.Language = "not a locale!");

        // Hand-edited garbage reads as the defaults.
        var edited = Load("""
            { "audio": { "volume": { "Music": "loud" }, "mute": 3 },
              "display": { "mode": "sideways", "size": [0, 2], "maxFps": -5, "vsync": "yes", "shadows": "Ultra" } }
            """, Project());
        Assert.Equal(1f, edited.GetBusVolume("Music"));
        Assert.False(edited.IsBusMuted("Music"));
        Assert.Equal(WindowMode.Maximized, edited.WindowMode);
        Assert.Null(edited.WindowSize);
        Assert.Equal(0, edited.MaxFps);
        Assert.True(edited.VSync);
        Assert.Equal(ShadowQuality.High, edited.Shadows);
    }

    [Fact]
    public void InputOverridesStoreFullListsOnlyForChangedActions()
    {
        var project = Project();
        var settings = new UserSettings(new ConfigFile(), project);
        var changes = new List<UserSettingsSection>();
        settings.Changed += changes.Add;
        var map = project.Input; // the live map (GameSession installs the project's)

        map.Unbind("interact", InputBinding.Parse("key:E"));
        map.Bind("interact", "key:F", "pad:X");
        settings.StoreInput(map);

        Assert.True(settings.HasInputOverride("interact"));
        Assert.False(settings.HasInputOverride("jump"));
        var stored = new List<string>();
        Assert.True(settings.Config.GetStrings("input", "interact", stored));
        Assert.Equal(["key:F", "pad:X"], stored);
        Assert.Equal([UserSettingsSection.Input], changes);
        Assert.Equal(["key:E"], settings.DefaultInput.GetAction("interact")!.Bindings.Select(b => b.ToString())); // defaults kept

        settings.StoreInput(map); // nothing new
        Assert.Single(changes);

        // Order matters (slots): the same keys in another order is an override too.
        map.Unbind("jump", InputBinding.Parse("key:Space"));
        map.Bind("jump", "key:Space");
        settings.StoreInput(map);
        Assert.True(settings.HasInputOverride("jump"));

        // Rebinding back to the defaults removes the override.
        settings.Reset(UserSettingsSection.Input); // no tree attached: only the file
        settings.StoreInput(settings.DefaultInput.Clone());
        Assert.False(settings.Config.HasSection("input"));
    }

    [Fact]
    public void InputOverridesApplyAndUnknownActionsAreDropped()
    {
        var project = Project();
        var settings = Load("""
            { "format": 1,
              "input": { "interact": ["key:F", "pad:X", "key:NotAKey"], "removed": ["key:G"], "jump": "key:J" } }
            """, project);
        var tree = new SceneTree();
        tree.Input.Map = project.Input;
        var version = project.Input.Version;
        var sink = new MemoryLogSink { Levels = Log.Level.Warning };
        Log.AddSink(sink);
        try
        {
            settings.Apply(tree);
        }
        finally
        {
            Log.RemoveSink(sink);
        }

        Assert.Equal(["key:F", "pad:X"], tree.Input.Map.GetAction("interact")!.Bindings.Select(b => b.ToString()));
        Assert.Equal(["key:Space", "pad:A"], tree.Input.Map.GetAction("jump")!.Bindings.Select(b => b.ToString())); // a bad list is ignored
        Assert.True(project.Input.Version > version); // input states refresh
        Assert.Equal(["key:E"], settings.DefaultInput.GetAction("interact")!.Bindings.Select(b => b.ToString()));
        Assert.Equal(["interact"], settings.Config.GetSectionKeys("input")); // "removed" and the broken "jump" are dropped
        var warnings = sink.Snapshot().Where(e => e.Message.Contains(SettingsPath, StringComparison.Ordinal)).Select(e => e.Message).ToList();
        Assert.Contains(warnings, m => m.Contains("'removed' is not in the project", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains("key:NotAKey", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains("input.jump is not an array of strings", StringComparison.Ordinal));
        tree.Shutdown();
    }

    [Fact]
    public void BusVolumesAndMutesApplyAndUnknownBusesAreDropped()
    {
        var settings = Load("""
            { "audio": { "volume": { "Music": 0.5, "Ghost": 0.3 }, "mute": ["Voice", "Phantom"] } }
            """, Project());
        var tree = new SceneTree();
        using var audio = AudioTestUtil.CreateServer(tree);
        var musicFader = audio.GetBus("Music")!.VolumeDb;

        settings.Apply(tree);

        Assert.Equal(0.5f, audio.GetBus("Music")!.UserVolume);
        Assert.Equal(1f, audio.GetBus("SFX")!.UserVolume);
        Assert.True(audio.GetBus("Voice")!.Mute);
        Assert.Equal(musicFader, audio.GetBus("Music")!.VolumeDb); // the designer's fader is untouched
        Assert.Equal(["Music"], settings.Config.GetNode("audio", "volume")!.AsObject().Select(p => p.Key));
        var muted = new List<string>();
        settings.Config.GetStrings("audio", "mute", muted);
        Assert.Equal(["Voice"], muted);

        // Live: the setters reach the buses.
        settings.SetBusVolume("SFX", 0.25f);
        settings.SetBusMuted("Voice", false);
        Assert.Equal(0.25f, audio.GetBus("SFX")!.UserVolume);
        Assert.False(audio.GetBus("Voice")!.Mute);
        Assert.False(settings.Config.HasSectionKey("audio", "mute"));

        settings.Reset(UserSettingsSection.Audio);
        Assert.All(audio.Buses, bus => Assert.Equal(1f, bus.UserVolume));
        Assert.False(settings.Config.HasSection("audio"));
        tree.Shutdown();
    }

    [Fact]
    public void EngineOptionsArePatchedBeforeTheEngineStarts()
    {
        var project = Project();
        var settings = Load("""
            { "display": { "vsync": false, "size": [1600, 900], "maxFps": 90 }, "locale": { "language": "es" } }
            """, project);

        var options = settings.PatchEngineOptions(project.ToEngineOptions());

        Assert.False(options.VSync);
        Assert.Equal(new Vector2D<int>(1600, 900), options.WindowSize);
        Assert.Equal("es", options.Locale);

        // Unset values keep the project's; command-line flags still win (GameHost applies them after).
        var untouched = new UserSettings(new ConfigFile(), project).PatchEngineOptions(project.ToEngineOptions());
        Assert.True(untouched.VSync);
        Assert.Equal(new Vector2D<int>(1280, 720), untouched.WindowSize);
        Assert.Null(untouched.Locale);
        Assert.Equal("pt", new GameHostOptions { Locale = "pt" }.Apply(options).Locale);
    }

    [Fact]
    public void LiveChangesApplyToTheWindowAndServersAndRaiseChanged()
    {
        var project = Project();
        var settings = new UserSettings(new ConfigFile(), project);
        var (tree, window, render) = AttachedTree();
        settings.Apply(tree);
        Assert.Same(tree, settings.Tree);
        var changes = new List<UserSettingsSection>();
        settings.Changed += changes.Add;

        settings.WindowMode = WindowMode.Fullscreen;
        settings.VSync = false;
        settings.MaxFps = 120;
        settings.WindowSize = new Vector2I(1600, 900); // stored; the window is fullscreen, so its size waits
        settings.Shadows = ShadowQuality.Low;

        Assert.Equal(WindowMode.Fullscreen, window.Mode);
        Assert.False(window.VSync);
        Assert.Equal(120, window.MaxFps);
        Assert.Equal(new Vector2(1280, 720), window.Size);
        Assert.Equal(ShadowQuality.Low, render.ShadowQuality);
        Assert.Equal(Enumerable.Repeat(UserSettingsSection.Display, 5), changes);

        settings.WindowMode = WindowMode.Fullscreen; // unchanged: no event
        settings.VSync = false;
        Assert.Equal(5, changes.Count);

        settings.WindowMode = WindowMode.Windowed;
        settings.WindowSize = new Vector2I(1920, 1080);
        Assert.Equal(new Vector2(1920, 1080), window.Size);
        settings.WindowSize = null; // the project's size
        Assert.Equal(new Vector2(1280, 720), window.Size);

        settings.Reset(UserSettingsSection.Display);
        Assert.Equal(UserSettingsSection.Display, changes[^1]);
        Assert.True(window.VSync);
        Assert.Equal(0, window.MaxFps);
        Assert.Equal(ShadowQuality.High, render.ShadowQuality);
        Assert.False(settings.Config.HasSection("display"));
        tree.Shutdown();
    }

    [Fact]
    public void StartupAppliesOnlyWhatThePlayerSet()
    {
        var settings = Load("""{ "display": { "mode": "maximized", "maxFps": 75, "shadows": "Off" } }""", Project());
        var (tree, window, render) = AttachedTree();
        window.VSync = false; // what the host started with (e.g. --no-vsync): unset values are left alone

        settings.Apply(tree);

        Assert.Equal(WindowMode.Maximized, window.Mode);
        Assert.Equal(75, window.MaxFps);
        Assert.False(window.VSync);
        Assert.Equal(ShadowQuality.Off, render.ShadowQuality); // Off works at startup: no visuals yet
        Assert.False(render.ShadowsEnabled);
        Assert.False(settings.ShadowsNeedRestart);
        tree.Shutdown();
    }

    [Fact]
    public void ShadowsToOrFromOffWaitForARestartOnceVisualsExist()
    {
        var settings = new UserSettings(new ConfigFile(), Project());
        var (tree, _, render) = AttachedTree();
        settings.Apply(tree);
        var visual = new FakeVisual();
        render.Track(visual); // a visual created GPU resources

        settings.Shadows = ShadowQuality.Medium; // live
        Assert.Equal(ShadowQuality.Medium, render.ShadowQuality);
        Assert.False(settings.ShadowsNeedRestart);

        settings.Shadows = ShadowQuality.Off; // saved, applies at the next start
        Assert.Equal(ShadowQuality.Medium, render.ShadowQuality);
        Assert.True(settings.ShadowsNeedRestart);
        Assert.Equal("Off", settings.Config.GetString("display", "shadows"));

        settings.Shadows = ShadowQuality.Low; // back on: live again
        Assert.Equal(ShadowQuality.Low, render.ShadowQuality);
        Assert.False(settings.ShadowsNeedRestart);
        render.Untrack(visual);
        tree.Shutdown();
    }

    [Fact]
    public void GameSectionsShareTheFileAndSurviveARestart()
    {
        var project = Project();
        var settings = UserSettings.Load(SettingsPath, project);
        settings.Config.SetValue("game", "subtitles", true);
        settings.Config.SetValue("game", "fov", 80);
        settings.SetBusMuted("Voice", true);
        settings.UiScale = 1.25f;
        settings.WindowSize = new Vector2I(1600, 900);
        settings.Language = "es";
        settings.Save();

        var reloaded = UserSettings.Load(SettingsPath, project);
        Assert.True(reloaded.Config.GetBool("game", "subtitles"));
        Assert.Equal(80, reloaded.Config.GetInt("game", "fov"));
        Assert.True(reloaded.IsBusMuted("Voice"));
        Assert.Equal(1.25f, reloaded.UiScale);
        Assert.Equal(new Vector2I(1600, 900), reloaded.WindowSize);
        Assert.Equal("es", reloaded.Language);

        reloaded.Reset(UserSettingsSection.Game);
        Assert.False(reloaded.Config.HasSection("game"));
        Assert.True(reloaded.IsBusMuted("Voice")); // the engine's sections stay
    }

    [Fact]
    public void TheSessionAppliesSettingsBeforeTheAutoloads()
    {
        var project = Project();
        project.Autoloads.Add(new AutoloadSettings { Name = "Probe", Type = nameof(SettingsProbeNode) });
        var settings = Load("""{ "input": { "interact": ["key:F"] } }""", project);
        var tree = new SceneTree();
        using var session = new GameSession(tree, project) { UserSettings = settings };

        session.Start(); // no main scene: logs a warning, still prepares

        Assert.Same(tree, settings.Tree);
        Assert.Equal("key:F", ((SettingsProbeNode)tree.Root.GetNode("/root/Probe")).InteractOnReady);
        tree.Shutdown();
    }

    [Fact]
    public void TheDefaultPathIsInTheUserDataFolder()
    {
        var project = Project();
        project.UserDataName = "StableName";
        Assert.Equal(Path.Combine(UserDataPaths.GetDirectory("StableName"), "settings.json"), UserSettings.GetPath(project));
    }

    private static (SceneTree Tree, FakeWindow Window, RenderServer Render) AttachedTree()
    {
        var tree = new SceneTree();
        var window = new FakeWindow();
        tree.Window = window;
        var render = new RenderServer(new HeadlessRenderer());
        tree.Servers.Register(render);
        return (tree, window, render);
    }

    private sealed class FakeVisual : IRenderResourceOwner
    {
        public void ReleaseRenderResourcesForShutdown()
        {
        }
    }

    /// <summary>An <see cref="IRenderer"/> that is not an <see cref="IVulkanContext"/> (no shadow system is ever created).</summary>
    private sealed class HeadlessRenderer : IRenderer
    {
        public RenderingBackend Backend => RenderingBackend.Vulkan;
        public bool VSync { get; set; }
        public void OnResize(Vector2D<int> newSize) { }
        public void BeginFrame() { }
        public void EndFrame() { }
        public void SetClearColor(float r, float g, float b, float a = 1) { }
        public void Clear() { }
        public void EnableDepthTest() { }
        public void DisableDepthTest() { }
        public void RequestCapture() { }

        public bool TryTakeCapture([NotNullWhen(true)] out FrameCapture? capture)
        {
            capture = null;
            return false;
        }

        public void Dispose() { }
    }
}

/// <summary>The player's language goes through <see cref="Tr"/>, which is process-global.</summary>
[Collection(nameof(LocalizationState))]
public sealed class UserSettingsLanguageTests
{
    [Fact]
    public void TheLanguageAppliesAtStartAndLive()
    {
        using var fixture = new LocaleFixture();
        fixture.AddCatalog("es", LocaleFixture.OneOther, "msgid \"Play\"\nmsgstr \"Jugar\"\n");
        fixture.AddCatalog("fr", LocaleFixture.OneOther, "msgid \"Play\"\nmsgstr \"Jouer\"\n");
        fixture.Use("en");
        var config = new ConfigFile();
        config.SetValue("locale", "language", "es");
        var settings = new UserSettings(config, UserSettingsTests.Project());
        var changes = new List<UserSettingsSection>();
        settings.Changed += changes.Add;

        settings.Apply(new SceneTree());
        Assert.Equal("es", Tr.CurrentLocale);
        Assert.Equal("Jugar", Tr._("Play"));

        settings.Language = "fr";
        Assert.Equal("fr", Tr.CurrentLocale);
        Assert.Equal([UserSettingsSection.Language], changes);

        settings.Language = null; // the project's default, else the OS language
        Assert.Null(settings.Config.GetString("locale", "language"));
        Assert.Equal(Tr.ResolveStartupLocale(null), Tr.CurrentLocale);
        Assert.Equal(2, changes.Count);
    }
}

/// <summary>An autoload that records what the player's settings had done to the input map when it became ready.</summary>
internal sealed class SettingsProbeNode : Node
{
    public string? InteractOnReady { get; private set; }

    protected override void OnReady() => InteractOnReady = Tree?.Input.Map.GetAction("interact")?.Bindings[0].ToString();
}

/// <summary>A window for tests: remembers what was set.</summary>
internal sealed class FakeWindow : IWindowControl
{
    public string Title { get; set; } = "Test";

    public bool Fullscreen
    {
        get => Mode is WindowMode.Fullscreen or WindowMode.ExclusiveFullscreen;
        set => Mode = value ? WindowMode.Fullscreen : WindowMode.Windowed;
    }

    public WindowMode Mode { get; set; }

    public Vector2 Size { get; set; } = new(1280, 720);

    public Vector2 Position { get; set; }

    public Rect2 ScreenBounds => new(0, 0, 1920, 1080);

    public bool VSync { get; set; } = true;

    public int MaxFps { get; set; }
}
