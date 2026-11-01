using System.Globalization;
using System.Text;
using MainframeEngine;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;

namespace Neon;

/// <summary>
/// The pause menu: <c>pause</c> (Escape, gamepad Start) opens it over the running city and closes it again, as the Forest's
/// does. Pages: <b>Display</b>, <b>Graphics</b>, <b>Camera</b>, <b>Audio</b> and <b>Controls</b>, every
/// <see cref="NeonOptions"/> row applied live and saved to the player's settings when the menu closes, plus Resume and Quit.
/// Key rebinding is the engine's <see cref="SettingsMenu"/> in Neon's colours.
/// </summary>
/// <remarks>
/// The document is generated from <see cref="NeonOptions.All"/> (<see cref="BuildRml"/>) and styled by
/// <c>Assets/UI/menu.rcss</c>. While it is open the player is disabled and the mouse is free; the rain, the traffic and the
/// holograms go on behind it, so a change shows at once. It sees input before the UI so Escape and Start toggle it even while
/// a slider has focus; gamepad B closes it. Closed, its layer is hidden and it does nothing per frame but check two flags.
/// </remarks>
public sealed class NeonMenu : UiDocument
{
    /// <summary>The menu's pages, in navigation order.</summary>
    public static readonly string[] Pages = [NeonOptions.Display, NeonOptions.Graphics, NeonOptions.Camera, NeonOptions.Audio, NeonOptions.Controls];

    /// <summary>The menu's UI layer: above the game's, below the frame-rate readout (100) and the key rebinding (90).</summary>
    public const int LayerOrder = 50;

    /// <summary>The key rebinding document (<see cref="SettingsMenuOptions.Source"/>).</summary>
    public const string KeysSource = "Assets/UI/keys.rml";

    // Which column of the Graphics page each group takes.
    private static readonly string[] RightColumnGroups = ["City", "Weather"];

    private static readonly (string Keys, string What)[] KeyReference =
    [
        ("W A S D", "Move"),
        ("Mouse", "Look"),
        ("Shift", "Sprint"),
        ("C", "Crouch"),
        ("Space", "Jump"),
        ("E", "Use"),
        ("Esc", "Menu"),
        ("F2", "Photo"),
        ("F3", "Frame rate"),
        ("F12", "Developer overlay"),
    ];

    private NeonWorld _world = new();
    private string[] _texts = [];
    private RmlDataModel? _model;
    private bool _changed;
    private bool _unsaved;
    private bool _pauseHeld;
    private PendingAction _pending;

    public NeonMenu()
    {
        Name = "Menu";
        Visible = false;
        InputBeforeUi = true;
    }

    /// <summary>The page shown (<see cref="Pages"/>).</summary>
    public string Page { get; private set; } = NeonOptions.Display;

    public bool IsOpen { get; private set; }

    /// <summary>What the options act on.</summary>
    public NeonWorld World
    {
        get => _world;
        set => _world = value;
    }

    /// <summary>Whether closing writes the settings file (off in tests and scripted runs).</summary>
    public bool Persist { get; set; }

    /// <summary>How many times the settings were written (tests).</summary>
    public int Saves { get; private set; }

    /// <summary>Raised when the menu closes.</summary>
    public event Action? Closed;

    /// <summary>
    /// Adds the menu to <paramref name="parent"/> on its own hidden UI layer (<see cref="LayerOrder"/>), acting on
    /// <paramref name="world"/>. The player stops releasing the mouse on <c>pause</c> itself: the menu does.
    /// </summary>
    public static NeonMenu Attach(Node parent, NeonWorld world, bool persist)
    {
        var layer = new UiLayer { Name = "MenuLayer", Layer = LayerOrder, Visible = false };
        var menu = new NeonMenu { _world = world, Persist = persist, Rml = BuildRml() };
        layer.AddChild(menu);
        parent.AddChild(layer);
        if (world.Player is { } player)
            player.ReleaseMouseOnPause = false;
        return menu;
    }

    protected override void OnReady()
    {
        _texts = new string[NeonOptions.All.Length];
        RefreshTexts();
        var model = _model = CreateDataModel("menu");
        model.Bind("page", this, static m => m.Page);
        for (var i = 0; i < NeonOptions.All.Length; i++)
        {
            var option = NeonOptions.All[i];
            var index = i;
            switch (option.Kind)
            {
                case NeonOptionKind.Slider:
                    model.Bind(option.Key, () => _world.Value(option), v => OnSlider(option, v));
                    break;
                case NeonOptionKind.Toggle:
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
                if (e.ArgumentCount >= 2 && NeonOptions.Find(e.GetArgument(0).GetString()) is { } option)
                    Set(option, e.GetArgument(1).GetSingle());
            })
            .Event("step", e =>
            {
                if (e.ArgumentCount >= 2 && NeonOptions.Find(e.GetArgument(0).GetString()) is { } option)
                    Set(option, _world.Value(option) + e.GetArgument(1).GetSingle());
            })
            .Event("reset", () => ResetPage())
            .Event("resume", () => _pending = PendingAction.Close)
            .Event("keys", () => _pending = PendingAction.Keys)
            .Event("quit", () => _pending = PendingAction.Quit);
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
            if (_world.Player is { } player)
                player.ProcessMode = ProcessMode.Disabled;
            if (Tree is { } tree)
                tree.Input.MouseMode = MouseMode.Visible;
        }

