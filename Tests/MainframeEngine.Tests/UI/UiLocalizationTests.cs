using MainframeEngine.Localization;
using MainframeEngine.Tests.Localization;

namespace MainframeEngine.Tests.UI;

/// <summary>
/// M9 × M8: the UI server translates documents through <see cref="Tr"/> (docs/design/localization.md#game-ui-rmlui) —
/// text nodes via <c>TranslateString</c>, attributes and <c>no-tr</c> via <see cref="RmlLocalization.PrepareDocument(string)"/>
/// on every <c>.rml</c> the file interface serves — and reloads its documents after a locale change.
/// </summary>
/// <remarks>RmlUi is process-global (<see cref="SerialRmlUi"/>); <see cref="Tr"/> is reset by the fixture.</remarks>
[Collection(nameof(SerialRmlUi))]
public sealed class UiLocalizationTests : IDisposable
{
    private readonly LocaleFixture _locales = new();

    public UiLocalizationTests()
    {
        _locales.AddCatalog("es", LocaleFixture.OneOther, """
            msgid "Hello"
            msgstr "Hola"

            msgid "Health {{ health }}"
            msgstr "Salud {{ health }}"

            msgid "Keep"
            msgstr "Mantener"

            msgid "Tip"
            msgstr "Consejo"

            msgid "Name"
            msgstr "Nombre"

            msgid "Switch"
            msgstr "Cambiar"

            msgid "Menu"
            msgstr "Menú"

            """);
        _locales.AddCatalog("de", LocaleFixture.OneOther, """
            msgid "Hello"
            msgstr "Hallo"

            msgid "Health {{ health }}"
            msgstr "Gesundheit {{ health }}"

            """);
        _locales.Use("en");
    }

    public void Dispose() => _locales.Dispose();

    private static string Text(UiDocument doc, string id) => doc.GetElementById(id)!.InnerRml.Trim();

    [Fact]
    public void DocumentTextIsTranslatedAtLoadAndAgainAfterALocaleSwitch()
    {
        Tr.SetLocale("es");
        using var ui = new UiTestTree();
        var doc = new HudTestDocument
        {
            Name = "Hud",
            Source = ui.Write("UI/l10n.rml", UiTestTree.Page("""
                <p id="greet">Hello</p>
                <p id="hp">Health {{ health }}</p>
                <button id="btn" title="Tip">Ok</button>
                <input id="name" type="text" placeholder="Name"/>
                """, "data-model='hud'")),
        };
        ui.AddLayer(0, doc);
        ui.Tick(2);
        Assert.Equal("Hola", Text(doc, "greet"));
        Assert.Equal("Salud 42", Text(doc, "hp"));       // the template is translated, then the data view fills it
        Assert.DoesNotContain(RmlLocalization.OptOutMarker, doc.GetElementById("hp")!.InnerRml);
        Assert.Equal("Consejo", doc.GetElementById("btn")!.GetAttribute("title")); // PrepareDocument
        Assert.Equal("Nombre", doc.GetElementById("name")!.GetAttribute("placeholder"));

        var greet = doc.GetElementById("greet")!;
        Tr.SetLocale("de");
        Assert.Equal("Hola", Text(doc, "greet")); // applied at the UI server's next frame, not inside SetLocale
        ui.Tick(2);
        Assert.Equal("Hallo", Text(doc, "greet"));
        Assert.Equal("Gesundheit 42", Text(doc, "hp"));
        Assert.Equal("Tip", doc.GetElementById("btn")!.GetAttribute("title")); // no German catalog entry: source text
        Assert.True(greet.IsValid); // UiElement wrappers follow the reloaded DOM

        doc.Health = 7;
        doc.Model.Dirty("health");
        ui.Tick();
        Assert.Equal("Gesundheit 7", Text(doc, "hp")); // the data model survived the reload

        Tr.SetLocale("en");
        ui.Tick(2);
        Assert.Equal("Hello", Text(doc, "greet"));
        Assert.Equal("Health 7", Text(doc, "hp"));
    }

