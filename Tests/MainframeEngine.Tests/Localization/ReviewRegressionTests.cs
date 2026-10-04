using MainframeEngine.L10n;
using MainframeEngine.L10n.Extraction;
using MainframeEngine.L10n.Gettext;
using MainframeEngine.Localization;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Localization;

/// <summary>Regressions from the M9 code review.</summary>
[Collection(nameof(LocalizationState))]
public sealed class ReviewRegressionTests : IDisposable
{
    private readonly LocaleFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void BrokenPluralTranslationFallsBackToTheSourceMatchingN()
    {
        _fixture.AddCatalog("es", LocaleFixture.OneOther, """
            msgid "{0} enemy left"
            msgid_plural "{0} enemies left"
            msgstr[0] "Queda {0"
            msgstr[1] "Quedan {0"
            """);
        _fixture.Use("es");
        Assert.Equal("5 enemies left", Tr.N("{0} enemy left", "{0} enemies left", 5));
        Assert.Equal("1 enemy left", Tr.N("{0} enemy left", "{0} enemies left", 1));
    }

    [Fact]
    public void InterpolatedPluralFormsGetEveryArgument()
    {
        // Russian form 0 is also used for 21, so it may use the plural's arguments.
        _fixture.AddCatalog("ru", LocaleFixture.Russian, """
            msgid "{0} has one file"
            msgid_plural "{0} has {1} files"
            msgstr[0] "У {0} {1} файл"
            msgstr[1] "У {0} {1} файла"
            msgstr[2] "У {0} {1} файлов"
            """);
        _fixture.Use("ru");
        var name = "Ани";
        long n = 21;
        Assert.Equal("У Ани 21 файл", Tr.N($"{name} has one file", $"{name} has {n} files", n));
    }

    [Fact]
    public void ConfigureIgnoresInvalidOrUncatalogedSavedLocales()
    {
        _fixture.AddCatalog("es", LocaleFixture.OneOther, "");
        Tr.Configure(new LocalizationOptions { LocaleDirectory = _fixture.Directory, LogMissingTranslations = false }, "??");
        Assert.NotEqual("??", Tr.CurrentLocale);
        Tr.Configure(new LocalizationOptions { LocaleDirectory = _fixture.Directory, FallbackLocales = ["es"] }, "ca");
        Assert.Equal("ca", Tr.CurrentLocale); // its chain reaches the es fallback
    }

    [Fact]
    public void FailingListenersDoNotStopOtherNotifications()
    {
        _fixture.Use("en");
        var reached = false;
        void Throwing(object? sender, LocaleChangedEventArgs e) => throw new InvalidOperationException("listener bug");
        void Counting(object? sender, LocaleChangedEventArgs e) => reached = true;
        Tr.LocaleChanged += Throwing;
        Tr.LocaleChanged += Counting;
        try
        {
            Tr.SetLocale("es");
            Assert.True(reached);
        }
        finally
        {
            Tr.LocaleChanged -= Throwing;
            Tr.LocaleChanged -= Counting;
        }
    }

    [Fact]
    public void UpdateRestoresObsoleteTranslationsAndSkipsFuzzyInValidation()
    {
        var template = PoParser.Parse("""
            msgid ""
            msgstr "Content-Type: text/plain; charset=UTF-8\n"

            msgid "Back again"
            msgstr ""
            """);
        var catalog = PoParser.Parse("""
            msgid ""
            msgstr "Content-Type: text/plain; charset=UTF-8\n"

            #~ msgid "Back again"
            #~ msgstr "De vuelta"

            #, fuzzy, csharp-format
            msgid "Score: {0}"
            msgstr "Puntos: {1}"
            """);
        L10nCli.MergeInto(catalog, template, "es");
        var restored = catalog.ByKey()["Back again"];
        Assert.False(restored.Obsolete);
        Assert.Equal("De vuelta", restored.Translations[0]);

        // A fuzzy work-in-progress translation does not fail compilation (it is not compiled).
        var po = Path.Combine(_fixture.Directory, "fuzzy.po");
        File.WriteAllText(po, """
            msgid ""
            msgstr "Content-Type: text/plain; charset=UTF-8\n"

            #, fuzzy, csharp-format
            msgid "Score: {0}"
            msgstr "Puntos: {1}"
            """);
        Assert.Equal(0, L10nCli.Run(["compile", po], TextWriter.Null, TextWriter.Null));
        Assert.Equal(1, L10nCli.Run(["compile", po, "--use-fuzzy"], TextWriter.Null, TextWriter.Null));
    }

    [Fact]
    public void LocaleFoldersMustBeNormalised()
    {
        var folder = Path.Combine(_fixture.Directory, "pt-BR", "LC_MESSAGES");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "messages.po"), "msgid \"\"\nmsgstr \"Content-Type: text/plain; charset=UTF-8\\n\"\n");
        using var error = new StringWriter();
        Assert.Equal(1, L10nCli.Run(["compile", "--dir", _fixture.Directory], TextWriter.Null, error));
        Assert.Contains("rename the folder to 'pt_BR'", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InstancesResolveWhenTheRootIsAboveTheProject()
    {
        TypeRegistry.EnsureRegistered(typeof(LocalizedLabel).Assembly);
        var project = LocaleFixture.FixtureProject;
        var root = Path.GetDirectoryName(project)!; // like `just l10n-extract --root .` at the repository root
        var builder = new TemplateBuilder();
        var extractor = new SceneExtractor(TranslatablePropertyIndex.FromRegistry([], []), root);
        extractor.Extract(Path.Combine(project, "Content", "Scenes", "Main.mscene"), "Main.mscene", builder);
        var entries = builder.Build("x").ByKey();
        Assert.True(entries.ContainsKey("Overridden panel title"));
        Assert.True(entries.ContainsKey("Overridden caption"));
    }

    [Fact]
    public void UnquotedAttributesAreQuotedWhenTranslated()
    {
        _fixture.AddCatalog("es", LocaleFixture.OneOther, """
            msgid "Hello"
            msgstr "Hola mundo"
            """);
        _fixture.Use("es");
        Assert.Equal("<body><p title=\"Hola mundo\">x</p></body>",
            RmlLocalization.PrepareDocument("<body><p title=Hello>x</p></body>"));
    }
}
