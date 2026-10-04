using System.Diagnostics;
using MainframeEngine.L10n;
using MainframeEngine.L10n.Gettext;
using MainframeEngine.Localization;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Localization;

/// <summary>
/// End to end on a copy of the fixture project, as <c>just l10n-extract</c> / <c>l10n-compile</c> / <c>l10n-check</c> run it:
/// extract (C# + RML + scenes) → update/create .po → translate → compile → check → pseudo-locale → stats, then the
/// runtime loads the result.
/// </summary>
[Collection(nameof(LocalizationState))]
public sealed class L10nCliTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-l10n-cli", Guid.NewGuid().ToString("N"));
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public L10nCliTests()
    {
        CopyDirectory(LocaleFixture.FixtureProject, _project);
        TypeRegistry.EnsureRegistered(typeof(LocalizedLabel).Assembly);
    }

    public void Dispose()
    {
        Tr.ResetForTests();
        _output.Dispose();
        _error.Dispose();
        try
        {
            Directory.Delete(_project, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string P(string relative) => Path.Combine(_project, relative);

    private int Run(params string[] args) => L10nCli.Run(args, _output, _error);

    private string LocaleDir => P("Content/locale");

    private string Pot => P("Content/locale/messages.pot");

    [Fact]
    public void FullWorkflowOnAFixtureProject()
    {
        // 1. C# through GetText.NET's extractor when it is restored (CI and `just` restore it), then everything else.
        var include = new List<string>();
        if (ExtractionTests.RunExtractor(P("Src"), P("obj/code.pot"), out _))
            include.AddRange(["--include", P("obj/code.pot")]);
        Assert.Equal(0, Run([
            "extract", "-o", Pot, "--root", _project, "--project", "Fixture",
            "--rml", P("Content/UI"), "--scenes", P("Content/Scenes"), "--translatable", "UnknownWidget.Text", .. include]));

        var template = PoParser.ParseFile(Pot).ByKey();
        Assert.Contains("Content/UI/menu.rml:26", template["Settings"].References);
        Assert.Contains("Content/Scenes/Main.mscene:16", template["Settings"].References);
        Assert.True(template.ContainsKey("Unknown type text"));
        if (include.Count > 0)
        {
            // One entry for C#, RML and scene occurrences; C# references rebased onto the project root.
            Assert.Contains("Src/Hud.cs:10", template["Settings"].References);
            Assert.Equal("{0} enemies left", template["{0} enemy left"].IdPlural);
        }

        // Re-extracting with no changes leaves the template byte-identical (no dates, stable order).
        var first = File.ReadAllText(Pot);
        Assert.Equal(0, Run([
            "extract", "-o", Pot, "--root", _project, "--project", "Fixture",
            "--rml", P("Content/UI"), "--scenes", P("Content/Scenes"), "--translatable", "UnknownWidget.Text", .. include]));
        Assert.Equal(first, File.ReadAllText(Pot));

        // 2. New catalogs (msginit) with the right plural rules.
        Assert.Equal(0, Run("update", "--pot", Pot, "--dir", LocaleDir, "--create", "es,pl"));
        var esPath = L10nCli.PoPath(LocaleDir, "es", "messages");
        var es = PoParser.ParseFile(esPath);
        Assert.Equal("es", es.Language);
        Assert.Equal(2, es.PluralCount);
        Assert.Equal(3, PoParser.ParseFile(L10nCli.PoPath(LocaleDir, "pl", "messages")).PluralCount);

        // 3. A translator fills in Spanish.
        Translate(es, "Settings", "Ajustes");
        Translate(es, "Start game", "Empezar partida");
        Translate(es, "Player name", "Nombre del jugador");
        Translate(es, "Overridden caption", "Leyenda sustituida");
        PoWriter.WriteFile(es, esPath);

        // 4. Compile and verify.
        Assert.Equal(0, Run("compile", "--dir", LocaleDir));
        Assert.True(File.Exists(Path.ChangeExtension(esPath, ".mo")));
        Assert.Equal(0, Run("check", "--dir", LocaleDir));

        // 5. Pseudo-locale.
        Assert.Equal(0, Run("pseudo", "--pot", Pot, "--dir", LocaleDir, "--compile"));
        var qps = PoParser.ParseFile(L10nCli.PoPath(LocaleDir, "qps", "messages")).ByKey();
        Assert.Equal("[Šéţţîñĝš ~~]", qps["Settings"].Translations[0]);

        // 6. Coverage.
        Assert.Equal(0, Run("stats", "--dir", LocaleDir, "--pot", Pot));
        Assert.Matches(@"es\s+4\s+0\s+\d+", _output.ToString());

        // 7. The runtime reads what the tool wrote.
        Tr.Configure(new LocalizationOptions { LocaleDirectory = LocaleDir, LogMissingTranslations = false }, "es");
        Assert.Equal(["en", "es", "pl", "qps"], Tr.GetAvailableLocales());
        Assert.Equal("Ajustes", Tr._("Settings"));
        Assert.True(Tr.TranslateMarkup("\n    Start   game\n", out var markup));
        Assert.Equal(" Empezar partida ", markup);
        Assert.Contains("placeholder=\"Nombre del jugador\"", RmlLocalization.PrepareDocument(File.ReadAllText(P("Content/UI/menu.rml"))),
            StringComparison.Ordinal);
        Tr.SetLocale("qps");
        Assert.Equal("[Šéţţîñĝš ~~]", Tr._("Settings"));
        Assert.True(Tr.TranslateMarkup("Player name", out markup));
        Assert.StartsWith("[Þļåýéŕ ñåṁé", markup, StringComparison.Ordinal);

        // 8. Messages that leave the source become obsolete; translations survive a re-extraction.
        File.WriteAllText(P("Content/UI/menu.rml"), "<rml><body><p>Settings</p><p>Brand new</p></body></rml>");
        Assert.Equal(0, Run([
            "extract", "-o", Pot, "--root", _project, "--rml", P("Content/UI"), "--scenes", P("Content/Scenes"), .. include]));
        Assert.Equal(0, Run("update", "--pot", Pot, "--dir", LocaleDir));
        var updated = PoParser.ParseFile(esPath);
        Assert.Equal("Ajustes", updated.ByKey()["Settings"].Translations[0]);
        Assert.Equal(string.Empty, updated.ByKey()["Brand new"].Translations[0]);
        Assert.Contains(updated.Messages, m => m.Obsolete && m.Id == "Start game" && m.Translations[0] == "Empezar partida");

        // 9. The committed .mo is now stale ("Start game" became obsolete): check fails until it is recompiled.
        Assert.Equal(1, Run("check", "--dir", LocaleDir));
        Assert.Contains("stale", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, Run("compile", "--dir", LocaleDir));
        Assert.Equal(0, Run("check", "--dir", LocaleDir));
    }

    [Fact]
    public void CompileRejectsBrokenPlaceholders()
    {
        var po = P("broken.po");
        File.WriteAllText(po, """
            msgid ""
            msgstr "Content-Type: text/plain; charset=UTF-8\n"

            #, csharp-format
            msgid "Score: {0}"
            msgstr "Puntos: {1}"
            """);
        Assert.Equal(1, Run("compile", po, "-o", P("broken.mo")));
        Assert.Contains("broken.po: error: line 5: \"Score: {0}\": msgstr uses {1}", _error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(P("broken.mo")));

        File.WriteAllText(po, """
            msgid ""
            msgstr "Content-Type: text/plain; charset=UTF-8\n"

            #, csharp-format
            msgid "Score: {0}"
            msgstr "Puntos"
            """);
        Assert.Equal(0, Run("compile", po, "-o", P("warn.mo")));             // a dropped argument only warns...
        Assert.Equal(1, Run("compile", po, "-o", P("warn.mo"), "--strict")); // ...unless --strict
    }

    [Fact]
    public void UsageErrorsExitWithTwo()
    {
        Assert.Equal(2, Run());
        Assert.Equal(2, Run("frobnicate"));
        Assert.Equal(2, Run("compile", "--dir", LocaleDir, "--bogus", "x"));
        Assert.Equal(2, Run("extract", "-o", Pot));
        Assert.Equal(0, Run("help"));
        Assert.Contains("pseudo", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, Run("compile", P("missing.po")));
    }

    /// <summary>The built tool runs as its own process (what MSBuild and the justfile invoke).</summary>
    [Fact]
    public void ToolExecutableRuns()
    {
        var tool = Path.Combine(AppContext.BaseDirectory, "mf-l10n.dll");
        Assert.True(File.Exists(tool), tool);
        var po = P("one.po");
        File.WriteAllText(po, "msgid \"\"\nmsgstr \"Content-Type: text/plain; charset=UTF-8\\n\"\n\nmsgid \"Yes\"\nmsgstr \"Sí\"\n");

        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { tool, "compile", po, "-o", P("one.mo") })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output);
        Assert.Equal("Sí", MoFormat.Read(File.ReadAllBytes(P("one.mo"))).Single(m => m.Key == "Yes").Translations[0]);
    }

    private static void Translate(PoCatalog catalog, string id, string translation)
    {
        var entry = catalog.ByKey()[id];
        entry.Translations.Clear();
        entry.Translations.Add(translation);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
