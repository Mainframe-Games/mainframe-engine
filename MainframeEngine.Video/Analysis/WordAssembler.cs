using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Analysis;

/// <summary>
/// Groups Whisper's sub-word tokens into words. Whisper marks a word boundary with a leading
/// space on the first token of the word; punctuation-only tokens belong to the word before them;
/// control tokens such as <c>[_BEG_]</c> and special tokens such as <c>&lt;|endoftext|&gt;</c> carry no text.
/// </summary>
public static class WordAssembler
{
    /// <summary>Shortest word the timeline will accept; a zero-length word cannot be cut.</summary>
    public const double MinWordSeconds = 0.01;

    /// <summary>Groups Whisper's sub-word tokens into words.</summary>
    /// <param name="tokens">The tokens to assemble.</param>
    /// <returns>A list of words assembled from the tokens.</returns>
    public static IReadOnlyList<Word> FromTokens(IEnumerable<TokenTiming> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var words = new List<Word>();
        var text = new System.Text.StringBuilder();
        var start = 0.0;
        var end = 0.0;
        var probabilitySum = 0.0;
        var tokenCount = 0;
        double? anchor = null;

        foreach (var token in tokens)
        {
            var trimmed = token.Text.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("[_", StringComparison.Ordinal) || trimmed.StartsWith("<|", StringComparison.Ordinal))
            {
                continue;
            }
            var punctuationOnly = trimmed.All(char.IsPunctuation);
            var startsWord = tokenCount > 0 && char.IsWhiteSpace(token.Text[0]) && !punctuationOnly;
            if (startsWord)
            {
                Flush();
            }

            if (tokenCount == 0)
            {
                start = token.Start;
                end = token.End;
                anchor = token.Anchor;
            }

            text.Append(trimmed);
            end = Math.Max(end, token.End);
            probabilitySum += token.Probability;
            tokenCount++;
        }

        Flush();
        return words;

        void Flush()
        {
            if (tokenCount == 0)
            {
                return;
            }

            var wordText = text.ToString();
            if (wordText.Length > 0)
            {
                var wordEnd = end > start ? end : start + MinWordSeconds;
                words.Add(new Word(wordText, start, wordEnd, probabilitySum / tokenCount, anchor));
            }

            text.Clear();
            probabilitySum = 0;
            tokenCount = 0;
            anchor = null;
        }
    }
}
