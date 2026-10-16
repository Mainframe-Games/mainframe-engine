using MainframeEngine.Tests.Audio;
using MainframeEngine.Tests.Saves;
using Silk.NET.Input;

namespace MainframeEngine.Tests.UI;

/// <summary>The drop-in settings menu headless: every tab binds, rebinding captures keys, tabs switch, closing saves.</summary>
[Collection(nameof(SerialRmlUi))]
public sealed class SettingsMenuTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("mf-settings-menu").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string SettingsPath => Path.Combine(_folder, UserSettings.FileName);

    private sealed class Fixture : IDisposable
    {
        public Fixture(string settingsPath)
        {
            Ui = new UiTestTree();
            Audio = AudioTestUtil.CreateServer(Ui.Tree);
            Project = UserSettingsTests.Project();
            Ui.Tree.Input.Map = Project.Input;
            Settings = UserSettings.Load(settingsPath, Project);
            Settings.Apply(Ui.Tree);
        }

        public UiTestTree Ui { get; }
        public AudioServer Audio { get; }
        public ProjectSettings Project { get; }
        public UserSettings Settings { get; }

        public SettingsMenu Open(SettingsMenuOptions? options = null)
        {
            var menu = SettingsMenu.Open(Ui.Tree, options ?? new SettingsMenuOptions
            {
                Settings = Settings,
                PauseTree = true,
                HiddenBuses = ["Voice"],
                BusLabels = { ["SFX"] = "Sound effects" },
            });
            Ui.Tick(2);
            return menu;
        }

        public static string Text(SettingsMenu menu) => menu.QuerySelector("#settings")!.InnerRml;

        public void Dispose()
        {
            Audio.Dispose();
            Ui.Dispose();
        }
    }

    [Fact]
    public void OpensOnceAndBindsEveryTab()
    {
        using var f = new Fixture(SettingsPath);
        var menu = f.Open();

        Assert.True(menu.IsLoaded);
        Assert.True(menu.Modal);
        Assert.True(f.Ui.Tree.Paused);
        Assert.Same(menu, SettingsMenu.Open(f.Ui.Tree)); // one menu per tree
        Assert.Same(menu, SettingsMenu.Find(f.Ui.Tree));

        // Audio: one row per bus except the hidden one, with the game's label.
        Assert.Equal("audio", menu.Tab);
        var audio = Fixture.Text(menu);
        Assert.Contains("Master", audio, StringComparison.Ordinal);
        Assert.Contains("Sound effects", audio, StringComparison.Ordinal);
        Assert.DoesNotContain("Voice", audio, StringComparison.Ordinal);
        Assert.Contains("100%", audio, StringComparison.Ordinal);

        // Controls: the project's actions with their bindings by device class.
        menu.ShowTab("controls");
        f.Ui.Tick();
        var controls = Fixture.Text(menu);
        Assert.Contains("jump", controls, StringComparison.Ordinal);
        Assert.Contains("Space", controls, StringComparison.Ordinal);
        Assert.Equal(6, menu.Document.AsElement().QuerySelectorAll("button.binding").Length); // 2 actions × 3 slots

        // Display: the rows exist, and nothing changed just by opening it (selects write their value back on load).
        menu.ShowTab("display");
        f.Ui.Tick();
        var display = Fixture.Text(menu);
        Assert.Contains("VSync", display, StringComparison.Ordinal);
        Assert.Contains("Shadows", display, StringComparison.Ordinal);
        Assert.False(menu.RevertPending);
        Assert.False(f.Settings.Config.HasSection("display"));
        Assert.False(f.Settings.Config.HasSection("locale"));
    }

    [Fact]
    public void RebindingCapturesAKeyAndStoresTheOverride()
    {
        using var f = new Fixture(SettingsPath);
        var menu = f.Open();
        menu.ShowTab("controls");
        f.Ui.Tick();

        menu.Document.AsElement().QuerySelectorAll("button.binding")[0].Click(); // jump, first key slot
        f.Ui.Tick(2);
        Assert.True(menu.IsCapturing);
        f.Ui.Key(Key.F, pressed: true);
        f.Ui.Key(Key.F, pressed: false);
        f.Ui.Tick();

        Assert.False(menu.IsCapturing);
        Assert.Equal(["key:F", "pad:A"], f.Ui.Tree.Input.Map.GetAction("jump")!.Bindings.Select(b => b.ToString()));
        Assert.True(f.Settings.HasInputOverride("jump"));
        Assert.Contains(">F<", Fixture.Text(menu), StringComparison.Ordinal);

        menu.Close();
        f.Ui.Tick();
        Assert.True(File.Exists(SettingsPath)); // something changed: saved on close
        Assert.Contains("key:F", File.ReadAllText(SettingsPath), StringComparison.Ordinal);

        // Reset controls restores the project's bindings.
        f.Settings.Reset(UserSettingsSection.Input);
        Assert.Equal(["key:Space", "pad:A"], f.Ui.Tree.Input.Map.GetAction("jump")!.Bindings.Select(b => b.ToString()));
    }

    [Fact]
    public void BumpersSwitchTabsAndEscapeClosesWithoutSavingWhenNothingChanged()
    {
        using var f = new Fixture(SettingsPath);
        var closed = 0;
        var menu = f.Open();
        menu.Closed += () => closed++;

        f.Ui.Pad(ButtonName.RightBumper, true);
        Assert.Equal("controls", menu.Tab);
        f.Ui.Pad(ButtonName.RightBumper, true);
        Assert.Equal("display", menu.Tab);
        f.Ui.Pad(ButtonName.RightBumper, true);
        Assert.Equal("audio", menu.Tab); // wraps
        f.Ui.Pad(ButtonName.LeftBumper, true);
        Assert.Equal("display", menu.Tab);

        f.Ui.Key(Key.Escape, pressed: true);
        f.Ui.Tick();
        Assert.Equal(1, closed);
        Assert.Null(SettingsMenu.Find(f.Ui.Tree));
        Assert.False(f.Ui.Tree.Paused);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void MuteAppliesLiveAndClosingSaves()
    {
        using var f = new Fixture(SettingsPath);
        var menu = f.Open(new SettingsMenuOptions { Settings = f.Settings, Tabs = SettingsTabs.Audio });

        menu.Document.AsElement().QuerySelectorAll("button.mute")[0].Click(); // Master
        f.Ui.Tick();
        Assert.True(f.Settings.IsBusMuted("Master"));
        Assert.True(f.Audio.Master.Mute);
        Assert.True(menu.Document.AsElement().QuerySelectorAll("button.mute")[0].IsClassSet("on"));

        menu.Close();
        f.Ui.Tick();
        Assert.Contains("Master", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
        Assert.False(f.Ui.Tree.Paused); // PauseTree was off
    }

    [Fact]
    public void OnlyTheRequestedTabsShow()
    {
        using var f = new Fixture(SettingsPath);
        var menu = f.Open(new SettingsMenuOptions { Settings = f.Settings, Tabs = SettingsTabs.Controls | SettingsTabs.Display });

        Assert.Equal("controls", menu.Tab);
        menu.ShowTab("audio"); // not shown: ignored
        Assert.Equal("controls", menu.Tab);
    }
}
