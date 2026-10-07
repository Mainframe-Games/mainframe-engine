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
    }

    protected override void OnReady()
    {
        base.OnReady();
        AddChild(new DemoNav { Name = "NavBar" });
    }
}

/// <summary>Tabs for every Demo scene, FPS, language picker. Number keys 1–9 switch tabs; Escape quits.</summary>
public sealed class DemoNav : UiDocument
{
    private sealed class Tab
    {
        public required DemoSceneInfo Scene { get; init; }
        public required int Index { get; init; }
        public string Title { get; set; } = "";
        public bool Active { get; set; }
    }

    private static readonly RmlStructType<Tab> TabType = new RmlStructType<Tab>()
        .Member("title", static t => t.Title)
        .Member("icon", static t => t.Scene.Icon)
        .Member("index", static t => t.Index)
        .Member("active", static t => t.Active);

    private readonly List<Tab> _tabs = [];
    private readonly List<string> _locales = [];
    private RmlDataModel? _model;
    private Node? _lastScene;
    private float _fpsTimer;
    private int _fps;

    public DemoNav()
    {
        Source = "Content/Nav/nav.rml";
        AutoFocus = false;
    }

    public string ActiveId { get; private set; } = "";

    protected override void OnReady()
    {
        for (var i = 0; i < DemoScenes.All.Count; i++)
            _tabs.Add(new Tab { Scene = DemoScenes.All[i], Index = i });
        _locales.AddRange(Tr.GetAvailableLocales());
        Translate();
        _model = CreateDataModel("nav")
            .BindList("tabs", _tabs, TabType)
            .BindList("locales", _locales)
            .Bind("locale", this, static d => Tr.CurrentLocale, static (d, v) => { if (!string.IsNullOrEmpty(v) && v != Tr.CurrentLocale) Tr.SetLocale(v); })
            .Bind("fps", this, static d => d._fps)
            .Event("open", e => Open((int)e.GetArgument(0).GetDouble()));
    }

    protected override void OnLocaleChanged()
    {
        Translate();
        _model?.Dirty("tabs");
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (!ReferenceEquals(Tree?.CurrentScene, _lastScene))
        {
            _lastScene = Tree?.CurrentScene;
            ActiveId = DemoScenes.ByRootName(_lastScene?.Name)?.Id ?? "";
            foreach (var tab in _tabs)
                tab.Active = tab.Scene.Id == ActiveId;
            _model?.Dirty("tabs");
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
        if (inputEvent is not InputEventKey { Pressed: true } key)
            return;
        if (key.Key == Key.Escape)
            Tree?.Quit(0);
        else if (key.Key is >= Key.Number1 and <= Key.Number9)
            Open(key.Key - Key.Number1);
    }

    /// <summary>Switches to tab <paramref name="index"/> (no-op when it is already open).</summary>
    public void Open(int index)
    {
        if (index < 0 || index >= DemoScenes.All.Count || DemoScenes.All[index].Id == ActiveId)
            return;
        Tree?.ChangeSceneToFile(DemoScenes.All[index].Path);
    }

    private void Translate()
    {
        // Literals at the call site so the extractor sees them.
        foreach (var tab in _tabs)
            tab.Title = tab.Scene.Id switch
            {
                "basic_3d" => Tr._("Basic 3D"),
                "basic_2d" => Tr._("Basic 2D"),
                "audio_2d" => Tr._("Audio 2D"),
                "audio_3d" => Tr._("Audio 3D"),
                "sound_fx" => Tr._("Sound FX"),
                "ui" => Tr._("UI"),
                "physics_2d" => Tr._("Physics 2D"),
                "physics_3d" => Tr._("Physics 3D"),
                "spine" => Tr._("Spine"),
                _ => tab.Scene.Title,
            };
    }
}
