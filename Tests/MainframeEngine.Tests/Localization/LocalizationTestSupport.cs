using System.Text;
using MainframeEngine.L10n.Gettext;
using MainframeEngine.Localization;

namespace MainframeEngine.Tests.Localization;

/// <summary>A node with player-facing text, as games write them (scene-translation tests, extraction fixtures).</summary>
public sealed class LocalizedLabel : Node
{
    [Export(Translatable = true)]
    public string Text { get; set; } = string.Empty;

    [Export(Translatable = true)]
    public string[] Lines { get; set; } = [];

    [Export]
    public string Key { get; set; } = string.Empty;

    [Export]
    public DialogueLine? Line { get; set; }

    /// <summary>What the label shows: <see cref="Text"/> through <see cref="Node.Atr"/>.</summary>
    public string Displayed { get; private set; } = string.Empty;

    public int LocaleChanges { get; private set; }

    protected override void OnReady() => Displayed = Atr(Text);

    protected override void OnLocaleChanged()
    {
        LocaleChanges++;
        Displayed = Atr(Text);
    }
}

/// <summary>A resource with a translatable string.</summary>
public sealed class DialogueLine : Resource
{
    [Export(Translatable = true)]
    public string Text { get; set; } = string.Empty;

    [Export]
    public string Speaker { get; set; } = string.Empty;
}

/// <summary>
/// Localization tests share <see cref="Tr"/>'s process-wide state, so they run one at a time.
/// </summary>
[CollectionDefinition(nameof(LocalizationState), DisableParallelization = true)]
public sealed class LocalizationState;

/// <summary>Temporary locale folders with catalogs compiled by mf-l10n's writer.</summary>
internal sealed class LocaleFixture : IDisposable
{
    public LocaleFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), "mf-l10n-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    /// <summary>Compiles <paramref name="po"/> (a .po body without header) to <c>{locale}/LC_MESSAGES/messages.mo</c>.</summary>
    public string AddCatalog(string locale, string pluralForms, string po)
    {
        var text = new StringBuilder()
            .Append("msgid \"\"\nmsgstr \"\"\n")
            .Append("\"Language: ").Append(locale).Append("\\n\"\n")
            .Append("\"Content-Type: text/plain; charset=UTF-8\\n\"\n")
            .Append("\"Plural-Forms: ").Append(pluralForms).Append("\\n\"\n\n")
            .Append(po)
            .ToString();
        var path = Path.Combine(Directory, locale, "LC_MESSAGES", "messages.mo");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, MoFormat.Write(PoParser.Parse(text)));
        return path;
    }

    /// <summary>Points <see cref="Tr"/> at this folder and selects <paramref name="locale"/>.</summary>
    public void Use(string locale, IReadOnlyList<string>? fallbacks = null) =>
        Tr.Configure(new LocalizationOptions
        {
            LocaleDirectory = Directory,
            FallbackLocales = fallbacks ?? [],
            LogMissingTranslations = false,
        }, locale);

    public void Dispose()
    {
        Tr.ResetForTests();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temp folder left behind is harmless.
        }
    }

    public const string OneOther = "nplurals=2; plural=(n != 1);";
    public const string Polish = "nplurals=3; plural=(n==1 ? 0 : n%10>=2 && n%10<=4 && (n%100<10 || n%100>=20) ? 1 : 2);";
    public const string Russian = "nplurals=3; plural=(n%10==1 && n%100!=11 ? 0 : n%10>=2 && n%10<=4 && (n%100<10 || n%100>=20) ? 1 : 2);";

    /// <summary>The fixture project copied to the test output.</summary>
    public static string FixtureProject => Path.Combine(AppContext.BaseDirectory, "Localization", "Fixtures", "Project");
}
