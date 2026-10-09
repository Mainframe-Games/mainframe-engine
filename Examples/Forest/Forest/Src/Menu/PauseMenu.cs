using System.Globalization;
using System.Text;
using MainframeEngine;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;

namespace Forest;

/// <summary>
/// The Forest's pause menu (ADR 0180): <c>pause</c> (Escape, gamepad Start) opens it over the running world and closes
/// it again. Pages: <b>Graphics</b>, <b>Audio</b> and <b>Controls</b> (every <see cref="ForestOptions"/> row, applied
/// live, saved to the player's <c>settings.json</c> when the menu closes) and <b>Cameras</b> (the fly-over, the reference
/// shots R1–R8, a free camera, back to the player: <see cref="ForestCameras"/>), plus Resume and Quit.
/// </summary>
/// <remarks>
/// The document is generated from <see cref="ForestOptions.All"/> (<see cref="BuildRml"/>) and styled by
/// <c>Content/UI/menu.rcss</c>. While it is open the player is disabled and the mouse is free; the world keeps running
/// (wind, water, birds, a fly-over behind the menu). It sees input before the UI (<see cref="Node.InputBeforeUi"/>) so
/// Escape and Start toggle it even while a slider has focus; gamepad B closes it. Closed, its layer is hidden and it does
/// nothing per frame but check two flags: 0 B.
/// </remarks>
public sealed class PauseMenu : UiDocument
{
    public const string CamerasPage = "cameras";

    /// <summary>The menu's pages, in navigation order.</summary>
    public static readonly string[] Pages = [ForestOptions.Graphics, ForestOptions.Audio, ForestOptions.Controls, CamerasPage];

    /// <summary>The menu's UI layer: above the game's, below the frame-rate readout (100).</summary>
    public const int LayerOrder = 50;

    private ForestWorld _world = new();
    private ForestCameras? _cameras;
    private string? _settingsPath;
    private string[] _texts = [];
    private RmlDataModel? _model;
    private bool _changed;
    private bool _unsaved;
    private bool _pauseHeld;
    private PendingAction _pending;
    private int _pendingShot;
    private string _state = "Paused";

    public PauseMenu()
    {
        Name = "PauseMenu";
        Visible = false;
        InputBeforeUi = true;
    }

    /// <summary>The page shown (<see cref="Pages"/>).</summary>
    public string Page { get; private set; } = ForestOptions.Graphics;

    public bool IsOpen { get; private set; }

    public ForestWorld World => _world;

    public ForestCameras? Cameras => _cameras;

    /// <summary>Where the settings are saved (null: not saved, as in tests and scripted runs).</summary>
    public string? SettingsPath => _settingsPath;

    /// <summary>How many times the settings were written (tests).</summary>
    public int Saves { get; private set; }

    /// <summary>
    /// Adds the menu to <paramref name="parent"/> on its own hidden UI layer (<see cref="LayerOrder"/>), acting on
    /// <paramref name="world"/> and <paramref name="cameras"/> and saving to <paramref name="settingsPath"/>. The player
    /// stops releasing the mouse on <c>pause</c> itself: the menu does.
    /// </summary>
    public static PauseMenu Attach(Node parent, ForestWorld world, ForestCameras cameras, string? settingsPath)
    {
        var layer = new UiLayer { Name = "MenuLayer", Layer = LayerOrder, Visible = false };
        var menu = new PauseMenu { _world = world, _cameras = cameras, _settingsPath = settingsPath, Rml = BuildRml() };
        layer.AddChild(menu);
        parent.AddChild(layer);
        if (world.Player is { } player)
            player.ReleaseMouseOnPause = false;
        cameras.ModeChanged += menu.OnCameraModeChanged;
        return menu;
    }

