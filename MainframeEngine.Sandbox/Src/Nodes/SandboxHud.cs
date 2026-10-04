using MainframeEngine.UI.Rml;

namespace MainframeEngine.Sandbox;

/// <summary>
/// The Sandbox's RmlUi HUD (<c>Content/UI/hud.rml</c>): frame stats, and scene settings wired two-way to live state —
/// exposure, the box's spin speed, max FPS, the sun and coloured lights, VSync — plus buttons for the widget demo,
/// the ImGui dev overlay, the engine credits and quitting. Replaces the stats/settings part of the old ImGui window (the ImGui windows
/// remain as the F12 developer overlay).
/// </summary>
public sealed class SandboxHud : UiDocument
{
    private static readonly string[] FpsValues = ["0", "30", "60", "120", "144", "240"];

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

    private SpinningBox? FindBox() => Tree?.CurrentScene?.FindChild("Box", recursive: true) as SpinningBox;

    private Light3D? FindLight(string name) => Tree?.CurrentScene?.FindChild(name, recursive: true) as Light3D;
}
