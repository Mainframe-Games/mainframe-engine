using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// The engine's drop-in settings menu (<see cref="SettingsMenu"/>) on its Controls tab, with the "Interact" key slot waiting
/// for a key (the capture state). In-memory <see cref="UserSettings"/> over a small project input map; the layer is laid out
/// at 1 dp = 1 px from a 960×600 reference, so the capture does not depend on the display's scale. Self-check: the tab is
/// Controls and the menu is capturing on <see cref="CheckFrame"/>.
/// </summary>
public sealed class SettingsMenuScene(HostOptions host) : RenderTestGame(host)
{
    public const uint ControlsFrame = 4, RebindFrame = 8, CheckFrame = 30;

    private SettingsMenu _menu = null!;

    protected override void LoadScene()
    {
        var project = new ProjectSettings { Name = "Render Test" };
        project.Input
            .Bind("move_forward", "key:W", "key:Up", "axis:LeftY-")
            .Bind("jump", "key:Space", "pad:A")
            .Bind("interact", "key:E", "pad:X")
            .Bind("drop", "mouse:Left", "axis:RightTrigger+")
            .Bind("pause", "key:Escape", "pad:Start");
        project.Input.GetAction("move_forward")!.Label = "Move forward";
        project.Input.GetAction("jump")!.Label = "Jump";
        project.Input.GetAction("interact")!.Label = "Interact";
        project.Input.GetAction("drop")!.Label = "Drop crate";
        project.Input.GetAction("pause")!.Label = "Pause";
        project.Input.GetAction("pause")!.Rebindable = false;
        Tree.Input.Map = project.Input;

        _menu = SettingsMenu.Open(Tree, new SettingsMenuOptions { Settings = new UserSettings(new ConfigFile(), project) });
        var layer = _menu.Layer!;
        layer.ScaleMode = UiScaleMode.ReferenceResolution;
        layer.ReferenceResolution = new Vector2(960, 600);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        switch (gameTime.FrameCount)
        {
            case ControlsFrame:
                _menu.ShowTab("controls");
                break;
            case RebindFrame:
                // The third row's first key slot: Interact.
                var bindings = _menu.Document.AsElement().QuerySelectorAll("button.binding");
                if (bindings.Length < 15)
                    Fail($"Expected 15 binding buttons (5 actions × 3 slots), found {bindings.Length}.");
                else
                    bindings[6].Click();
                break;
            case CheckFrame:
                if (_menu.Tab != "controls")
                    Fail($"The menu shows the '{_menu.Tab}' tab, expected 'controls'.");
                if (!_menu.IsCapturing)
                    Fail("The menu is not waiting for a key.");
                break;
        }
    }

    protected override void DisposeScene()
    {
    }
}
