using System.Text;
using MainframeEngine.Localization;

namespace MainframeEngine.Tests.Localization;

/// <summary>
/// The RML side of the M8 contract: the parser shared by extraction and runtime, the <c>TranslateString</c> hook
/// (<see cref="Tr.TranslateMarkup(string, out string)"/>), the <c>no-tr</c> opt-out and document preparation.
/// </summary>
[Collection(nameof(LocalizationState))]
public sealed class RmlLocalizationTests : IDisposable
{
    private readonly LocaleFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static string MenuSource => File.ReadAllText(Path.Combine(LocaleFixture.FixtureProject, "Content", "UI", "menu.rml"));

    [Fact]
    public void ScanFindsTextAndTranslatableAttributes()
    {
        var runs = RmlLocalization.Scan(MenuSource);
        var extracted = runs.Where(r => !r.OptedOut).Select(r => (r.Kind, r.Text, r.Line)).ToList();
        Assert.Equal(
        [
            (RmlTextKind.Attribute, "Main menu", 11),
            (RmlTextKind.Text, "Start game", 13),
            (RmlTextKind.Text, "Press", 14),
            (RmlTextKind.Text, "Start", 14),
            (RmlTextKind.Text, "to continue your adventure.", 14),
            (RmlTextKind.Text, "Options & settings", 16),
            (RmlTextKind.Attribute, "Player name", 17),
            (RmlTextKind.Attribute, "Apply", 18),
            (RmlTextKind.Text, "Score: {{ score }}", 21),
            (RmlTextKind.Text, "Fish <3 © ☺", 25),
            (RmlTextKind.Text, "Settings", 26),
        ], extracted);

        var optedOut = runs.Where(r => r.OptedOut).Select(r => r.Text).ToList();
        Assert.Equal(["Debug build", "Nested opt-out", "Not this either", "x"], optedOut);
        Assert.Equal("placeholder", runs.Single(r => r.Text == "Player name").Attribute);
        Assert.Equal("button", runs.Single(r => r.Text == "Options & settings").Element);
    }

    [Theory]
    [InlineData("  Start \n\t game  ", "Start game")]
    [InlineData("A&amp;B &lt;tag&gt; &quot;q&quot; &apos;s&apos;", "A&B <tag> \"q\" 's'")]
    [InlineData("non&nbsp;breaking", "non breaking")]
    [InlineData("&#65;&#x42;&unknown; &", "AB&unknown; &")]
    [InlineData("&#xD800;", "&#xD800;")] // a lone surrogate is not a character
    public void NormalizeDecodesEntitiesAndCollapsesWhitespace(string raw, string expected) =>
        Assert.Equal(expected, RmlLocalization.Normalize(raw));

    [Theory]
    [InlineData("Settings", true)]
    [InlineData("{{ score }}", false)]
    [InlineData("{{a}} / {{b}}", false)]
    [InlineData("Score: {{ score }}", true)]
    [InlineData("100%", false)]
    [InlineData("{{ unterminated", false)]
    [InlineData("日本語", true)]
    public void OnlyTextWithLettersIsTranslatable(string text, bool expected) =>
        Assert.Equal(expected, RmlLocalization.IsTranslatable(text));

    [Fact]
    public void TranslateMarkupMatchesNormalisedTextAndEncodesTheResult()
    {
        _fixture.AddCatalog("es", LocaleFixture.OneOther, """
            msgid "to continue your adventure."
            msgstr "para continuar tu aventura."

            msgid "Options & settings"
            msgstr "Opciones <y> ajustes"

            msgid "Score: {{ score }}"
            msgstr "Puntos: {{ score }}"
            """);
        _fixture.Use("es");

        // RmlUi passes the raw text node, whitespace and entities included; outer whitespace survives as one space.
        Assert.True(Tr.TranslateMarkup(" to   continue\n   your adventure.", out var translated));
        Assert.Equal(" para continuar tu aventura.", translated);
        Assert.True(Tr.TranslateMarkup("Options &amp; settings", out translated));
        Assert.Equal("Opciones &lt;y&gt; ajustes", translated); // a translation can never inject markup
        Assert.True(Tr.TranslateMarkup(Encoding.UTF8.GetBytes("Score: {{ score }}"), out translated));
        Assert.Equal("Puntos: {{ score }}", translated);

        Assert.False(Tr.TranslateMarkup("Not in the catalog", out translated));
        Assert.Null(translated);
        Assert.False(Tr.TranslateMarkup("{{ score }}", out _));
        Assert.False(Tr.TranslateMarkup("   ", out _));
        Assert.False(Tr.TranslateMarkup(ReadOnlySpan<byte>.Empty, out _));
    }

