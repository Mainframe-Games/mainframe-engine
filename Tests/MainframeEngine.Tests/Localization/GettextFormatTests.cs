using System.Diagnostics;
using System.Globalization;
using System.Text;
using GetText;
using GetText.Loaders;
using MainframeEngine.L10n;
using MainframeEngine.L10n.Gettext;

namespace MainframeEngine.Tests.Localization;

/// <summary>
/// mf-l10n's gettext formats: <c>.po</c> parsing and writing, the <c>.mo</c> writer (GNU layout, hash table, byte
/// identical to msgfmt when it is installed) and reader, placeholder checks and the pseudo-locale.
/// </summary>
public sealed class GettextFormatTests
{
    private const string SamplePo = """
        # Polish translation.
        #
        msgid ""
        msgstr ""
        "Project-Id-Version: Test\n"
        "Language: pl\n"
        "MIME-Version: 1.0\n"
        "Content-Type: text/plain; charset=UTF-8\n"
        "Content-Transfer-Encoding: 8bit\n"
        "Plural-Forms: nplurals=3; plural=(n==1 ? 0 : n%10>=2 && n%10<=4 && (n%100<10 || n%100>=20) ? 1 : 2);\n"

        # A translator note.
        #. An extracted comment.
        #: Src/Hud.cs:10 Src/Menu.cs:4
        #, csharp-format
        msgid "Score: {0}"
        msgstr "Wynik: {0}"

        msgctxt "menu"
        msgid "Open"
        msgstr "Otwórz"

        #, csharp-format
        msgid "{0} file"
        msgid_plural "{0} files"
        msgstr[0] "{0} plik"
        msgstr[1] "{0} pliki"
        msgstr[2] "{0} plików"

        msgid ""
        "Line one\n"
        "Line \"two\"\t\\ end"
        msgstr "Linia \101\x42"

        msgid "Untranslated"
        msgstr ""

        #, fuzzy
        msgid "Fuzzy"
        msgstr "Rozmyty"

        msgid "Żółw 🐢"
        msgstr "Turtle 🐢"

        #~ msgid "Old"
        #~ msgstr "Stary"
        """;

    [Fact]
    public void ParsesEntriesCommentsAndEscapes()
    {
        var catalog = PoParser.Parse(SamplePo);
        Assert.Equal(["Polish translation.", ""], catalog.FileComments);
        Assert.Equal("pl", catalog.Language);
        Assert.Equal(3, catalog.PluralCount);
        var messages = catalog.Messages.ToList();
        Assert.Equal(8, messages.Count);

        var score = messages[0];
        Assert.Equal(["A translator note."], score.TranslatorComments);
        Assert.Equal(["An extracted comment."], score.ExtractedComments);
        Assert.Equal(["Src/Hud.cs:10", "Src/Menu.cs:4"], score.References);
        Assert.Equal(["csharp-format"], score.Flags);
        Assert.Equal(["Wynik: {0}"], score.Translations);

        Assert.Equal("menu\u0004Open", messages[1].Key);
        Assert.Equal("{0} files", messages[2].IdPlural);
        Assert.Equal(3, messages[2].Translations.Count);
        Assert.Equal("Line one\nLine \"two\"\t\\ end", messages[3].Id);
        Assert.Equal("Linia AB", messages[3].Translations[0]);
        Assert.False(messages[4].HasTranslation);
        Assert.True(messages[5].IsFuzzy);
        Assert.Equal("Żółw 🐢", messages[6].Id);
        Assert.True(messages[7].Obsolete);
        Assert.Equal("Stary", messages[7].Translations[0]);
    }

