using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Detection;

/// <summary>
/// Finds filler words ("um", "uh", …) in a transcript by exact match against a word list. Pure
/// text matching: context-dependent fillers such as "like" or "so" are deliberately not in the
/// default list, because deciding those needs the surrounding sentence (Phase 3).
/// </summary>
public static class FillerDetector
{
    public static IReadOnlyList<string> DefaultWords { get; } =
        ["um", "umm", "uh", "uhh", "er", "erm", "ah", "hmm", "hm", "mm"];

    /// <summary>Lower-cases and strips leading and trailing punctuation so "Um," matches "um". Inner hyphens survive.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var span = text.AsSpan().Trim();
        while (span.Length > 0 && char.IsPunctuation(span[0]))
        {
            span = span[1..];
        }

        while (span.Length > 0 && char.IsPunctuation(span[^1]))
        {
            span = span[..^1];
        }

        return span.ToString().ToLowerInvariant();
    }

    /// <summary>The transcript words whose normalised text is in <paramref name="fillerWords"/>, in timeline order.</summary>
    public static IReadOnlyList<Word> Find(IReadOnlyList<Word> transcript, IEnumerable<string> fillerWords)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(fillerWords);

        var set = new HashSet<string>(
            fillerWords.Select(Normalize).Where(w => w.Length > 0),
            StringComparer.Ordinal);
        if (set.Count == 0)
        {
            return [];
        }

        return transcript
            .Where(w => set.Contains(Normalize(w.Text)))
            .OrderBy(w => w.Start)
            .ToList();
    }
}