        _changed = true; // refresh every value: the F3 readout and the window may have changed
        Refresh();
    }

    /// <summary>Closes the menu, gives control back to the player and saves changed settings.</summary>
    public void Close()
    {
        if (!IsOpen)
            return;
        IsOpen = false;
        Visible = false;
        Modal = false;
        if (Layer is { } layer)
            layer.Visible = false;
        if (_world.Player is { } player)
        {
            player.ProcessMode = ProcessMode.Inherit;
            if (player.CaptureMouse && Tree is { } tree)
                tree.Input.MouseMode = MouseMode.Captured;
        }

        Save();
        Closed?.Invoke();
    }

    /// <summary>Opens the key rebinding over the menu (the engine's <see cref="SettingsMenu"/> in Neon's colours).</summary>
    public void OpenKeys()
    {
        if (Tree is { } tree)
            SettingsMenu.Open(tree, new SettingsMenuOptions { Tabs = SettingsTabs.Controls, Source = KeysSource, Settings = _world.User });
    }

    /// <summary>Opens the menu, or closes it.</summary>
    public void Toggle()
    {
        if (IsOpen)
            Close();
        else
            Open();
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
    public void Set(NeonOption option, float value)
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

    /// <summary>Writes the settings if they changed since the last save.</summary>
    public void Save()
    {
        if (!_unsaved)
            return;
        _unsaved = false;
        if (!Persist || _world.User is not { FilePath: not null } user)
            return;
        try
        {
            user.Save();
            Saves++;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Neon] Could not save the settings: {e.Message}");
        }
    }

    protected override void OnInput(InputEvent inputEvent)
    {
        var map = Tree?.Input.Map;
        if (Tree is { } tree && SettingsMenu.Find(tree) is not null)
            return; // the key rebinding is on top and takes Escape for itself
        if (inputEvent.IsActionPressed("pause", map))
        {
            var consumed = IsOpen;
            if (!_pauseHeld) // key repeat
            {
                _pauseHeld = true;
                if (IsOpen || NeonPause.WantsMenu(true, _world.Player))
                    Toggle();
            }

            if (consumed || IsOpen)
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

    // Actions from click handlers run here, outside RmlUi's event dispatch (they hide the layer).
    private void RunPending()
    {
        var action = _pending;
        _pending = PendingAction.None;
        switch (action)
        {
            case PendingAction.Close:
                Close();
                break;
            case PendingAction.Keys:
                OpenKeys();
                break;
            case PendingAction.Quit:
                Save();
                Tree?.Quit(0);
                break;
        }
    }

    private void OnSlider(NeonOption option, float value)
    {
        // The view writing the value back after a refresh must not count as a change.
        if (MathF.Abs(option.Clamp(_world, value) - _world.Value(option)) < 1e-5f)
            return;
        Set(option, value);
    }

    private void Refresh()
    {
        _changed = false;
        RefreshTexts();
        _model?.DirtyAll();
    }

    private void RefreshTexts()
    {
        for (var i = 0; i < _texts.Length; i++)
        {
            var option = NeonOptions.All[i];
            _texts[i] = option.Describe(_world, _world.Value(option));
        }
    }

    private enum PendingAction
    {
        None,
        Close,
        Keys,
        Quit,
    }

    // ── The document ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The menu's RML: navigation, the five pages (rows from <see cref="NeonOptions.All"/>) and the tooltip.</summary>
    public static string BuildRml()
    {
        var b = new StringBuilder(48 * 1024);
        b.Append("<rml><head><title>Paused</title>")
            .Append("<link type=\"text/rcss\" href=\"/Assets/UI/widgets/widgets.rcss\"/>")
            .Append("<link type=\"text/rcss\" href=\"/Assets/UI/menu.rcss\"/>")
            .Append("</head><body data-model=\"menu\"><div id=\"scrim\"></div><div id=\"scanlines\"></div>");

        // Navigation.
        b.Append("<div id=\"nav\"><div class=\"brand\"><div class=\"brand-kicker\">// SYSTEM CONFIG</div>")
            .Append("<div class=\"brand-title\">NEON</div><div class=\"brand-state\"><span class=\"pulse\"></span>PAUSED // 20F</div></div>")
            .Append("<button id=\"resume\" class=\"nav-item resume\" data-event-click=\"resume\"><span class=\"nav-i\">&gt;&gt;</span>Resume</button>");
        for (var i = 0; i < Pages.Length; i++)
        {
            var page = Pages[i];
            b.Append("<button id=\"nav-").Append(page).Append("\" class=\"nav-item\" data-class-active=\"page == '").Append(page)
                .Append("'\" data-event-click=\"show('").Append(page).Append("')\"><span class=\"nav-i\">0").Append(i + 1).Append("</span>")
                .Append(Title(page)).Append("</button>");
        }

        b.Append("<div class=\"nav-gap\"></div><button id=\"quit\" class=\"nav-item quit\" data-event-click=\"quit\"><span class=\"nav-i\">XX</span>Quit</button>")
            .Append("<div id=\"nav-foot\"><div><span class=\"key\">ESC</span>Resume</div><div><span class=\"key\">B</span>Back on a gamepad</div></div></div>");

        // Panel.
        b.Append("<div id=\"panel\"><div class=\"corner tl\"></div><div class=\"corner tr\"></div><div class=\"corner bl\"></div><div class=\"corner br\"></div>")
            .Append("<div class=\"panel-head\">");
        foreach (var page in Pages)
            b.Append("<h1 data-class-hidden=\"page != '").Append(page).Append("'\">").Append(Title(page)).Append("</h1>");
        b.Append("<div class=\"head-note\">Changes apply at once and are saved when you resume.</div>")
            .Append("<button id=\"reset\" class=\"reset\" data-event-click=\"reset\">Defaults</button>")
            .Append("</div><div class=\"panel-body\">");

        AppendPage(b, NeonOptions.Display, static o => o.Group == "Image");
        AppendPage(b, NeonOptions.Graphics, static o => Array.IndexOf(RightColumnGroups, o.Group) >= 0);
        AppendPage(b, NeonOptions.Camera, static o => o.Group == "Lens");

        // Audio.
        b.Append("<div class=\"page\" data-class-hidden=\"page != 'audio'\"><div class=\"columns\"><div class=\"col\">");
        AppendGroups(b, NeonOptions.OnPage(NeonOptions.Audio));
        b.Append("<div class=\"note\">Everything you hear is synthesised when Neon loads: no recordings.</div>")
            .Append("</div><div class=\"col-gap\"></div><div class=\"col\"></div></div></div>");

        // Controls, with the keys for reference.
        b.Append("<div class=\"page\" data-class-hidden=\"page != 'controls'\"><div class=\"columns\"><div class=\"col\">");
        AppendGroups(b, NeonOptions.OnPage(NeonOptions.Controls));
        b.Append("<button id=\"rebind\" class=\"action\" data-event-click=\"keys\">Rebind keys and buttons</button>")
            .Append("</div><div class=\"col-gap\"></div><div class=\"col\"><div class=\"group\"><div class=\"group-title\">Keys</div>");
        foreach (var (keys, what) in KeyReference)
            b.Append("<div class=\"opt\"><div class=\"opt-label\">").Append(what).Append("</div><div class=\"opt-val keys\">").Append(keys).Append("</div></div>");
        b.Append("</div><div class=\"note\">Gamepad: left stick to move, right stick to look, A to jump, B to crouch, X to use, Start for this menu.</div>")
            .Append("</div></div></div>");

        b.Append("</div></div><div id=\"tooltip\"></div></body></rml>");
        return b.ToString();
    }

    private static void AppendPage(StringBuilder b, string page, Func<NeonOption, bool> rightColumn)
    {
        b.Append("<div class=\"page\" data-class-hidden=\"page != '").Append(page).Append("'\"><div class=\"columns\"><div class=\"col\">");
        AppendGroups(b, NeonOptions.OnPage(page).Where(o => !rightColumn(o)));
        b.Append("</div><div class=\"col-gap\"></div><div class=\"col\">");
        AppendGroups(b, NeonOptions.OnPage(page).Where(rightColumn));
        b.Append("</div></div></div>");
    }

    private static string Title(string page) => page switch
    {
        NeonOptions.Display => "Display",
        NeonOptions.Graphics => "Graphics",
        NeonOptions.Camera => "Camera",
        NeonOptions.Audio => "Audio",
        _ => "Controls",
    };

    private static void AppendGroups(StringBuilder b, IEnumerable<NeonOption> options)
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

    private static void AppendRow(StringBuilder b, NeonOption option)
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
            case NeonOptionKind.Slider:
                b.Append("<input type=\"range\" min=\"").Append(F(option.Min)).Append("\" max=\"").Append(F(option.Max))
                    .Append("\" step=\"").Append(F(option.Step > 0f ? option.Step : (option.Max - option.Min) / 100f))
                    .Append("\" data-value=\"").Append(key).Append("\"/><div class=\"opt-val\">{{").Append(key).Append("_t}}</div>");
                break;
            case NeonOptionKind.Toggle:
                b.Append("<div class=\"seg\"><button data-class-on=\"!").Append(key).Append("\" data-event-click=\"pick('").Append(key)
                    .Append("', 0)\">Off</button><button data-class-on=\"").Append(key).Append("\" data-event-click=\"pick('").Append(key)
                    .Append("', 1)\">On</button></div>");
                break;
            case NeonOptionKind.Stepper:
                b.Append("<div class=\"stepper\"><button data-event-click=\"step('").Append(key).Append("', -1)\">&lt;</button><div class=\"step-val\">{{")
                    .Append(key).Append("_t}}</div><button data-event-click=\"step('").Append(key).Append("', 1)\">&gt;</button></div>");
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
