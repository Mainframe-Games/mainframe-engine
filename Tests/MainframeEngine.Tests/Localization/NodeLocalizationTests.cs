using System.Text;
using MainframeEngine.Localization;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Localization;

/// <summary>
/// Scene strings: <c>[Export(Translatable = true)]</c> registration, translation on load and re-translation when the
/// locale changes (<see cref="Node.OnLocaleChanged"/>), <see cref="AutoTranslateMode"/>, and the font fallback table.
/// </summary>
[Collection(nameof(LocalizationState))]
public sealed class NodeLocalizationTests : IDisposable
{
    private readonly LocaleFixture _fixture = new();

    public NodeLocalizationTests()
    {
        _fixture.AddCatalog("es", LocaleFixture.OneOther, """
            msgid "Settings"
            msgstr "Ajustes"

            msgid "Back"
            msgstr "Volver"

            msgid "{0} coin"
            msgid_plural "{0} coins"
            msgstr[0] "{0} moneda"
            msgstr[1] "{0} monedas"
            """);
        _fixture.AddCatalog("de", LocaleFixture.OneOther, """
            msgid "Settings"
            msgstr "Einstellungen"
            """);
    }

    public void Dispose() => _fixture.Dispose();

    private const string SceneJson = """
        {
          "format": 1,
          "uid": "scn_00000000c0de",
          "root": {
            "type": "Node", "name": "Menu",
            "children": [
              { "type": "LocalizedLabel", "name": "Title", "props": { "Text": "Settings" } },
              { "type": "Node", "name": "Debug", "props": { "AutoTranslateMode": "Disabled" },
                "children": [ { "type": "LocalizedLabel", "name": "Raw", "props": { "Text": "Settings" } } ] }
            ]
          }
        }
        """;

    [Fact]
    public void TranslatableExportsAreRegistered()
    {
        var label = TypeRegistry.Get(typeof(LocalizedLabel))!;
        Assert.Equal(["Text", "Lines"], label.TranslatableProperties.Select(p => p.Name));
        Assert.True(label.FindProperty("Text")!.Hints.Translatable);
        Assert.False(label.FindProperty("Key")!.Hints.Translatable);
        Assert.Equal(["Text"], TypeRegistry.Get(typeof(DialogueLine))!.TranslatableProperties.Select(p => p.Name));
        Assert.Empty(TypeRegistry.Get(typeof(Node3D))!.TranslatableProperties);
        Assert.Equal(AutoTranslateMode.Inherit, new Node().AutoTranslateMode);
    }

    [Fact]
    public void SceneStringsAreTranslatedOnLoadAndWhenTheLocaleChanges()
    {
        _fixture.Use("es");
        var tree = new SceneTree();
        var treeEvents = 0;
        tree.LocaleChanged += () => treeEvents++;
        try
        {
            var root = PackedScene.Parse(Encoding.UTF8.GetBytes(SceneJson)).Instantiate();
            tree.Root.AddChild(root);
            var title = root.GetNode<LocalizedLabel>("Title");
            var raw = root.GetNode<LocalizedLabel>("Debug/Raw");

            Assert.Equal("Settings", title.Text);       // the stored value stays the msgid
            Assert.Equal("Ajustes", title.Displayed);   // shown translated (OnReady)
            Assert.Equal("Settings", raw.Displayed);    // AutoTranslateMode.Disabled inherited from Debug
            Assert.False(raw.CanAutoTranslate());

            Tr.SetLocale("de");
            Assert.Equal("Einstellungen", title.Displayed);
            Assert.Equal(1, title.LocaleChanges);
            Assert.Equal(1, raw.LocaleChanges);         // notified, still untranslated
            Assert.Equal("Settings", raw.Displayed);
            Assert.Equal(1, treeEvents);

            raw.AutoTranslateMode = AutoTranslateMode.Always; // overrides the parent and re-translates at once
            Assert.Equal("Einstellungen", raw.Displayed);
            Assert.Equal("Einstellungen", raw.Atr("Settings"));

            Tr.SetLocale("fr"); // no catalog: back to the source text
            Assert.Equal("Settings", title.Displayed);

            // Saving keeps the source text, never the translation.
            var saved = Encoding.UTF8.GetString(SceneSaver.ToJson(root, "scn_00000000c0de"));
            Assert.Contains("\"Text\": \"Settings\"", saved, StringComparison.Ordinal);
            Assert.DoesNotContain("Einstellungen", saved, StringComparison.Ordinal);
        }
        finally
        {
            tree.Shutdown();
        }
    }

