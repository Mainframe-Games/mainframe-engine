using MainframeEngine.Localization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Sandbox;

/// <summary>
/// The Sandbox's RmlUi HUD (<c>Content/UI/hud.rml</c>): frame stats, and scene settings wired two-way to live state —
/// exposure, the box's spin speed, max FPS, the language, the sun and coloured lights, VSync, collision shapes — plus buttons for the
/// widget demo, the ImGui dev overlay, the engine credits and quitting. Replaces the stats/settings part of the old ImGui window (the
/// ImGui windows remain as the F12 developer overlay). Its text is translated by the UI server (M9); the language dropdown switches
/// <see cref="Tr"/>, after which the UI server reloads the document in the new language and the scene's
/// <see cref="WelcomeBanner"/> (shown at the top) re-translates.
/// </summary>
public sealed class SandboxHud : UiDocument
{
    private static readonly string[] FpsValues = ["0", "30", "60", "120", "144", "240"];

    private static readonly RmlStructType<LocaleItem> LocaleType = new RmlStructType<LocaleItem>()
        .Member("id", static l => l.Id)
        .Member("name", static l => l.Name);

    private readonly List<LocaleItem> _locales = [];
    private WelcomeBanner? _banner;
    private bool _bannerResolved;

    private RmlDataModel _model = null!;
    private float _sunEnergy = -1f;
    private readonly List<(Light3D Light, float Energy)> _lamps = [];
    private uint _frame;
    private uint _fps;
    private float _ms;
    private int _uiDraws;
    private float _statsTimer;

    /// <summary>The game whose renderer, window and scene the HUD controls (set before adding the node).</summary>
    public Engine Game { get; set; } = null!;

    /// <summary>The widget demo document toggled by the "Widgets" button (optional).</summary>
    public UiDocument? WidgetDemo { get; set; }

    /// <summary>The engine credits document toggled by the "Credits" button (optional; FreeType's licence asks for it).</summary>
    public UiDocument? Credits { get; set; }

    public SandboxHud()
    {
        Source = "Content/UI/hud.rml";
        AutoFocus = false;
    }

    protected override void OnReady()
    {
        // M9: every compiled catalog (Content/locale/<locale>/LC_MESSAGES/messages.mo), named in its own language.
        foreach (var locale in Tr.GetAvailableLocales())
            _locales.Add(new LocaleItem(locale, LocaleId.DisplayName(locale)));

        _model = CreateDataModel("sandbox")
            .Bind("fps", this, static h => (int)h._fps)
            .Bind("ms", this, static h => h._ms)
            .Bind("frame", this, static h => (int)h._frame)
            .Bind("uiDraws", this, static h => h._uiDraws)
            .Bind("exposure", this, static h => h.Exposure, static (h, v) => h.Exposure = v)
            .Bind("spin", this, static h => (int)h.SpinSpeed, static (h, v) => h.SpinSpeed = v)
            .Bind("maxFps", this, static h => h.MaxFps, static (h, v) => h.MaxFps = v)
            .Bind("sun", this, static h => h.SunOn, static (h, v) => h.SunOn = v)
            .Bind("lamps", this, static h => h.LampsOn, static (h, v) => h.LampsOn = v)
            .Bind("vsync", this, static h => h.Game.Renderer.VSync, static (h, v) => h.Game.Renderer.VSync = v)
            .Bind("shapes", this, static h => h.CollisionShapes, static (h, v) => h.CollisionShapes = v)
            .Bind("locale", this, static h => h.Locale, static (h, v) => h.Locale = v)
            .BindList("locales", _locales, LocaleType)
            .Bind("welcomeTitle", this, static h => h._banner?.DisplayTitle ?? string.Empty)
            .Bind("welcomeHint", this, static h => h._banner?.DisplayHint ?? string.Empty)
            .Event("toggleWidgets", () =>
            {
                if (WidgetDemo is not null)
                    WidgetDemo.Visible = !WidgetDemo.Visible;
            })
            .Event("toggleOverlay", () => Game.DevOverlayVisible = !Game.DevOverlayVisible)
            .Event("toggleCredits", () =>
            {
                if (Credits is not null)
                    Credits.Visible = !Credits.Visible;
            });

        GetElementById("quit")!.Click += _ => Game.Quit(ExitCode.Ok);
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        _frame = gameTime.FrameCount;
        _model.Dirty("frame");

        // The scene loads after the HUD: pick up its banner once it is there.
        if (!_bannerResolved && Tree?.CurrentScene is { } scene)
        {
            _bannerResolved = true;
            _banner = scene.FindChild("Welcome", recursive: false) as WelcomeBanner;
            DirtyWelcome();
        }

        // Stats change every frame; four updates a second are readable (and re-layout less text).
        _statsTimer -= gameTime.DeltaTime;
        if (_statsTimer > 0)
            return;
        _statsTimer = 0.25f;
        _fps = gameTime.FramesPerSecond;
        _ms = gameTime.FramesPerSecond > 0 ? 1000f / gameTime.FramesPerSecond : 0f;
        _uiDraws = Tree?.Servers.Get<UiServer>()?.Renderer?.Stats.DrawCalls ?? 0;
        _model.Dirty("fps");
        _model.Dirty("ms");
        _model.Dirty("uiDraws");
    }

