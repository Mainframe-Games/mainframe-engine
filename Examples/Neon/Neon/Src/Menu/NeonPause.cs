using MainframeEngine;

namespace Neon;

/// <summary>
/// The pause menu: <c>pause</c> (Esc, gamepad Start) opens the engine's drop-in <see cref="SettingsMenu"/> (audio
/// volumes per bus, key rebinding, display), which pauses the game until it closes (Esc again); the mouse is captured
/// again then. Not while seated at the computer: there Esc stands up (<see cref="ComputerSeat"/>).
/// </summary>
public sealed class NeonPause : Node
{
    /// <summary>The buses the menu shows, with their labels (Bathroom, SFX and UI are internal).</summary>
    public static readonly Dictionary<string, string> BusLabels = new(StringComparer.Ordinal)
    {
        ["Music"] = "Music",
        ["Ambience"] = "City",
        ["Rain"] = "Rain",
        ["Foley"] = "Effects",
        ["Tv"] = "TV",
    };

    private FirstPersonController? _player;

    /// <summary>Whether a <c>pause</c> press opens the menu: not while the body is locked (seated or standing up).</summary>
    public static bool WantsMenu(bool pausePressed, FirstPersonController? player) =>
        pausePressed && player is not { MovementLocked: true };

    protected override void OnProcess(in GameTime gameTime)
    {
        if (Tree is not { } tree || tree.EditMode || tree.Servers.Get<UiServer>() is null)
            return;
        _player ??= tree.CurrentScene?.FindChildren<FirstPersonController>(owned: false).FirstOrDefault();
        if (!WantsMenu(tree.Input.IsActionJustPressed("pause"), _player) || SettingsMenu.Find(tree) is not null)
            return;
        var labels = new Dictionary<string, string>(BusLabels, StringComparer.Ordinal); // Neon is English only
        var menu = SettingsMenu.Open(tree, new SettingsMenuOptions
        {
            PauseTree = true,
            HiddenBuses = ["Bathroom", "SFX", "UI"],
            BusLabels = labels,
        });
        menu.Closed += () =>
        {
            if (_player is { CaptureMouse: true })
                tree.Input.MouseMode = MouseMode.Captured;
        };
    }
}
