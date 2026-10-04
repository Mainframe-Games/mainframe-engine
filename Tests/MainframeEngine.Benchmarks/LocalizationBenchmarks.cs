using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using MainframeEngine.L10n.Gettext;
using MainframeEngine.Localization;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// <see cref="Tr"/> cost (M9): cached lookups must stay allocation-free (hit, miss, context), formatting allocates only
/// its result, and a locale switch (load + merge two catalogs of <see cref="Messages"/> messages each, notify) is
/// measured so UI language menus can budget for it.
/// </summary>
[MemoryDiagnoser]
public class LocalizationBenchmarks : IDisposable
{
    private const int Messages = 1000;
    private string _directory = string.Empty;
    private bool _toggle;

    // Instance fields, as call sites hold them (literals in real code; fields keep the methods non-static here).
    private readonly string _hit = "Message 500";
    private readonly string _miss = "Not in any catalog";
    private readonly string _context = "menu";
    private readonly string _contextHit = "Item 500";
    private readonly string _format = "Score: {0}";
    private readonly string _markup = "  Message   500 ";
    private int _score = 42;

    [GlobalSetup]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "mf-l10n-bench", Guid.NewGuid().ToString("N"));
        WriteCatalog("es", "es");
        WriteCatalog("de", "de");
        Tr.Configure(new LocalizationOptions { LocaleDirectory = _directory, LogMissingTranslations = false }, "es");
    }

    private void WriteCatalog(string locale, string prefix)
    {
        var po = new StringBuilder("msgid \"\"\nmsgstr \"Content-Type: text/plain; charset=UTF-8\\nPlural-Forms: nplurals=2; plural=(n != 1);\\n\"\n\n");
        for (var i = 0; i < Messages; i++)
        {
            po.Append(CultureInfo.InvariantCulture, $"msgid \"Message {i}\"\nmsgstr \"{prefix} {i}\"\n\n");
            po.Append(CultureInfo.InvariantCulture, $"msgctxt \"menu\"\nmsgid \"Item {i}\"\nmsgstr \"{prefix} item {i}\"\n\n");
        }

        po.Append("msgid \"Score: {0}\"\nmsgstr \"Puntos: {0}\"\n\n");
        var path = Path.Combine(_directory, locale, "LC_MESSAGES", "messages.mo");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, MoFormat.Write(PoParser.Parse(po.ToString())));
    }

    [Benchmark]
    public string LookupHit() => Tr._(_hit);

    [Benchmark]
    public string LookupMiss() => Tr._(_miss);

    [Benchmark]
    public string LookupContextHit() => Tr.P(_context, _contextHit);

    [Benchmark]
    public string FormatOneArgument() => Tr._(_format, _score++);

    [Benchmark]
    public bool TranslateMarkupHit() => Tr.TranslateMarkup(_markup, out _);

    /// <summary>Switching between two locales: load, merge and swap the catalogs and raise LocaleChanged.</summary>
    [Benchmark]
    public void SwitchLocale()
    {
        _toggle = !_toggle;
        Tr.SetLocale(_toggle ? "de" : "es");
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        Tr.ResetForTests();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
