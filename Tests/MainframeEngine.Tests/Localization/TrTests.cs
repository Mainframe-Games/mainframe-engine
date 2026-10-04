using System.Globalization;
using System.Text;
using MainframeEngine.Localization;

namespace MainframeEngine.Tests.Localization;

/// <summary>
/// <see cref="Tr"/> over real <c>.mo</c> catalogs: loading, the fallback chain, contexts, plurals with complex rules,
/// format-safe placeholders, interpolated strings, runtime switching and allocation-free lookups.
/// </summary>
[Collection(nameof(LocalizationState))]
public sealed class TrTests : IDisposable
{
    private readonly LocaleFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void UnconfiguredReturnsSourceText()
    {
        Tr.ResetForTests();
        Assert.Equal("en", Tr.CurrentLocale);
        const string text = "Untouched";
        Assert.Same(text, Tr._(text));
        Assert.Equal("Open", Tr.P("menu", "Open"));
        Assert.Equal("1 file", Tr.N("{0} file", "{0} files", 1));
        Assert.Equal("3 files", Tr.N("{0} file", "{0} files", 3));
        Assert.Equal(string.Empty, Tr._(string.Empty));
    }

    [Fact]
    public void FallbackChainPrefersTheMostSpecificCatalog()
    {
        _fixture.AddCatalog("pt_BR", LocaleFixture.OneOther, """
            msgid "Hello"
            msgstr "Oi"
            """);
        _fixture.AddCatalog("pt", LocaleFixture.OneOther, """
            msgid "Hello"
            msgstr "Olá"

            msgid "Goodbye"
            msgstr "Adeus"
            """);

        _fixture.Use("pt-BR"); // .NET form is accepted and normalised
        Assert.Equal("pt_BR", Tr.CurrentLocale);
        Assert.Equal(["pt_BR", "pt", "en"], Tr.LocaleChain);
        Assert.Equal(["pt_BR", "pt"], Tr.LoadedLocales);
        Assert.Equal("Oi", Tr._("Hello"));       // pt_BR
        Assert.Equal("Adeus", Tr._("Goodbye"));  // falls back to pt
        Assert.Equal("Missing", Tr._("Missing")); // falls back to the source text
        Assert.Equal("pt-BR", Tr.Culture.Name);

        _fixture.Use("pt");
        Assert.Equal("Olá", Tr._("Hello"));
    }

    [Fact]
    public void ConfiguredFallbackLocalesComeBeforeTheSourceLocale()
    {
        _fixture.AddCatalog("es", LocaleFixture.OneOther, """
            msgid "Settings"
            msgstr "Ajustes"
            """);
        _fixture.Use("ca", fallbacks: ["es"]);
        Assert.Equal(["ca", "es", "en"], Tr.LocaleChain);
        Assert.Equal("Ajustes", Tr._("Settings"));
    }

    [Fact]
    public void ContextsAreSeparateMessages()
    {
        _fixture.AddCatalog("de", LocaleFixture.OneOther, """
            msgid "Open"
            msgstr "Offen"

            msgctxt "menu"
            msgid "Open"
            msgstr "Öffnen"
            """);
        _fixture.Use("de");
        Assert.Equal("Offen", Tr._("Open"));
        Assert.Equal("Öffnen", Tr.P("menu", "Open"));
        Assert.Equal("Open", Tr.P("door", "Open"));
        Assert.True(Tr.HasTranslation("Open", "menu"));
        Assert.False(Tr.HasTranslation("Open", "door"));
    }

