using MainframeEngine;
using MainframeEngine.Localization;
using Silk.NET.Input;

namespace Demo;

/// <summary>
/// Scripted QA for saves and settings (<c>just qa-settings</c>, <c>build/qa-demo-settings.sh</c>), started with the Demo's
/// own argument <c>++ --qa settings|check|saves</c>. Each run drives the real game through its input path and quits with
/// 0 (pass) or 1 (fail, the reasons logged as errors):
/// <list type="bullet">
/// <item><c>settings</c>: F10 opens the menu, the "rain" key is rebound to F by pressing it, volumes, mute, VSync, max FPS,
/// UI scale and language change, Escape closes the menu (which saves).</item>
/// <item><c>check</c> (a new process): every value came back from <c>settings.json</c> before the first frame.</item>
/// <item><c>saves</c> (Physics 3D): two saves, the newest file damaged as a crash mid-write would, then a load recovers the
/// previous save and keeps the damaged file.</item>
/// </list>
/// </summary>
public sealed class DemoQa : Node
{
    private readonly string _mode;
    private readonly List<string> _failures = [];
    private Task<SaveResult>? _save;
    private int _frame;
    private int _step;

    public DemoQa() : this("") { }

    public DemoQa(string mode)
    {
        _mode = mode;
        ProcessMode = ProcessMode.Always;
    }

    /// <summary>The QA mode from <see cref="GameHost.UserArgs"/> (<c>++ --qa mode</c>), or null.</summary>
    public static string? ModeFromArgs()
    {
        var args = GameHost.UserArgs;
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--qa")
                return args[i + 1];
        }

        return null;
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        _frame++;
        if (_frame < 30 || Tree is not { } tree)
            return; // let the start scene and the UI settle
        var done = _mode switch
        {
            "settings" => Settings(tree),
            "check" => Check(tree),
            "saves" => Saves(tree),
            _ => Fail($"unknown QA mode '{_mode}'"),
        };
        if (!done)
            return;
        foreach (var failure in _failures)
            Log.Error($"[QA] {failure}");
        Log.Info($"[QA] {_mode}: {(_failures.Count == 0 ? "PASS" : $"FAIL ({_failures.Count})")}");
        tree.Quit(_failures.Count == 0 ? 0 : 1);
        _step = int.MaxValue;
    }

    private bool Settings(SceneTree tree)
    {
        switch (_step++)
        {
            case 0:
                Press(tree, Key.F10);
                return false;
            case 5:
                if (SettingsMenu.Find(tree) is not { } menu)
                    return Fail("F10 did not open the settings menu");
                menu.ShowTab("controls");
                return false;
            case 10:
                {
                    var index = tree.Input.Map.Actions.ToList().FindIndex(a => a.Name == "rain");
                    var buttons = SettingsMenu.Find(tree)!.Document.AsElement().QuerySelectorAll("button.binding");
                    if (index < 0 || buttons.Length <= index * 3)
                        return Fail("no binding button for 'rain'");
                    buttons[index * 3].Click(); // the first key slot
                    return false;
                }
            case 15:
                Press(tree, Key.F);
                return false;
            case 20:
                {
                    var settings = UserSettings.Current!;
                    if (tree.Input.Map.GetBinding("rain", InputDeviceClass.KeyboardMouse, 0).ToString() != "key:F")
                        Fail("pressing F did not rebind 'rain'");
                    settings.SetBusVolume("Music", 0.5f);
                    settings.SetBusMuted("Voice", true);
                    settings.VSync = false;
                    settings.MaxFps = 120;
                    settings.UiScale = 1.25f;
                    settings.Language = "es";
                    return false;
                }
            case 30:
                Press(tree, Key.Escape); // closes the menu, which saves
                return false;
            case 40:
                if (SettingsMenu.Find(tree) is not null)
                    Fail("Escape did not close the settings menu");
                if (UserSettings.Current?.FilePath is not { } path || !File.Exists(path))
                    Fail("closing the menu did not write settings.json");
                return true;
            default:
                return false;
        }
    }

    private bool Check(SceneTree tree)
    {
        var settings = UserSettings.Current;
        if (settings is null)
            return Fail("no UserSettings loaded");
        Expect(tree.Input.Map.GetBinding("rain", InputDeviceClass.KeyboardMouse, 0).ToString(), "key:F", "rain binding");
        Expect(settings.GetBusVolume("Music"), 0.5f, "Music volume");
        Expect(tree.Servers.Get<AudioServer>()?.GetBus("Music")?.UserVolume ?? 0.5f, 0.5f, "Music bus UserVolume");
        Expect(settings.IsBusMuted("Voice"), true, "Voice muted");
        Expect(settings.VSync, false, "VSync");
        Expect(settings.MaxFps, 120, "max FPS");
        Expect(settings.UiScale, 1.25f, "UI scale");
        Expect(tree.Servers.Get<UiServer>()?.UserScale ?? 1.25f, 1.25f, "UiServer.UserScale");
        Expect(Tr.CurrentLocale, "es", "language");
        return true;
    }

    private bool Saves(SceneTree tree)
    {
        var panel = tree.CurrentScene?.GetNodeOrNull<Physics3DPanel>("Ui/Panel");
        if (panel is null)
            return Fail("run the saves QA on the Physics 3D scene");
        switch (_step)
        {
            case 0:
            case 1:
                if (_save is null)
                {
                    panel.SelectedSlot = 0;
                    _save = panel.SaveAsync();
                    return false;
                }

                if (!_save.IsCompleted)
                    return false;
                if (_save.Result.Status != SaveStatus.Ok)
                    return Fail($"save {_step + 1} failed: {_save.Result.Error}");
                _save = null;
                _step++;
                return false;
            case 2:
                {
                    // A crash mid-write: the newest file is cut short.
                    var file = GameHost.UserDataPath("saves/" + Physics3DPanel.SlotNames[0] + SaveServer.FileExtension);
                    var bytes = File.ReadAllBytes(file);
                    File.WriteAllBytes(file, bytes[..(bytes.Length / 2)]);
                    Expect(panel.Load(), LoadStatus.RecoveredFromBackup, "load after damage");
                    if (Directory.GetFiles(Path.GetDirectoryName(file)!, "*.corrupt-*").Length == 0)
                        Fail("the damaged file was not kept as .corrupt-*");
                    Expect(panel.Load(), LoadStatus.Ok, "load after recovery");
                    return true;
                }
            default:
                return false;
        }
    }

    private static void Press(SceneTree tree, Key key)
    {
        tree.PushInput(new InputEventKey { Key = key, Pressed = true });
        tree.PushInput(new InputEventKey { Key = key, Pressed = false });
    }

    private void Expect<T>(T actual, T expected, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            _failures.Add($"{what}: expected {expected}, got {actual}");
    }

    private bool Fail(string message)
    {
        _failures.Add(message);
        return true;
    }
}
