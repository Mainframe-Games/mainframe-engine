using MainframeEngine;
using MainframeEngine.Localization;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;

namespace Demo;

/// <summary>The autoload: a top UI layer holding the nav bar (survives scene changes).</summary>
public sealed class DemoNavLayer : UiLayer
{
    public DemoNavLayer()
    {
        Name = "Nav";
        Layer = 100;
        ProcessMode = ProcessMode.Always; // FPS and the active tab keep updating while the settings menu pauses the game
    }

    protected override void OnReady()
    {
        base.OnReady();
        AddChild(new DemoNav { Name = "NavBar" });
        if (DemoQa.ModeFromArgs() is { } qa)
            AddChild(new DemoQa(qa) { Name = "Qa" }); // `++ --qa …` (just qa-settings)
    }
}

/// <summary>
/// Two rows: the scene categories (with FPS, language picker and the settings menu: gear, F12 hint, F10 / Start), and
/// under them the open category's scenes. The input actions <c>scene_next</c>/<c>scene_prev</c> (Page Down/Up, RB/LB)
/// step through every scene, crossing categories; number keys 1–9 open a category; <c>quit</c> (Escape) quits.
/// </summary>
public sealed class DemoNav : UiDocument
{
    private sealed class Tab
    {
        public required int Index { get; init; }
        public string Title { get; set; } = "";
        public bool Active { get; set; }
    }

    private static readonly RmlStructType<Tab> TabType = new RmlStructType<Tab>()
        .Member("title", static t => t.Title)
        .Member("index", static t => t.Index)
        .Member("active", static t => t.Active);

    private readonly List<Tab> _categories = [];
    private readonly List<Tab> _sceneTabs = []; // one per scene, in DemoScenes.All order
    private readonly List<Tab> _shownScenes = []; // the open category's
    private readonly int[] _lastInCategory = new int[DemoScenes.Categories.Count];
    private readonly List<string> _locales = [];
    private RmlDataModel? _model;
    private Node? _lastScene;
    private int _category = -1;
    private float _fpsTimer;
    private int _fps;
    private bool _settingsArgHandled;

    public DemoNav()
    {
        Source = "Assets/Nav/nav.rml";
        AutoFocus = false;
    }

    public string ActiveId { get; private set; } = "";

    /// <summary>The open category's id (<see cref="DemoScenes.Categories"/>).</summary>
    public string ActiveCategory => DemoScenes.Categories[Math.Max(_category, 0)].Id;

    /// <summary>The ids of the scenes the second row shows.</summary>
    public IEnumerable<string> ShownSceneIds => _shownScenes.Select(t => DemoScenes.All[t.Index].Id);

    protected override void OnReady()
    {
        for (var i = 0; i < DemoScenes.Categories.Count; i++)
        {
            _categories.Add(new Tab { Index = i });
            _lastInCategory[i] = FirstSceneOf(i);
        }
        for (var i = 0; i < DemoScenes.All.Count; i++)
            _sceneTabs.Add(new Tab { Index = i });
        _locales.AddRange(Tr.GetAvailableLocales());
        Translate();
        ShowCategory(0);
        _model = CreateDataModel("nav")
            .BindList("categories", _categories, TabType)
            .BindList("scenes", _shownScenes, TabType)
            .BindList("locales", _locales)
            .Bind("locale", this, static d => Tr.CurrentLocale, static (d, v) => SetLanguage(v))
            .Bind("fps", this, static d => d._fps)
            .Event("category", e => OpenCategory((int)e.GetArgument(0).GetDouble()))
            .Event("open", e => Open((int)e.GetArgument(0).GetDouble()))
            .Event("settings", () => OpenSettings());
    }

    protected override void OnLocaleChanged()
    {
        Translate();
        _model?.Dirty("categories");
        _model?.Dirty("scenes");
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        // `++ --settings` (build/demo-screenshots.sh): open the settings menu on the first frame.
        if (!_settingsArgHandled)
        {
            _settingsArgHandled = true;
            if (GameHost.UserArgs.Contains("--settings"))
                OpenSettings();
        }

        if (!ReferenceEquals(Tree?.CurrentScene, _lastScene))
        {
            _lastScene = Tree?.CurrentScene;
            var scene = DemoScenes.ByRootName(_lastScene?.Name);
            ActiveId = scene?.Id ?? "";
            foreach (var tab in _sceneTabs)
                tab.Active = DemoScenes.All[tab.Index].Id == ActiveId;
            if (scene is not null)
            {
                var category = DemoScenes.CategoryIndex(scene.Category);
                _lastInCategory[category] = _sceneTabs.FindIndex(t => t.Active);
                ShowCategory(category);
            }
            _model?.Dirty("scenes");
        }

        _fpsTimer += gameTime.DeltaTime;
        if (_fpsTimer >= 0.5f)
        {
            _fps = (int)MathF.Round(1f / MathF.Max(gameTime.DeltaTime, 1e-4f));
            _fpsTimer = 0f;
            _model?.Dirty("fps");
        }
    }