    [Fact]
    public void WriterRoundTripsTheParser()
    {
        var catalog = PoParser.Parse(SamplePo);
        var written = PoWriter.Write(catalog);
        var again = PoParser.Parse(written);
        Assert.Equal(written, PoWriter.Write(again));
        Assert.Equal(catalog.Messages.Select(m => (m.Key, m.IdPlural, string.Join('|', m.Translations), m.Obsolete)),
            again.Messages.Select(m => (m.Key, m.IdPlural, string.Join('|', m.Translations), m.Obsolete)));
        Assert.Contains("msgid \"\"\n\"Line one\\n\"\n\"Line \\\"two\\\"\\t\\\\ end\"\n", written, StringComparison.Ordinal);
        Assert.Contains("#~ msgid \"Old\"\n#~ msgstr \"Stary\"\n", written, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("msgid \"a\"\n", "has no msgstr")]
    [InlineData("msgid \"a\"\nmsgstr \"b\n", "unterminated string")]
    [InlineData("msgid \"a\"\nmsgstr[0] \"b\"\n", "msgstr[n] without msgid_plural")]
    [InlineData("msgid \"a\"\nmsgid_plural \"b\"\nmsgstr[1] \"c\"\n", "expected msgstr[0]")]
    [InlineData("msgid \"a\"\nmsgstr \"\\q\"\n", "unknown escape")]
    [InlineData("bogus \"a\"\n", "unknown keyword")]
    [InlineData("msgid \"\"\nmsgstr \"Content-Type: text/plain; charset=ISO-8859-1\\n\"\n", "not supported")]
    public void ReportsSyntaxErrorsWithLineNumbers(string po, string message)
    {
        var e = Assert.Throws<PoFormatException>(() => PoParser.Parse(po, "x.po"));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
        Assert.StartsWith("x.po:", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MoWriterAndReaderRoundTrip()
    {
        var catalog = PoParser.Parse(SamplePo);
        var bytes = MoFormat.Write(catalog);

        // GNU header: magic, revision 0, N, table offsets, hash table size/offset.
        Assert.Equal(MoFormat.Magic, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal(0u, BitConverter.ToUInt32(bytes, 4));
        Assert.Equal(6u, BitConverter.ToUInt32(bytes, 8)); // header + 5 translated, non-fuzzy, non-obsolete messages
        Assert.Equal(28u, BitConverter.ToUInt32(bytes, 12));
        Assert.Equal((uint)MoFormat.HashTableSize(6), BitConverter.ToUInt32(bytes, 20));

        var messages = MoFormat.Read(bytes); // also checks every message is found through the hash table
        Assert.Equal(["", "Line one\nLine \"two\"\t\\ end", "Score: {0}", "menu\u0004Open", "{0} file", "Żółw 🐢"],
            messages.Select(m => m.Key));
        Assert.Equal(["{0} plik", "{0} pliki", "{0} plików"], messages.Single(m => m.Key == "{0} file").Translations);
        Assert.Equal("{0} files", messages.Single(m => m.Key == "{0} file").IdPlural);
        Assert.Contains("Plural-Forms:", messages[0].Translations[0], StringComparison.Ordinal);

        Assert.Equal(7, MoFormat.Read(MoFormat.Write(catalog, useFuzzy: true)).Count);
    }

    [Fact]
    public void GetTextNetLoadsTheCompiledCatalog()
    {
        var bytes = MoFormat.Write(PoParser.Parse(SamplePo));
        using var stream = new MemoryStream(bytes);
        var catalog = new Catalog(new MoAstPluralLoader(stream), CultureInfo.GetCultureInfo("pl"));
        Assert.Equal("Wynik: 7", catalog.GetString("Score: {0}", 7));
        Assert.Equal("Otwórz", catalog.GetParticularString("menu", "Open"));
        Assert.Equal("22 pliki", catalog.GetPluralString("{0} file", "{0} files", 22));
        Assert.Equal("Turtle 🐢", catalog.GetString("Żółw 🐢"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 5)]
    [InlineData(10, 13)]
    [InlineData(100, 137)]
    public void HashTableSizesMatchGettext(int messages, int size) => Assert.Equal(size, MoFormat.HashTableSize(messages));

    [Fact]
    public void HashPjwMatchesGettext()
    {
        Assert.Equal(0u, MoFormat.HashPjw([]));
        Assert.Equal((uint)'a', MoFormat.HashPjw("a"u8));
        Assert.Equal(('a' << 4) + (uint)'b', MoFormat.HashPjw("ab"u8));
        Assert.Equal(0x089ABAA8u, MoFormat.HashPjw("abcdefgh"u8)); // folds the high nibble back in
        Assert.Equal(MoFormat.HashPjw("Hello"u8), MoFormat.HashPjw("Hello\0ignored"u8)); // stops at NUL
    }

    /// <summary>
    /// The strongest check of the .mo writer: GNU msgfmt (when installed, as on dev machines and Linux CI) must
    /// produce exactly the same bytes for catalogs of many sizes (hash collisions, contexts, plurals, UTF-8).
    /// </summary>
    [Fact]
    public void MatchesGnuMsgfmtByteForByte()
    {
        if (!L10nCli.MsgfmtAvailable())
            Assert.Skip("GNU msgfmt is not on PATH");

        var directory = Path.Combine(Path.GetTempPath(), "mf-l10n-msgfmt", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var samples = new List<string> { SamplePo };
            foreach (var count in new[] { 0, 1, 2, 3, 7, 40, 257 })
                samples.Add(GeneratedPo(count));

            for (var i = 0; i < samples.Count; i++)
            {
                var po = Path.Combine(directory, $"sample{i}.po");
                File.WriteAllText(po, samples[i], new UTF8Encoding(false));
                var ours = MoFormat.Write(PoParser.ParseFile(po));
                using var log = new StringWriter();
                Assert.True(L10nCli.CompareWithMsgfmt(po, ours, log) == 0, $"sample {i}: {log}");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string GeneratedPo(int count)
    {
        var sb = new StringBuilder("msgid \"\"\nmsgstr \"Content-Type: text/plain; charset=UTF-8\\nPlural-Forms: nplurals=2; plural=(n != 1);\\n\"\n\n");
        for (var i = 0; i < count; i++)
        {
            if (i % 7 == 3)
                sb.Append(CultureInfo.InvariantCulture, $"msgctxt \"ctx{i % 3}\"\n");
            if (i % 5 == 1)
            {
                sb.Append(CultureInfo.InvariantCulture, $"msgid \"item {i} ñ\"\nmsgid_plural \"items {i}\"\nmsgstr[0] \"objeto {i}\"\nmsgstr[1] \"objetos {i}\"\n\n");
            }
            else
            {
                sb.Append(CultureInfo.InvariantCulture, $"msgid \"Message number {i} — {new string('x', i % 13)}\"\nmsgstr \"Mensaje {i}\"\n\n");
            }
        }

        return sb.ToString();
    }

    [Theory]
    [InlineData("Score: {0}", "Wynik: {0}", "csharp-format", 0, 0)]
    [InlineData("Score: {0}", "Wynik: {1}", "csharp-format", 1, 1)]   // unknown index (and {0} unused: warning)
    [InlineData("Score: {0}", "Wynik: {0", "csharp-format", 1, 0)]    // malformed
    [InlineData("Score: {0}", "Wynik", "csharp-format", 0, 1)]        // dropped argument: warning only
    [InlineData("{0} of {1}", "{1} z {0}", "csharp-format", 0, 0)]    // reordered
    [InlineData("Hi {{ name }}", "Cześć {{name}}", null, 0, 0)]       // RML data expression, spacing differs
    [InlineData("Hi {{ name }}", "Cześć {{ nick }}", null, 1, 0)]     // RML data expression changed
    [InlineData("Use {braces}", "Użyj {nawiasów}", null, 0, 0)]       // not a format: not checked
    public void PlaceholderChecks(string id, string translation, string? flag, int errors, int warnings)
    {
        var entry = new PoEntry { Id = id, Line = 3 };
        entry.Translations.Add(translation);
        if (flag is not null)
            entry.Flags.Add(flag);
        var issues = Placeholders.Check(entry, 2);
        Assert.Equal(errors, issues.Count(i => i.IsError));
        Assert.Equal(warnings, issues.Count(i => !i.IsError));
    }

    [Fact]
    public void PluralCountMustMatchTheHeader()
    {
        var entry = new PoEntry { Id = "{0} file", IdPlural = "{0} files" };
        entry.Translations.AddRange(["{0} plik", "{0} pliki"]);
        Assert.Contains(Placeholders.Check(entry, 3), i => i.IsError && i.Message.Contains("plural forms", StringComparison.Ordinal));
        Assert.Empty(Placeholders.Check(entry, 2));
    }

    [Theory]
    [InlineData("Settings", "full", "[Šéţţîñĝš ~~]")]
    [InlineData("Settings", "latin1", "[Séttîñgs ~~]")]
    [InlineData("Score: {0}", "full", "[Šçöŕé: {0} ~]")]
    [InlineData("{0:N2} of {1,-5}", "full", "[{0:N2} öƒ {1,-5} ~]")]
    [InlineData("Hi {{ name }}!", "full", "[Ĥî {{ name }}! ~]")]
    [InlineData("  padded\n", "full", "  [þåððéð ~]\n")]
    [InlineData("100%", "full", "[100% ~]")]
    [InlineData("%s left", "full", "[%s ļéƒţ ~]")]
    [InlineData("", "full", "")]
    public void PseudoLocalizationAccentsExpandsAndBrackets(string source, string charset, string expected) =>
        Assert.Equal(expected, PseudoLocalizer.Transform(source, PseudoLocalizer.ParseCharset(charset)));

    [Fact]
    public void PseudoTablesCoverEveryAsciiLetter()
    {
        var full = PseudoLocalizer.Transform("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", PseudoCharset.Full, 0);
        Assert.Equal(54, full.Length); // 52 letters + brackets: one character each
        Assert.DoesNotContain(full[1..^1], char.IsAscii);
        var latin1 = PseudoLocalizer.Transform("Hello World", PseudoCharset.Latin1, 0);
        Assert.All(latin1, c => Assert.True(c <= '\u00FF'));
        Assert.Equal(15, PseudoLocalizer.Transform("aaaaaaaaaa", PseudoCharset.Full).Length); // 10 + 30% padding (" ~~") + brackets
    }
}