    [Fact]
    public void OptOutMarkerIsStrippedWithoutTranslating()
    {
        _fixture.AddCatalog("es", LocaleFixture.OneOther, """
            msgid "Debug build"
            msgstr "Compilación de depuración"
            """);
        _fixture.Use("es");
        Assert.True(Tr.TranslateMarkup(RmlLocalization.OptOutMarker + "Debug build", out var translated));
        Assert.Equal("Debug build", translated);
        Assert.True(Tr.TranslateMarkup(Encoding.UTF8.GetBytes(RmlLocalization.OptOutMarker + " Debug build"), out translated));
        Assert.Equal(" Debug build", translated);
        Assert.True(Tr.TranslateMarkup("Debug build", out translated)); // the same text outside no-tr is translated
        Assert.Equal("Compilación de depuración", translated);
    }

    [Fact]
    public void PrepareDocumentTranslatesAttributesAndMarksOptedOutText()
    {
        _fixture.AddCatalog("es", LocaleFixture.OneOther, """
            msgid "Player name"
            msgstr "Nombre del \"jugador\""

            msgid "Apply"
            msgstr "Aplicar"

            msgid "Main menu"
            msgstr "Menú principal"

            msgid "Not this either"
            msgstr "Tampoco esto"
            """);
        _fixture.Use("es");

        var source = MenuSource;
        var prepared = RmlLocalization.PrepareDocument(source);
        Assert.Contains("placeholder=\"Nombre del &quot;jugador&quot;\"", prepared, StringComparison.Ordinal);
        Assert.Contains("<input type=\"submit\" value=\"Aplicar\"/>", prepared, StringComparison.Ordinal);
        Assert.Contains("<body title=\"Menú principal\">", prepared, StringComparison.Ordinal);
        Assert.Contains("value=\"Typed by the player\"", prepared, StringComparison.Ordinal); // text inputs keep their value
        Assert.Contains("title=\"Not this either\"", prepared, StringComparison.Ordinal);      // inside no-tr
        Assert.Contains("<p class=\"hud no-tr\">﷐Debug build</p>", prepared, StringComparison.Ordinal);
        Assert.Contains("<span>﷐Nested opt-out</span>", prepared, StringComparison.Ordinal);
        Assert.Contains("<h1>Start   game</h1>", prepared, StringComparison.Ordinal); // text nodes are left to TranslateString
        Assert.Equal(source.Length + 3 /* markers */, prepared.Length - ("Menú principal".Length - "Main menu".Length)
            - ("Nombre del &quot;jugador&quot;".Length - "Player name".Length) - ("Aplicar".Length - "Apply".Length));

        // Through the translator interface (what UiServer will call) and with nothing to change.
        var translator = TextTranslator.Current; // the ITextTranslator UiServer will be given
        Assert.Equal(prepared, translator.PrepareDocument(source));
        const string plain = "<rml><body><p>Untranslated</p></body></rml>";
        Assert.Same(plain, translator.PrepareDocument(plain));
        Assert.Equal("es", translator.Locale);
    }

    [Fact]
    public void ScannerToleratesMalformedMarkup()
    {
        var runs = RmlLocalization.Scan("<body><p>Unclosed <b>bold<p>Next</div> a < b <img src=x/><p class='no-tr'>Single quoted</p>");
        Assert.Equal(["Unclosed", "bold", "Next", "a", "< b"], runs.Where(r => !r.OptedOut).Select(r => r.Text));
        Assert.Equal(["Single quoted"], runs.Where(r => r.OptedOut).Select(r => r.Text));
        Assert.Empty(RmlLocalization.Scan("Text outside any element"));
        Assert.Empty(RmlLocalization.Scan("<!-- unterminated comment <p>x</p>"));
    }
}
