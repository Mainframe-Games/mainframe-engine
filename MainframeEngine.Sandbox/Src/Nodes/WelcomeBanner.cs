namespace MainframeEngine.Sandbox;

/// <summary>
/// A scene node with player-facing text (M9): <see cref="Title"/> and <see cref="Hint"/> are saved in
/// <c>Sandbox.mscene</c> as source text, extracted by <c>mf-l10n extract</c> and shown translated. The overlay draws
/// <see cref="DisplayTitle"/>/<see cref="DisplayHint"/>, which are refreshed when the locale changes.
/// </summary>
public sealed class WelcomeBanner : Node
{
    [Export(Translatable = true)]
    public string Title { get; set; } = string.Empty;

    [Export(Translatable = true, Multiline = true)]
    public string Hint { get; set; } = string.Empty;

    /// <summary><see cref="Title"/> in the current locale.</summary>
    public string DisplayTitle { get; private set; } = string.Empty;

    /// <summary><see cref="Hint"/> in the current locale.</summary>
    public string DisplayHint { get; private set; } = string.Empty;

    protected override void OnReady() => Retranslate();

    protected override void OnLocaleChanged() => Retranslate();

    private void Retranslate()
    {
        DisplayTitle = Atr(Title);
        DisplayHint = Atr(Hint);
    }
}
