using System.Globalization;
using MainframeEngine;

namespace Neon;

/// <summary>
/// Sets up the pause menu when the scene is ready: captures what its options act on (<see cref="NeonWorld"/>), applies the
/// player's saved settings and adds the <see cref="NeonMenu"/> and the frame-rate readout. Runs with <c>++ --no-capture</c>,
/// <c>--view</c> or <c>--menu</c> (screenshots, QA) keep the project's look and save nothing; <c>++ --option key=value</c>
/// sets a menu option for one run and <c>++ --menu [page]</c> starts with the menu open. <c>pause</c> does not open the menu
/// while seated at the computer: there Esc stands up (<see cref="Chair"/>).
/// </summary>
public sealed class NeonPause : Node
{
    private int _openIn;
    private string? _openPage;

    /// <summary>The menu, once the scene is ready; null in headless runs.</summary>
    public NeonMenu? Menu { get; private set; }

    /// <summary>Whether a <c>pause</c> press opens the menu: not while the body is locked (seated or standing up).</summary>
    public static bool WantsMenu(bool pausePressed, FirstPersonController? player) =>
        pausePressed && player is not { MovementLocked: true };

    protected override void OnReady()
    {
        if (Tree is not { } tree || tree.EditMode || tree.Servers.Get<UiServer>() is null || Parent is not { } scene)
            return;
        var args = GameHost.UserArgs;
        var play = GameHost.Project is not null && !args.Contains("--no-capture") && !args.Contains("--view") && !args.Contains("--menu");
        var fps = NeonFps.Attach(this);
        fps.Visible = false;
        var screen = tree.Window?.ScreenBounds.Size is { } size ? new Vector2I((int)size.X, (int)size.Y) : (Vector2I?)null;
        var world = NeonWorld.Capture(scene, RendererDisplay.Of(tree), UserSettings.Current, fps, screen);
        if (play)
            world.ApplySaved();
        Menu = NeonMenu.Attach(this, world, play);
        for (var i = 0; i < args.Count - 1; i++) // --option key=value: a menu option for this run (QA; not saved)
        {
            if (args[i] != "--option")
                continue;
            var eq = args[i + 1].IndexOf('=');
            if (eq > 0 && NeonOptions.Find(args[i + 1][..eq]) is { } option &&
                float.TryParse(args[i + 1][(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                Log.Info($"[Neon] --option {option.Key} = {world.Apply(option, value)}");
            else
                Log.Error($"[Neon] --option {args[i + 1]}: expected an option key (NeonOptions) = a number.");
        }

        for (var i = 0; i < args.Count; i++) // --menu [page]: starts with the menu open (QA screenshots)
        {
            if (args[i] == "--menu")
            {
                _openPage = i + 1 < args.Count ? args[i + 1] : NeonOptions.Display;
                _openIn = 20; // once the camera has settled on --view
            }
        }
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (_openIn > 0 && --_openIn == 0)
        {
            Menu?.Open(_openPage == "keys" ? NeonOptions.Controls : _openPage);
            if (_openPage == "keys")
                Menu?.OpenKeys();
        }
    }
}