    protected override void OnReady()
    {
        _texts = new string[ForestOptions.All.Length];
        RefreshTexts();
        var model = _model = CreateDataModel("menu");
        model.Bind("page", this, static m => m.Page)
            .Bind("state", this, static m => m._state)
            .Bind("cam", this, static m => (int)(m._cameras?.Mode ?? ForestCameraMode.Player))
            .Bind("shot", this, static m => m._cameras is { Mode: ForestCameraMode.Shot } cameras ? cameras.ShotIndex : -1);
        for (var i = 0; i < ForestOptions.All.Length; i++)
        {
            var option = ForestOptions.All[i];
            var index = i;
            switch (option.Kind)
            {
                case ForestOptionKind.Slider:
                    model.Bind(option.Key, () => _world.Value(option), v => OnSlider(option, v));
                    break;
                case ForestOptionKind.Toggle:
                    model.Bind(option.Key, () => _world.Value(option) > 0.5f);
                    break;
                default:
                    model.Bind(option.Key, () => (int)_world.Value(option));
                    break;
            }

            model.Bind(option.Key + "_t", () => _texts[index]);
        }

        model.Event("show", e => ShowPage(e.ArgumentCount > 0 ? e.GetArgument(0).GetString() : Page))
            .Event("pick", e =>
            {
                if (e.ArgumentCount >= 2 && ForestOptions.Find(e.GetArgument(0).GetString()) is { } option)
                    Set(option, e.GetArgument(1).GetSingle());
            })
            .Event("reset", () => ResetPage())
            .Event("resume", () => _pending = PendingAction.Close)
            .Event("quit", () => _pending = PendingAction.Quit)
            .Event("flyover", () => _pending = PendingAction.FlyOver)
            .Event("freefly", () => _pending = PendingAction.FreeFly)
            .Event("player", () => _pending = PendingAction.Player)
            .Event("goshot", e =>
            {
                _pendingShot = e.ArgumentCount > 0 ? e.GetArgument(0).GetInt32() : 0;
                _pending = PendingAction.Shot;
            });
    }

    /// <summary>Opens the menu (on <paramref name="page"/> when given), stopping the player and freeing the mouse.</summary>
    public void Open(string? page = null)
    {
        if (page is not null && Array.IndexOf(Pages, page) >= 0)
            Page = page;
        if (!IsOpen)
        {
            IsOpen = true;
            if (Layer is { } layer)
                layer.Visible = true;
            Visible = true;
            Modal = true;
            if (_cameras is not null)
                _cameras.MenuOpen = true;
        }

        _changed = true; // refresh every value: the scene may have changed (F3, --set, a camera)
        Refresh();
    }

    /// <summary>Closes the menu, gives control back to the camera mode and saves changed settings.</summary>
    public void Close()
    {
        if (!IsOpen)
            return;
        IsOpen = false;
        Visible = false;
        Modal = false;
        if (Layer is { } layer)
            layer.Visible = false;
        if (_cameras is not null)
            _cameras.MenuOpen = false;
        Save();
    }

    /// <summary>Opens the menu, or closes it. Outside the player's view it opens on the Cameras page.</summary>
    public void Toggle()
    {
        if (IsOpen)
            Close();
        else
            Open(_cameras is { Mode: not ForestCameraMode.Player } ? CamerasPage : null);
    }

    /// <summary>Shows a page (unknown names are ignored).</summary>
    public void ShowPage(string page)
    {
        if (Array.IndexOf(Pages, page) < 0)
            return;
        Page = page;
        _changed = true;
    }

    /// <summary>Sets an option from the menu (applied live, saved when the menu closes).</summary>
    public void Set(ForestOption option, float value)
    {
        _world.Apply(option, value);
        _unsaved = true;
        _changed = true;
    }

    /// <summary>"Reset to defaults" for the current page.</summary>
    public void ResetPage()
    {
        _world.Reset(Page);
        _unsaved = true;
        _changed = true;
    }

