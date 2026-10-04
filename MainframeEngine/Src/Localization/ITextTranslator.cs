using System.Diagnostics.CodeAnalysis;

namespace MainframeEngine.Localization;

/// <summary>
/// The engine-side contract the game UI (RmlUi, M8) calls for localization. <see cref="TextTranslator.Current"/>
/// forwards to <see cref="Tr"/>; tests and tools can pass their own implementation to the UI server.
/// </summary>
/// <remarks>
/// Wiring (docs/design/localization.md#game-ui-rmlui):
/// <list type="number">
/// <item><b>Document sources</b> — every <c>.rml</c> file the UI file interface serves goes through
/// <see cref="PrepareDocument"/> (attributes translated, <c>no-tr</c> text marked).</item>
/// <item><b>Text nodes</b> — RmlUi's <c>TranslateString(input)</c> callback calls <see cref="TryTranslateMarkup"/> with
/// the UTF-8 input; on true it writes the result and returns 1, otherwise returns 0.</item>
/// <item><b>Locale changes</b> — on <see cref="LocaleChanged"/> the UI server loads the faces of
/// <see cref="FontFallbackTable.ResolveCurrent"/> and reloads its documents (data models stay in C#; call
/// <c>DirtyAll()</c> on models whose strings come from <see cref="Tr"/>).</item>
/// </list>
/// </remarks>
public interface ITextTranslator
{
    /// <summary>The current locale (gettext form).</summary>
    string Locale { get; }

    /// <summary>The lookup chain, most specific first (for font selection).</summary>
    IReadOnlyList<string> LocaleChain { get; }

    /// <summary>Raised after the locale changed.</summary>
    event EventHandler<LocaleChangedEventArgs>? LocaleChanged;

    /// <summary>RmlUi <c>TranslateString</c>: translates one UTF-8 text run; false leaves it unchanged.</summary>
    bool TryTranslateMarkup(ReadOnlySpan<byte> utf8, [NotNullWhen(true)] out string? translated);

    /// <summary>Prepares an RML document source before RmlUi parses it (<see cref="RmlLocalization.PrepareDocument(string)"/>).</summary>
    string PrepareDocument(string rmlSource);
}

/// <summary><see cref="ITextTranslator"/> over the engine's <see cref="Tr"/> catalogs.</summary>
public sealed class TextTranslator : ITextTranslator
{
    private TextTranslator()
    {
    }

    /// <summary>The translator backed by <see cref="Tr"/>.</summary>
    public static TextTranslator Current { get; } = new();

    public string Locale => Tr.CurrentLocale;

    public IReadOnlyList<string> LocaleChain => Tr.LocaleChain;

    public event EventHandler<LocaleChangedEventArgs>? LocaleChanged
    {
        add => Tr.LocaleChanged += value;
        remove => Tr.LocaleChanged -= value;
    }

    public bool TryTranslateMarkup(ReadOnlySpan<byte> utf8, [NotNullWhen(true)] out string? translated) =>
        Tr.TranslateMarkup(utf8, out translated);

    public string PrepareDocument(string rmlSource) => RmlLocalization.PrepareDocument(rmlSource);
}