    [Fact]
    public void NoTrElementsKeepTheirSourceText()
    {
        Tr.SetLocale("es");
        using var ui = new UiTestTree();
        var doc = new UiDocument
        {
            Name = "NoTr",
            Source = ui.Write("UI/no-tr.rml", UiTestTree.Page("""
                <p id="a">Keep</p>
                <p id="b" class="no-tr">Keep</p>
                <div class="no-tr"><span id="c">Keep</span></div>
                <input id="d" type="text" placeholder="Keep" class="no-tr"/>
                <input id="e" type="text" placeholder="Keep"/>
                """)),
        };
        ui.AddLayer(0, doc);
        ui.Tick(2);
        Assert.Equal("Mantener", Text(doc, "a"));
        Assert.Equal("Keep", Text(doc, "b"));
        Assert.Equal("Keep", Text(doc, "c"));
        Assert.DoesNotContain(RmlLocalization.OptOutMarker, doc.GetElementById("b")!.InnerRml); // the marker never renders
        Assert.Equal("Keep", doc.GetElementById("d")!.GetAttribute("placeholder"));
        Assert.Equal("Mantener", doc.GetElementById("e")!.GetAttribute("placeholder"));
    }

    [Fact]
    public void InMemoryDocumentsAndPanelTitlesAreTranslated()
    {
        Tr.SetLocale("es");
        using var ui = new UiTestTree();
        var inline = new UiDocument { Name = "Inline", Rml = UiTestTree.Page("<p id='t' class='x' title='Tip'>Hello</p>") };
        var panel = new UiDocument
        {
            Name = "Panel",
            Source = ui.Write("UI/panel-doc.rml", """
                <rml><head><title>Menu</title><link type="text/template" href="/Content/UI/widgets/panel.rml"/></head>
                <body template="mf-panel"><p id="t">Hello</p></body></rml>
                """),
        };
        ui.AddLayer(0, inline, panel);
        ui.Tick(2);
        Assert.Equal("Hola", Text(inline, "t"));
        Assert.Equal("Consejo", inline.GetElementById("t")!.GetAttribute("title"));
        Assert.Equal("Menú", Text(panel, UiDocument.PanelTitleId)); // RmlUi translated <title>; the panel shows it once
        Assert.Equal("Hola", Text(panel, "t"));
    }

    [Fact]
    public void SwitchingLocaleInsideAUiCallbackIsDeferredToTheNextFrame()
    {
        using var ui = new UiTestTree();
        var doc = new UiDocument
        {
            Name = "Menu",
            Source = ui.Write("UI/switch.rml", UiTestTree.Page("<p id='t'>Hello</p><button id='go'>Switch</button>")),
        };
        ui.AddLayer(0, doc);
        ui.Tick();
        var clicks = 0;
        doc.GetElementById("go")!.Click += _ =>
        {
            clicks++;
            Tr.SetLocale("es"); // inside RmlUi's event dispatch: the document must not be reloaded under it
        };

        doc.Document.GetElementById("go").Click();
        Assert.Equal(1, clicks);
        Assert.Equal("Hello", Text(doc, "t"));
        ui.Tick();
        Assert.Equal("Hola", Text(doc, "t"));
        Assert.Equal("Cambiar", Text(doc, "go"));

        doc.Document.GetElementById("go").Click(); // the re-attached listener still works after the reload
        Assert.Equal(2, clicks);
    }

    [Fact]
    public void LocaleSwitchLoadsTheLocalesFallbackFonts()
    {
        var tablePath = Path.Combine(_locales.Directory, "fonts.mres");
        ResourceSaver.Save(new FontFallbackTable
        {
            Locales =
            [
                new LocaleFontSet { Locale = "ja", Fonts = ["Content/UI/fonts/RobotoMono-Regular.ttf"] },
                new LocaleFontSet { Locale = "de", Fonts = ["Content/UI/fonts/LatoLatin-Italic.ttf", "Content/UI/fonts/missing.ttf"] },
            ],
        }, tablePath);

        using var ui = new UiTestTree(new UiServerOptions { FontFallbackTablePath = tablePath });
        Assert.Empty(ui.Server.FallbackFonts); // en: no set for it, no defaults

        Tr.SetLocale("ja"); // no catalog: source text, but the fonts still follow the locale
        ui.Tick();
        Assert.Equal(["Content/UI/fonts/RobotoMono-Regular.ttf"], ui.Server.FallbackFonts);

        Tr.SetLocale("de");
        ui.Tick();
        Assert.Equal(2, ui.Server.FallbackFonts.Count); // faces accumulate (RmlUi cannot unload one); a missing one is skipped
        Assert.Contains("Content/UI/fonts/LatoLatin-Italic.ttf", ui.Server.FallbackFonts);
    }