    [Fact]
    public void LocaleChangeDuringTickIsDeferredToTheEndOfTheFrame()
    {
        _fixture.Use("en");
        var tree = new SceneTree();
        try
        {
            var label = new LocalizedLabel { Text = "Back" };
            var switcher = new LocaleSwitcher(label);
            tree.Root.AddChild(label);
            tree.Root.AddChild(switcher);
            Assert.Equal("Back", label.Displayed);

            tree.Tick(new GameTime { DeltaTime = 1 / 60f });
            Assert.Equal("Back", switcher.SeenDuringProcess); // not re-translated mid-frame
            Assert.Equal("Volver", label.Displayed);           // but by the end of it
        }
        finally
        {
            tree.Shutdown();
        }
    }

    [Fact]
    public void LocaleChangeFromAnotherThreadIsAppliedOnTheTreesNextTick()
    {
        _fixture.Use("en");
        var tree = new SceneTree();
        try
        {
            var label = new LocalizedLabel { Text = "Back" };
            tree.Root.AddChild(label);
            var thread = new Thread(() => Tr.SetLocale("es"));
            thread.Start();
            thread.Join();
            Assert.Equal("Back", label.Displayed);
            tree.Tick(new GameTime { DeltaTime = 1 / 60f });
            Assert.Equal("Volver", label.Displayed);
        }
        finally
        {
            tree.Shutdown();
        }
    }

    [Fact]
    public void AtrNTranslatesPluralsUnlessDisabled()
    {
        _fixture.Use("es");
        var node = new Node();
        Assert.Equal("3 monedas", node.AtrN("{0} coin", "{0} coins", 3));
        node.AutoTranslateMode = AutoTranslateMode.Disabled;
        Assert.Equal("3 coins", node.AtrN("{0} coin", "{0} coins", 3));
        Assert.Equal("1 coin", node.AtrN("{0} coin", "{0} coins", 1));
        Assert.Equal("Settings", node.Atr("Settings"));
        node.Free();
    }

    [Fact]
    public void ShutDownTreesAreNotNotified()
    {
        _fixture.Use("en");
        var tree = new SceneTree();
        var label = new LocalizedLabel { Text = "Back" };
        tree.Root.AddChild(label);
        var events = 0;
        tree.LocaleChanged += () => events++;
        tree.Shutdown();
        Tr.SetLocale("es");
        Assert.Equal(0, events);
    }

    [Fact]
    public void FontFallbackTableResolvesTheLocaleChain()
    {
        var table = new FontFallbackTable
        {
            DefaultFonts = ["Content/UI/fonts/Lato-Regular.ttf"],
            Locales =
            [
                new LocaleFontSet { Locale = "ja", Fonts = ["Content/UI/fonts/NotoSansJP.otf"] },
                new LocaleFontSet { Locale = "zh-Hans", Fonts = ["Content/UI/fonts/NotoSansSC.otf", "Content/UI/fonts/Lato-Regular.ttf"] },
                new LocaleFontSet { Locale = "zh", Fonts = ["Content/UI/fonts/NotoSansTC.otf"] },
            ],
        };

        Assert.Equal(["Content/UI/fonts/NotoSansJP.otf", "Content/UI/fonts/Lato-Regular.ttf"], table.Resolve(["ja_JP", "ja", "en"]));
        Assert.Equal(
            ["Content/UI/fonts/NotoSansSC.otf", "Content/UI/fonts/Lato-Regular.ttf", "Content/UI/fonts/NotoSansTC.otf"],
            table.Resolve(LocaleId.Chain("zh_Hans_CN", "en")));
        Assert.Equal(["Content/UI/fonts/Lato-Regular.ttf"], table.Resolve(["fr", "en"]));

        // It is an ordinary resource (a .mres file edited in the inspector).
        var info = TypeRegistry.Get(typeof(FontFallbackTable))!;
        Assert.Equal(["ResourceName", "DefaultFonts", "Locales"], info.Properties.Select(p => p.Name));
        Assert.Equal("*.ttf,*.otf", info.FindProperty("DefaultFonts")!.Hints.File);
        Assert.Equal(["ResourceName", "Locale", "Fonts"], TypeRegistry.Get(typeof(LocaleFontSet))!.Properties.Select(p => p.Name));
        Assert.Null(FontFallbackTable.TryLoad(Path.Combine(_fixture.Directory, "missing.mres")));
    }

    /// <summary>Switches the locale from inside its process callback, as a settings menu would.</summary>
    private sealed class LocaleSwitcher(LocalizedLabel label) : Node
    {
        public string? SeenDuringProcess { get; private set; }

        protected override void OnProcess(in GameTime gameTime)
        {
            Tr.SetLocale("es");
            SeenDuringProcess = label.Displayed;
        }
    }
}
