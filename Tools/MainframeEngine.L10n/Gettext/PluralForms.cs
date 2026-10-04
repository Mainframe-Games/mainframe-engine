using System.Globalization;
using MainframeEngine.Localization;

namespace MainframeEngine.L10n.Gettext;

/// <summary>
/// <c>Plural-Forms</c> headers for new catalogs (<c>mf-l10n update --create</c>), from the GNU gettext manual and
/// Unicode CLDR. Translators can edit the header afterwards; the runtime evaluates whatever the catalog says.
/// </summary>
internal static class PluralForms
{
    private const string OneOther = "nplurals=2; plural=(n != 1);";
    private const string ZeroOneOther = "nplurals=2; plural=(n > 1);";
    private const string Single = "nplurals=1; plural=0;";
    private const string Slavic = "nplurals=3; plural=(n%10==1 && n%100!=11 ? 0 : n%10>=2 && n%10<=4 && (n%100<10 || n%100>=20) ? 1 : 2);";

    private static readonly Dictionary<string, string> ByLanguage = new(StringComparer.Ordinal)
    {
        ["en"] = OneOther,
        ["de"] = OneOther,
        ["nl"] = OneOther,
        ["sv"] = OneOther,
        ["da"] = OneOther,
        ["nb"] = OneOther,
        ["no"] = OneOther,
        ["fi"] = OneOther,
        ["et"] = OneOther,
        ["es"] = OneOther,
        ["it"] = OneOther,
        ["pt"] = OneOther,
        ["ca"] = OneOther,
        ["el"] = OneOther,
        ["hu"] = OneOther,
        ["bg"] = OneOther,
        ["tr"] = OneOther,
        ["he"] = OneOther,
        ["fr"] = ZeroOneOther,
        ["pt_BR"] = ZeroOneOther,
        ["ja"] = Single,
        ["zh"] = Single,
        ["ko"] = Single,
        ["vi"] = Single,
        ["th"] = Single,
        ["id"] = Single,
        ["ru"] = Slavic,
        ["uk"] = Slavic,
        ["be"] = Slavic,
        ["sr"] = Slavic,
        ["hr"] = Slavic,
        ["bs"] = Slavic,
        ["pl"] = "nplurals=3; plural=(n==1 ? 0 : n%10>=2 && n%10<=4 && (n%100<10 || n%100>=20) ? 1 : 2);",
        ["cs"] = "nplurals=3; plural=(n==1) ? 0 : (n>=2 && n<=4) ? 1 : 2;",
        ["sk"] = "nplurals=3; plural=(n==1) ? 0 : (n>=2 && n<=4) ? 1 : 2;",
        ["lt"] = "nplurals=3; plural=(n%10==1 && n%100!=11 ? 0 : n%10>=2 && (n%100<10 || n%100>=20) ? 1 : 2);",
        ["lv"] = "nplurals=3; plural=(n%10==1 && n%100!=11 ? 0 : n != 0 ? 1 : 2);",
        ["ro"] = "nplurals=3; plural=(n==1 ? 0 : (n==0 || (n%100 > 0 && n%100 < 20)) ? 1 : 2);",
        ["sl"] = "nplurals=4; plural=(n%100==1 ? 0 : n%100==2 ? 1 : n%100==3 || n%100==4 ? 2 : 3);",
        ["ga"] = "nplurals=5; plural=(n==1 ? 0 : n==2 ? 1 : n<7 ? 2 : n<11 ? 3 : 4);",
        ["ar"] = "nplurals=6; plural=(n==0 ? 0 : n==1 ? 1 : n==2 ? 2 : n%100>=3 && n%100<=10 ? 3 : n%100>=11 ? 4 : 5);",
        [LocaleId.Pseudo] = OneOther,
    };

    /// <summary>The header for a locale (exact, then its language), or the two-form English rule.</summary>
    public static string For(string locale)
    {
        var normalized = LocaleId.Normalize(locale);
        return ByLanguage.GetValueOrDefault(normalized) ?? ByLanguage.GetValueOrDefault(LocaleId.Language(normalized)) ?? OneOther;
    }

    /// <summary>nplurals of a <c>Plural-Forms</c> value.</summary>
    public static int Count(string pluralForms)
    {
        var at = pluralForms.IndexOf("nplurals=", StringComparison.Ordinal);
        if (at < 0)
            return 2;
        var end = pluralForms.IndexOf(';', at);
        return int.TryParse(pluralForms.AsSpan(at + 9, (end < 0 ? pluralForms.Length : end) - at - 9).Trim(), NumberStyles.None,
            CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 2;
    }
}