    [Theory]
    [InlineData(1, "1 plik")]
    [InlineData(2, "2 pliki")]
    [InlineData(4, "4 pliki")]
    [InlineData(5, "5 plików")]
    [InlineData(12, "12 plików")]
    [InlineData(22, "22 pliki")]
    [InlineData(112, "112 plików")]
    [InlineData(0, "0 plików")]
    public void PolishPluralsFollowThePluralFormsHeader(long n, string expected)
    {
        _fixture.AddCatalog("pl", LocaleFixture.Polish, """
            #, csharp-format
            msgid "{0} file"
            msgid_plural "{0} files"
            msgstr[0] "{0} plik"
            msgstr[1] "{0} pliki"
            msgstr[2] "{0} plików"
            """);
        _fixture.Use("pl");
        Assert.Equal(expected, Tr.N("{0} file", "{0} files", n));
    }

    [Theory]
    [InlineData(1, "1 враг")]
    [InlineData(21, "21 враг")]
    [InlineData(3, "3 врага")]
    [InlineData(11, "11 врагов")]
    [InlineData(111, "111 врагов")]
    [InlineData(25, "25 врагов")]
    public void RussianPluralsWithContext(long n, string expected)
    {
        _fixture.AddCatalog("ru", LocaleFixture.Russian, """
            msgctxt "hud"
            msgid "{0} enemy"
            msgid_plural "{0} enemies"
            msgstr[0] "{0} враг"
            msgstr[1] "{0} врага"
            msgstr[2] "{0} врагов"
            """);
        _fixture.Use("ru");
        Assert.Equal(expected, Tr.NP("hud", "{0} enemy", "{0} enemies", n));
        Assert.Equal(n == 1 ? "1 enemy" : $"{n} enemies", Tr.N("{0} enemy", "{0} enemies", n)); // no context: untranslated
    }

    [Fact]
    public void PluralArgumentsAfterCountStartAtOne()
    {
        _fixture.AddCatalog("es", LocaleFixture.OneOther, """
            msgid "{0} file in {1}"
            msgid_plural "{0} files in {1}"
            msgstr[0] "{0} archivo en {1}"
            msgstr[1] "{0} archivos en {1}"
            """);
        _fixture.Use("es");
        Assert.Equal("3 archivos en Documentos", Tr.N("{0} file in {1}", "{0} files in {1}", 3, "Documentos"));
        Assert.Equal("1 archivo en /", Tr.N("{0} file in {1}", "{0} files in {1}", 1, "/"));
    }

    [Fact]
    public void PartiallyTranslatedPluralFallsBackToSource()
    {
        _fixture.AddCatalog("pl", LocaleFixture.Polish, """
            msgid "{0} cat"
            msgid_plural "{0} cats"
            msgstr[0] "{0} kot"
            msgstr[1] ""
            msgstr[2] "{0} kotów"
            """);
        _fixture.Use("pl");
        Assert.Equal("1 kot", Tr.N("{0} cat", "{0} cats", 1));
        Assert.Equal("3 cats", Tr.N("{0} cat", "{0} cats", 3)); // form 1 is empty
        Assert.Equal("5 kotów", Tr.N("{0} cat", "{0} cats", 5));
    }

    [Fact]
    public void FormatsWithTheLocaleCulture()
    {
        _fixture.AddCatalog("de", LocaleFixture.OneOther, """
            msgid "Distance: {0:N1} km"
            msgstr "Entfernung: {0:N1} km"
            """);
        _fixture.Use("de");
        Assert.Equal("Entfernung: 1.234,5 km", Tr._("Distance: {0:N1} km", 1234.5));
        Assert.Equal("Untranslated 1.234,5", Tr._("Untranslated {0:N1}", 1234.5)); // source text, still the locale's culture
    }

