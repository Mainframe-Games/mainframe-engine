using MainframeEngine.Video.Analysis;

namespace MainframeEngine.Video.Tests.Analysis;

public sealed class WordAssemblerTests
{
    private static TokenTiming T(string text, double start, double end, float p = 0.9f) => new(text, start, end, p);

    [Fact]
    public void ASpacePrefixedTokenStartsANewWord()
    {
        var words = WordAssembler.FromTokens([T(" Hello", 0.0, 0.3), T(" world", 0.4, 0.8)]);

        Assert.Equal([("Hello", 0.0, 0.3), ("world", 0.4, 0.8)], words.Select(w => (w.Text, w.Start, w.End)));
    }

    [Fact]
    public void TokensWithoutALeadingSpaceContinueTheCurrentWord()
    {
        var words = WordAssembler.FromTokens([T(" un", 0.0, 0.1), T("believ", 0.1, 0.3), T("able", 0.3, 0.5)]);

        var word = Assert.Single(words);
        Assert.Equal("unbelievable", word.Text);
        Assert.Equal(0.0, word.Start);
        Assert.Equal(0.5, word.End);
    }

    [Fact]
    public void PunctuationOnlyTokensAttachToTheCurrentWordEvenWithALeadingSpace()
    {
        var words = WordAssembler.FromTokens([T(" Hello", 0.0, 0.3), T(",", 0.3, 0.3), T(" .", 0.3, 0.35), T(" world", 0.4, 0.8)]);

        Assert.Equal(["Hello,.", "world"], words.Select(w => w.Text));
    }

    [Fact]
    public void ControlTokensAndBlankTokensAreSkipped()
    {
        var words = WordAssembler.FromTokens([T("[_BEG_]", 0.0, 0.0), T(" um", 0.5, 0.7), T("[_TT_35]", 0.7, 0.7), T("", 0.7, 0.7), T("  ", 0.8, 0.8)]);

        Assert.Equal("um", Assert.Single(words).Text);
    }

    [Fact]
    public void TheFirstTokenStartsAWordEvenWithoutASpace()
    {
        var words = WordAssembler.FromTokens([T("Hi", 0.0, 0.2)]);

        Assert.Equal("Hi", Assert.Single(words).Text);
    }

    [Fact]
    public void ConfidenceIsTheMeanTokenProbability()
    {
        var words = WordAssembler.FromTokens([T(" ab", 0.0, 0.1, 0.8f), T("cd", 0.1, 0.2, 0.6f)]);

        Assert.Equal(0.7, words[0].Confidence, 1e-6);
    }

    [Fact]
    public void AWordWithNoDurationGetsTenMilliseconds()
    {
        var words = WordAssembler.FromTokens([T(" x", 1.0, 1.0)]);

        Assert.Equal(1.01, words[0].End, 1e-9);
    }

    [Fact]
    public void EmptyInputGivesNoWords()
    {
        Assert.Empty(WordAssembler.FromTokens([]));
    }

    [Fact]
    public void AWordTakesTheAnchorOfItsFirstToken()
    {
        var words = WordAssembler.FromTokens([
            new TokenTiming(" hel", 0.0, 0.2, 0.9f, Anchor: 0.12),
            new TokenTiming("lo", 0.2, 0.3, 0.9f, Anchor: 0.25),
            new TokenTiming(" world", 0.4, 0.8, 0.9f),
        ]);

        Assert.Equal([(double?)0.12, null], words.Select(w => w.Anchor));
    }

    [Fact]
    public void WhisperSpecialTokensAreSkipped()
    {
        var words = WordAssembler.FromTokens([T("<|en|>", 0.0, 0.0), T("<|transcribe|>", 0.0, 0.0), T(" hello", 0.1, 0.4), T("<|endoftext|>", 0.4, 0.4)]);

        Assert.Equal("hello", Assert.Single(words).Text);
    }
}