    protected override void OnInput(InputEvent inputEvent)
    {
        var map = Tree?.Input.Map;
        var actions = map?.HasAction("quit") == true;
        if (inputEvent.IsActionPressed("settings", map))
            OpenSettings();
        else if (inputEvent.IsActionPressed("scene_next", map))
            Step(1);
        else if (inputEvent.IsActionPressed("scene_prev", map))
            Step(-1);
        else if (actions ? inputEvent.IsActionPressed("quit", map) : inputEvent is InputEventKey { Pressed: true, Key: Key.Escape })
            Tree?.Quit(0);
        else if (inputEvent is InputEventKey { Pressed: true, Key: >= Key.Number1 and <= Key.Number9 } key)
            OpenCategory(key.Key - Key.Number1);
    }

    /// <summary>Opens the engine's settings menu (all tabs; the game pauses while it is open).</summary>
    public SettingsMenu? OpenSettings()
    {
        if (Tree is not { } tree)
            return null;
        return SettingsMenu.Open(tree, new SettingsMenuOptions
        {
            PauseTree = true,
            BusLabels = { ["SFX"] = Tr._("Effects"), ["UI"] = Tr._("Interface") },
        });
    }

    private void Step(int step)
    {
        var count = DemoScenes.All.Count;
        var at = DemoScenes.All.ToList().FindIndex(s => s.Id == ActiveId);
        Open(((at < 0 ? 0 : at + step) % count + count) % count);
    }

    private static void SetLanguage(string locale)
    {
        if (string.IsNullOrEmpty(locale) || locale == Tr.CurrentLocale)
            return;
        if (UserSettings.Current is { } settings)
        {
            settings.Language = locale; // applies it and remembers it for the next start
            settings.Save();
        }
        else
        {
            Tr.SetLocale(locale);
        }
    }

    /// <summary>Opens category <paramref name="index"/>: the scene last open in it, else its first.</summary>
    public void OpenCategory(int index)
    {
        if (index >= 0 && index < DemoScenes.Categories.Count)
            Open(_lastInCategory[index]);
    }

    private static int FirstSceneOf(int category)
    {
        var id = DemoScenes.Categories[category].Id;
        for (var i = 0; i < DemoScenes.All.Count; i++)
        {
            if (DemoScenes.All[i].Category == id)
                return i;
        }
        return -1;
    }

    private void ShowCategory(int category)
    {
        if (category == _category)
            return;
        _category = category;
        for (var i = 0; i < _categories.Count; i++)
            _categories[i].Active = i == category;
        _shownScenes.Clear();
        var id = DemoScenes.Categories[category].Id;
        foreach (var tab in _sceneTabs)
        {
            if (DemoScenes.All[tab.Index].Category == id)
                _shownScenes.Add(tab);
        }
        _model?.Dirty("categories");
    }

    /// <summary>Switches to scene <paramref name="index"/> of <see cref="DemoScenes.All"/> (no-op when it is already open).</summary>
    public void Open(int index)
    {
        if (index < 0 || index >= DemoScenes.All.Count || DemoScenes.All[index].Id == ActiveId)
            return;
        Tree?.ChangeSceneToFile(DemoScenes.All[index].Path);
    }

    private void Translate()
    {
        // Literals at the call site so the extractor sees them.
        foreach (var tab in _categories)
            tab.Title = DemoScenes.Categories[tab.Index].Id switch
            {
                "3d" => Tr._("3D"),
                "2d" => Tr._("2D"),
                "physics" => Tr._("Physics"),
                "animation" => Tr._("Animation"),
                "audio" => Tr._("Audio"),
                "ui" => Tr._("UI"),
                _ => DemoScenes.Categories[tab.Index].Title,
            };
        foreach (var tab in _sceneTabs)
            tab.Title = DemoScenes.All[tab.Index].Id switch
            {
                "basic_3d" => Tr._("Basic 3D"),
                "particles" => Tr._("Particles"),
                "shaders" => Tr._("Shaders"),
                "basic_2d" => Tr._("Basic 2D"),
                "sprites_2d" => Tr._("Sprites 2D"),
                "tiles_2d" => Tr._("Tiles 2D"),
                "navigation_2d" => Tr._("Navigation 2D"),
                "navmesh_2d" => Tr._("Navigation Mesh 2D"),
                "paths_2d" => Tr._("Paths 2D"),
                "physics_2d" => Tr._("Physics 2D"),
                "physics_3d" => Tr._("Physics 3D"),
                "paths_3d" => Tr._("Paths 3D"),
                "navigation_3d" => Tr._("Navigation 3D"),
                "navmesh_3d" => Tr._("Navigation Mesh 3D"),
                "spine" => Tr._("Spine"),
                "animation_3d" => Tr._("Animation 3D"),
                "audio_2d" => Tr._("Audio 2D"),
                "audio_3d" => Tr._("Audio 3D"),
                "sound_fx" => Tr._("Sound FX"),
                "adaptive_music" => Tr._("Music"),
                "ui" => Tr._("UI"),
                "world_ui" => Tr._("World UI"),
                _ => DemoScenes.All[tab.Index].Title,
            };
    }
}