    /// <summary>Writes the settings if they changed since the last save (no-op without <see cref="SettingsPath"/>).</summary>
    public void Save()
    {
        if (!_unsaved || _settingsPath is null)
            return;
        try
        {
            _world.Settings.Save(_settingsPath);
            _unsaved = false;
            Saves++;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Forest] Could not save the settings to '{_settingsPath}': {e.Message}");
        }
    }

    protected override void OnInput(InputEvent inputEvent)
    {
        var map = Tree?.Input.Map;
        if (inputEvent.IsActionPressed("pause", map))
        {
            if (!_pauseHeld) // key repeat
            {
                _pauseHeld = true;
                Toggle();
            }

            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (inputEvent.IsActionReleased("pause", map))
        {
            _pauseHeld = false;
            return;
        }

        if (IsOpen && inputEvent is InputEventGamepadButton { Pressed: true, Button: ButtonName.B })
        {
            Close();
            GetViewport()?.SetInputAsHandled();
        }
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (_pending != PendingAction.None)
            RunPending();
        if (IsOpen && _changed)
            Refresh();
    }

    // Actions from click handlers run here, outside RmlUi's event dispatch (they hide the layer and switch cameras).
    private void RunPending()
    {
        var action = _pending;
        _pending = PendingAction.None;
        switch (action)
        {
            case PendingAction.Close:
                Close();
                break;
            case PendingAction.Quit:
                Save();
                Tree?.Quit(0);
                break;
            case PendingAction.FlyOver:
                _cameras?.StartFlyOver();
                Close();
                break;
            case PendingAction.FreeFly:
                _cameras?.StartFreeFly();
                Close();
                break;
            case PendingAction.Player:
                _cameras?.ReturnToPlayer();
                Close();
                break;
            case PendingAction.Shot:
                _cameras?.ShowShot(_pendingShot);
                Close();
                break;
        }
    }

    private void OnSlider(ForestOption option, float value)
    {
        // The view writing the value back after a refresh must not count as a change.
        if (MathF.Abs(option.Clamp(value) - _world.Value(option)) < 1e-5f)
            return;
        Set(option, value);
    }

    private void OnCameraModeChanged()
    {
        _state = StateOf(_cameras);
        _changed = true;
    }

    private void Refresh()
    {
        _changed = false;
        RefreshTexts();
        _state = StateOf(_cameras);
        _model?.DirtyAll();
    }

    private void RefreshTexts()
    {
        for (var i = 0; i < _texts.Length; i++)
        {
            var option = ForestOptions.All[i];
            _texts[i] = option.Describe(_world.Value(option));
        }
    }

    /// <summary>The line under the title: what the view is.</summary>
    public static string StateOf(ForestCameras? cameras) => cameras?.Mode switch
    {
        ForestCameraMode.FlyOver => "Fly-over",
        ForestCameraMode.Shot => $"R{cameras.ShotIndex + 1} · {ShotTitle(ValleyLayout.Shots[cameras.ShotIndex])}",
        ForestCameraMode.FreeFly => "Free camera",
        _ => "Paused",
    };

    /// <summary>A shot's display name: <c>r7-fall-close</c> → "Fall close".</summary>
    public static string ShotTitle(in ReferenceShot shot)
    {
        var name = shot.Name;
        var dash = name.IndexOf('-');
        if (dash >= 0)
            name = name[(dash + 1)..];
        name = name.Replace('-', ' ');
        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private enum PendingAction
    {
        None,
        Close,
        Quit,
        FlyOver,
        FreeFly,
        Player,
        Shot,
    }

    // ── The document ─────────────────────────────────────────────────────────────────────────────────────────

    // Which column of the Graphics page each group takes.
    private static readonly string[] RightColumnGroups = ["Atmosphere", "Sun (lighting bake approximate)", "Camera and post-processing"];

    private static readonly (string Keys, string What)[] KeyReference =
    [
        ("W A S D", "Move"),
        ("Mouse", "Look"),
        ("Shift", "Sprint"),
        ("C", "Crouch"),
        ("Space", "Jump"),
        ("Esc", "Menu"),
        ("F3", "Frame rate"),
        ("F12", "Developer overlay"),
    ];

    /// <summary>The menu's RML: navigation, the four pages (rows from <see cref="ForestOptions.All"/>) and the tooltip.</summary>
    public static string BuildRml()
    {
        var b = new StringBuilder(32 * 1024);
        b.Append("<rml><head><title>Paused</title>")
            .Append("<link type=\"text/rcss\" href=\"/Content/UI/widgets/widgets.rcss\"/>")
            .Append("<link type=\"text/rcss\" href=\"/Content/UI/menu.rcss\"/>")
            .Append("</head><body data-model=\"menu\"><div id=\"scrim\"></div>");

        // Navigation.
        b.Append("<div id=\"nav\"><div class=\"brand\"><div class=\"brand-kicker\">MAINFRAME ENGINE</div>")
            .Append("<div class=\"brand-title\">Forest</div><div class=\"brand-state\">{{state}}</div></div>")
            .Append("<button id=\"resume\" class=\"nav-item resume\" data-event-click=\"resume\">Resume</button>");
        foreach (var page in Pages)
            b.Append("<button id=\"nav-").Append(page).Append("\" class=\"nav-item\" data-class-active=\"page == '").Append(page)
                .Append("'\" data-event-click=\"show('").Append(page).Append("')\">").Append(Title(page)).Append("</button>");
        b.Append("<div class=\"nav-gap\"></div><button id=\"quit\" class=\"nav-item quit\" data-event-click=\"quit\">Quit</button>")
            .Append("<div id=\"nav-foot\"><div><span class=\"key\">Esc</span>Resume</div><div><span class=\"key\">B</span>Back on a gamepad</div></div></div>");

        // Panel.
        b.Append("<div id=\"panel\"><div class=\"panel-head\">");
        foreach (var page in Pages)
            b.Append("<h1 data-class-hidden=\"page != '").Append(page).Append("'\">").Append(Title(page)).Append("</h1>");
        b.Append("<div class=\"head-note\" data-class-hidden=\"page == 'cameras'\">Changes apply at once and are saved when you resume.</div>")
            .Append("<div class=\"head-note\" data-class-hidden=\"page != 'cameras'\">Pick a view. Esc brings you back here.</div>")
            .Append("<button id=\"reset\" class=\"reset\" data-class-hidden=\"page == 'cameras'\" data-event-click=\"reset\">Reset to defaults</button>")
            .Append("</div><div class=\"panel-body\">");

        // Graphics: two columns of groups.
        b.Append("<div class=\"page\" data-class-hidden=\"page != 'graphics'\"><div class=\"columns\"><div class=\"col\">");
        AppendGroups(b, ForestOptions.OnPage(ForestOptions.Graphics).Where(o => Array.IndexOf(RightColumnGroups, o.Group) < 0));
        b.Append("</div><div class=\"col-gap\"></div><div class=\"col\">");
        AppendGroups(b, ForestOptions.OnPage(ForestOptions.Graphics).Where(o => Array.IndexOf(RightColumnGroups, o.Group) >= 0));
        b.Append("</div></div></div>");

        // Audio.
        b.Append("<div class=\"page\" data-class-hidden=\"page != 'audio'\"><div class=\"columns\"><div class=\"col\">");
        AppendGroups(b, ForestOptions.OnPage(ForestOptions.Audio));
        b.Append("<div class=\"note\">Everything you hear is synthesised when the Forest loads: no recordings.</div>")
            .Append("</div><div class=\"col-gap\"></div><div class=\"col\"></div></div></div>");

        // Controls, with the keys for reference.
        b.Append("<div class=\"page\" data-class-hidden=\"page != 'controls'\"><div class=\"columns\"><div class=\"col\">");
        AppendGroups(b, ForestOptions.OnPage(ForestOptions.Controls));
        b.Append("</div><div class=\"col-gap\"></div><div class=\"col\"><div class=\"group\"><div class=\"group-title\">Keys</div>");
        foreach (var (keys, what) in KeyReference)
            b.Append("<div class=\"opt\"><div class=\"opt-label\">").Append(what).Append("</div><div class=\"opt-val keys\">")
                .Append(keys).Append("</div></div>");
        b.Append("</div><div class=\"note\">Gamepad: left stick to move, right stick to look, A to jump, B to crouch, Start for this menu.</div>")
            .Append("</div></div></div>");

        // Cameras.
        b.Append("<div class=\"page\" data-class-hidden=\"page != 'cameras'\"><div class=\"heroes\">")
            .Append("<button id=\"cam-player\" class=\"hero\" data-class-on=\"cam == 0\" data-event-click=\"player\"><div class=\"hero-title\">Return to player</div>")
            .Append("<div class=\"hero-sub\">Walk the valley.</div></button>")
            .Append("<button id=\"cam-flyover\" class=\"hero\" data-class-on=\"cam == 1\" data-event-click=\"flyover\"><div class=\"hero-title\">Fly-over</div>")
            .Append("<div class=\"hero-sub\">A slow loop over the glade, the fall, the pines and the pond.</div></button>")
            .Append("<button id=\"cam-free\" class=\"hero last\" data-class-on=\"cam == 3\" data-event-click=\"freefly\"><div class=\"hero-title\">Free camera</div>")
            .Append("<div class=\"hero-sub\">Fly anywhere: WASD, Space up, C down, Shift faster.</div></button></div>")
            .Append("<div class=\"group-title\">Reference shots</div><div class=\"shots\">");
        for (var i = 0; i < ValleyLayout.Shots.Length; i++)
        {
            var shot = ValleyLayout.Shots[i];
            var n = i.ToString(CultureInfo.InvariantCulture);
            b.Append("<button id=\"shot-").Append(i + 1).Append("\" class=\"shot\" data-class-on=\"shot == ").Append(n)
                .Append("\" data-event-click=\"goshot(").Append(n).Append(")\"><img src=\"/Content/UI/shots/").Append(shot.Name)
                .Append(".jpg\"/><div class=\"shot-caption\"><span class=\"shot-tag\">R").Append(i + 1).Append("</span><span class=\"shot-name\">")
                .Append(Escape(ShotTitle(shot))).Append("</span></div></button>");
        }

        b.Append("</div><div class=\"note\">In a shot, A and D (or left and right) step through the shots. ")
            .Append("The photo shots R4 and R5 add their own depth of field.</div></div>");

        b.Append("</div></div><div id=\"tooltip\"></div></body></rml>");
        return b.ToString();
    }

    private static string Title(string page) => page switch
    {
        ForestOptions.Graphics => "Graphics",
        ForestOptions.Audio => "Audio",
        ForestOptions.Controls => "Controls",
        _ => "Cameras",
    };

    private static void AppendGroups(StringBuilder b, IEnumerable<ForestOption> options)
    {
        string? group = null;
        foreach (var option in options)
        {
            if (option.Group != group)
            {
                if (group is not null)
                    b.Append("</div>");
                group = option.Group;
                b.Append("<div class=\"group\"><div class=\"group-title\">").Append(Escape(group)).Append("</div>");
            }

            AppendRow(b, option);
        }

        if (group is not null)
            b.Append("</div>");
    }

    private static void AppendRow(StringBuilder b, ForestOption option)
    {
        var key = option.Key;
        b.Append("<div id=\"opt-").Append(key).Append("\" class=\"opt\"");
        if (option.Hint is { } hint)
            b.Append(" title=\"").Append(Escape(hint)).Append('"');
        if (option.DimWhen is { } dim)
            b.Append(" data-class-dim=\"").Append(Escape(dim)).Append('"');
        b.Append("><div class=\"opt-label\">").Append(Escape(option.Label)).Append("</div><div class=\"opt-ctl\">");
        switch (option.Kind)
        {
            case ForestOptionKind.Slider:
                b.Append("<input type=\"range\" min=\"").Append(F(option.Min)).Append("\" max=\"").Append(F(option.Max))
                    .Append("\" step=\"").Append(F(option.Step > 0f ? option.Step : (option.Max - option.Min) / 100f))
                    .Append("\" data-value=\"").Append(key).Append("\"/><div class=\"opt-val\">{{").Append(key).Append("_t}}</div>");
                break;
            case ForestOptionKind.Toggle:
                b.Append("<div class=\"seg\"><button data-class-on=\"!").Append(key).Append("\" data-event-click=\"pick('").Append(key)
                    .Append("', 0)\">Off</button><button data-class-on=\"").Append(key).Append("\" data-event-click=\"pick('").Append(key)
                    .Append("', 1)\">On</button></div>");
                break;
            default:
                b.Append("<div class=\"seg\">");
                for (var c = 0; c < option.Choices.Length; c++)
                    b.Append("<button data-class-on=\"").Append(key).Append(" == ").Append(c).Append("\" data-event-click=\"pick('")
                        .Append(key).Append("', ").Append(c).Append(")\">").Append(Escape(option.Choices[c])).Append("</button>");
                b.Append("</div>");
                break;
        }

        b.Append("</div></div>");
    }

    private static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
}