    [Fact]
    public void WithoutATranslatorTextStaysUntranslated()
    {
        Tr.SetLocale("es");
        using var ui = new UiTestTree(new UiServerOptions { TextTranslator = null });
        Assert.Null(ui.Server.Translator);
        var doc = new UiDocument { Name = "Plain", Rml = UiTestTree.Page("<p id='t' class='no-tr'>Hello</p><p id='u'>Hello</p>") };
        ui.AddLayer(0, doc);
        ui.Tick();
        Assert.Equal("Hello", Text(doc, "t"));
        Assert.Equal("Hello", Text(doc, "u"));
    }

    [Fact]
    public void DataBoundValuesAreNeverTranslated()
    {
        Tr.SetLocale("es");
        using var ui = new UiTestTree();
        var doc = new NameDocument
        {
            Name = "Names",
            Source = ui.Write("UI/names.rml", UiTestTree.Page("""
                <p id="plain">{{ name }}</p><p id="tpl">Hello {{ name }}</p><p id="kept" class="no-tr">Name: {{ name }}</p>
                """, "data-model='names'")),
        };
        ui.AddLayer(0, doc);
        ui.Tick(2);
        Assert.Equal("Hello", Text(doc, "plain")); // the value "Hello" is a msgid, but it is data, not document text
        Assert.Equal("Hello Hello", Text(doc, "tpl"));
        Assert.Equal("Name: Hello", Text(doc, "kept"));

        doc.Value = "Keep";
        doc.Model.Dirty("name");
        ui.Tick();
        Assert.Equal("Keep", Text(doc, "plain"));
        Assert.Equal("Name: Keep", Text(doc, "kept"));
        Assert.DoesNotContain(RmlLocalization.OptOutMarker, doc.GetElementById("kept")!.InnerRml);
    }

    private sealed class NameDocument : UiDocument
    {
        public string Value { get; set; } = "Hello";
        public MainframeEngine.UI.Rml.RmlDataModel Model { get; private set; } = null!;

        protected override void OnReady() => Model = CreateDataModel("names").Bind("name", this, static d => d.Value);
    }

    [Fact]
    public void TranslatedHudFramesDoNotAllocate()
    {
        // Missing translations are logged here (as in Debug builds): RmlUi re-sends the substituted data-bound text
        // ("Salud 37", "HP 37 left") on every change; it must not be looked up, logged or allocate.
        Tr.Configure(new LocalizationOptions { LocaleDirectory = _locales.Directory, LogMissingTranslations = true }, "es");
        using var ui = new UiTestTree();
        var doc = new HudTestDocument
        {
            Name = "Hud",
            Source = ui.Write("UI/alloc-l10n.rml", UiTestTree.Page("""
                <p id="hp">Health {{ health }}</p><p id="raw">HP {{ health }} left</p>
                <div style="width: 200px; height: 10px; background-color: #f00;" data-style-width="health + 'px'"></div>
                <button id="btn">Hello</button>
                """, "data-model='hud'")),
        };
        ui.AddLayer(0, doc);
        ui.Tick(30);
        Assert.Equal("Hola", Text(doc, "btn"));

        var frame = 0;
        void Frames(int count)
        {
            for (var end = frame + count; frame < end; frame++)
            {
                doc.Health = frame; // new values in every window: each would be a new "missing" message
                doc.Model.Dirty("health");
                ui.Tick();
            }
        }

        Frames(30);
        Assert.Equal(0, AllocationGate.SmallestWindow(() => Frames(200)));
        Assert.StartsWith("Salud ", Text(doc, "hp"), StringComparison.Ordinal);
    }
}