    [Fact]
    public void BrokenTranslationsFallBackToTheSourceFormat()
    {
        // msgfmt would reject these with -c; catalogs compiled without checks must still never throw.
        _fixture.AddCatalog("fr", LocaleFixture.OneOther, """
            msgid "Score: {0}"
            msgstr "Score : {1}"

            msgid "Lives: {0}"
            msgstr "Vies : {0"

            msgid "Value: {0}"
            msgstr "Valeur : {0:Q}"

            msgid "{0} vs {1}"
            msgstr "{1} contre {0}"
            """);
        _fixture.Use("fr");
        Assert.Equal("Score: 7", Tr._("Score: {0}", 7));           // translation needs an argument we do not have
        Assert.Equal("Lives: 3", Tr._("Lives: {0}", 3));           // translation is not a valid format
        Assert.Equal("Value: 5", Tr._("Value: {0}", 5));           // the argument rejects the translation's specifier
        Assert.Equal("Bob contre Ann", Tr._("{0} vs {1}", "Ann", "Bob")); // reordering is fine
        Assert.Equal("Broken {0", Tr._("Broken {0", 1));           // even a broken source never throws
    }

    [Fact]
    public void PlainLookupsNeverFormat()
    {
        _fixture.AddCatalog("es", LocaleFixture.OneOther, """
            msgid "Use {braces}"
            msgstr "Usa {llaves}"
            """);
        _fixture.Use("es");
        Assert.Equal("Usa {llaves}", Tr._("Use {braces}"));
        Assert.Equal("Literal {0}", Tr._("Literal {0}"));
    }

    [Fact]
    public void InterpolatedStringsLookUpTheExtractorsMsgid()
    {
        _fixture.AddCatalog("es", LocaleFixture.OneOther, """
            msgid "Hello, {0}!"
            msgstr "¡Hola, {0}!"

            msgid "Val {0,5:N1} {{lit}}"
            msgstr "Valor {0,5:N1} {{lit}}"

            msgctxt "hud"
            msgid "{0} coin"
            msgid_plural "{0} coins"
            msgstr[0] "{0} moneda"
            msgstr[1] "{0} monedas"
            """);
        _fixture.Use("es");
        var name = "Ana";
        var value = 2.5;
        var n = 4;
        Assert.Equal("¡Hola, Ana!", Tr._($"Hello, {name}!"));
        Assert.Equal("Valor   2,5 {lit}", Tr._($"Val {value,5:N1} {{lit}}"));
        Assert.Equal("4 monedas", Tr.NP("hud", $"{n} coin", $"{n} coins", n));
        Assert.Equal("Bye, Ana", Tr._($"Bye, {name}")); // untranslated: the source, formatted
        Assert.Equal("1 coin", Tr.N($"{1} coin", $"{1} coins", 1));
    }

    [Fact]
    public void SetLocaleRaisesLocaleChangedAndReloads()
    {
        var path = _fixture.AddCatalog("es", LocaleFixture.OneOther, """
            msgid "Settings"
            msgstr "Ajustes"
            """);
        _fixture.Use("en");
        var events = new List<(string Previous, string Locale)>();
        void Handler(object? sender, LocaleChangedEventArgs e) => events.Add((e.PreviousLocale, e.Locale));
        Tr.LocaleChanged += Handler;
        try
        {
            Tr.SetLocale("es");
            Assert.Equal("Ajustes", Tr._("Settings"));

            File.WriteAllBytes(path, L10n.Gettext.MoFormat.Write(L10n.Gettext.PoParser.Parse("""
                msgid ""
                msgstr "Content-Type: text/plain; charset=UTF-8\n"

                msgid "Settings"
                msgstr "Configuración"
                """)));
            Tr.Reload();
            Assert.Equal("Configuración", Tr._("Settings"));
        }
        finally
        {
            Tr.LocaleChanged -= Handler;
        }

        Assert.Equal([("en", "es"), ("es", "es")], events);
        Assert.Throws<ArgumentException>(() => Tr.SetLocale("??"));
    }