    // Runs after Tr.SetLocale (the UI server reloads this document at its next frame; data models survive). The
    // banner re-translates in the same pass, so its strings are read when the views next update.
    protected override void OnLocaleChanged()
    {
        if (_model is null)
            return;
        DirtyWelcome();
        _model.Dirty("locale");
    }

    private void DirtyWelcome()
    {
        _model.Dirty("welcomeTitle");
        _model.Dirty("welcomeHint");
    }

    /// <summary>The dropdown's entry for the current locale: the first link of its chain that has a catalog (es_MX → es).</summary>
    private string Locale
    {
        get
        {
            foreach (var locale in Tr.LocaleChain)
                foreach (var item in _locales)
                    if (item.Id == locale)
                        return item.Id;
            return Tr.CurrentLocale;
        }
        set
        {
            if (!string.IsNullOrEmpty(value) && value != Locale)
                Tr.SetLocale(value);
        }
    }

    private sealed record LocaleItem(string Id, string Name);

    private float Exposure
    {
        get => Game.Renderer is IVulkanContext vk ? vk.Exposure : IVulkanContext.DefaultExposure;
        set
        {
            if (Game.Renderer is IVulkanContext vk)
                vk.Exposure = Math.Clamp(value, 0.05f, 8f);
        }
    }

    private float SpinSpeed
    {
        get => FindBox()?.DegreesPerSecond.Y ?? 0f;
        set
        {
            if (FindBox() is { } box)
                box.DegreesPerSecond = new System.Numerics.Vector3(value, value, 0);
        }
    }

    private string MaxFps
    {
        get
        {
            var fps = Game.MaxFPS.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var v in FpsValues)
                if (v == fps)
                    return v;
            return FpsValues[0];
        }
        set => Game.MaxFPS = int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var fps) ? fps : 0;
    }

    private bool SunOn
    {
        get => FindLight("Sun") is not { } sun || sun.Energy > 0;
        set
        {
            if (FindLight("Sun") is not { } sun)
                return;
            if (_sunEnergy < 0)
                _sunEnergy = sun.Energy;
            sun.Energy = value ? _sunEnergy : 0f;
        }
    }

    private bool LampsOn
    {
        get
        {
            foreach (var name in (ReadOnlySpan<string>)["BlueLamp", "WarmSpot", "GreenSpot"])
                if (FindLight(name) is { Energy: > 0 })
                    return true;
            return false;
        }
        set
        {
            if (_lamps.Count == 0)
                foreach (var name in (ReadOnlySpan<string>)["BlueLamp", "WarmSpot", "GreenSpot"])
                    if (FindLight(name) is { } light)
                        _lamps.Add((light, light.Energy));
            foreach (var (light, energy) in _lamps)
                light.Energy = value ? energy : 0f;
        }
    }

    // M6: the physics server's collision-shape debug draw (also in the F12 overlay).
    private bool CollisionShapes
    {
        get => Tree?.Servers.Get<PhysicsServer3D>()?.DebugDrawEnabled ?? false;
        set
        {
            if (Tree?.Servers.Get<PhysicsServer3D>() is { } physics)
                physics.DebugDrawEnabled = value;
        }
    }

    private SpinningBox? FindBox() => Tree?.CurrentScene?.FindChild("Box", recursive: true) as SpinningBox;

    private Light3D? FindLight(string name) => Tree?.CurrentScene?.FindChild(name, recursive: true) as Light3D;
}