    [Fact]
    public void AvailableAndStartupLocales()
    {
        _fixture.AddCatalog("es", LocaleFixture.OneOther, "");
        _fixture.AddCatalog("qps", LocaleFixture.OneOther, "");
        System.IO.Directory.CreateDirectory(Path.Combine(_fixture.Directory, "fr")); // no catalog: not offered
        _fixture.Use("en");

        Assert.Equal(["en", "es", "qps"], Tr.GetAvailableLocales());
        Assert.Equal("es", Tr.ResolveStartupLocale("es"));
        Assert.Equal("es_MX", Tr.ResolveStartupLocale("es-MX")); // es_MX → es catalog exists
        Assert.Equal("en_GB", Tr.ResolveStartupLocale("en_GB"));  // the source language needs no catalog
        Assert.NotEqual("fr", Tr.ResolveStartupLocale("fr"));    // no catalog: the OS language or the source locale
    }

    [Fact]
    public void LookupsAreAllocationFree()
    {
        _fixture.AddCatalog("es", LocaleFixture.OneOther, """
            msgid "Settings"
            msgstr "Ajustes"

            msgctxt "menu"
            msgid "Open"
            msgstr "Abrir"

            msgid "{0} item"
            msgid_plural "{0} items"
            msgstr[0] "Un objeto"
            msgstr[1] "Varios objetos"
            """);
        Tr.Configure(new LocalizationOptions { LocaleDirectory = _fixture.Directory, LogMissingTranslations = true }, "es");

        // Warm up (first miss is logged once, JIT, caches).
        var expected = Run();
        Assert.Equal("Ajustes".Length + "Not translated".Length + "Abrir".Length + "Open".Length + "Varios objetos".Length, expected);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var total = 0;
        for (var i = 0; i < 1000; i++)
            total += Run();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected * 1000, total);
        Assert.Equal(0, allocated);

        static int Run() =>
            Tr._("Settings").Length
            + Tr._("Not translated").Length
            + Tr.P("menu", "Open").Length
            + Tr.P("door", "Open").Length
            + Tr.N("{0} item", "{0} items", 3).Length; // no placeholder in the translation: no formatting
    }

    [Fact]
    public void CorruptCatalogsAreSkipped()
    {
        var path = Path.Combine(_fixture.Directory, "xx", "LC_MESSAGES", "messages.mo");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("not a catalog at all, definitely"));
        _fixture.Use("xx");
        Assert.Empty(Tr.LoadedLocales);
        Assert.Equal("Fine", Tr._("Fine"));
    }

    [Fact]
    public void LocaleIds()
    {
        Assert.Equal("pt_BR", LocaleId.Normalize("pt-br"));
        Assert.Equal("pt_BR", LocaleId.Normalize(" pt_BR.UTF-8@euro "));
        Assert.Equal("zh_Hans_CN", LocaleId.Normalize("ZH-hans-cn"));
        Assert.Equal("es_419", LocaleId.Normalize("es-419"));
        Assert.Equal(string.Empty, LocaleId.Normalize("../etc"));
        Assert.Equal(string.Empty, LocaleId.Normalize(null));
        Assert.Equal("pt-BR", LocaleId.ToCultureName("pt_BR"));
        Assert.Equal("zh", LocaleId.Language("zh_Hans_CN"));
        Assert.Equal(["zh_Hans_CN", "zh_Hans", "zh", "en"], LocaleId.Chain("zh-Hans-CN", "en"));
        Assert.Equal(["en_US", "en"], LocaleId.Chain("en_US", "en"));
        // Fallbacks serve other languages only: the source language never shows a fallback's translation.
        Assert.Equal(["pt_BR", "pt", "es", "en"], LocaleId.Chain("pt-BR", "en", ["es"]));
        Assert.Equal(["en_GB", "en"], LocaleId.Chain("en_GB", "en", ["es"]));
        Assert.Equal(["en"], LocaleId.Chain("en", "en", ["es"]));
        Assert.Equal("en", LocaleId.GetCulture("qps").Name);
        Assert.Equal(CultureInfo.GetCultureInfo("es").NativeName, LocaleId.DisplayName("es"));
        Assert.Equal("Pseudo-locale (qps)", LocaleId.DisplayName("qps"));
    }
}
