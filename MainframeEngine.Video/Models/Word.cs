namespace MainframeEngine.Video.Models;

/// <summary>
/// A single transcribed word with its timing. Produced locally by an <c>ITranscriber</c> in
/// Phase 2. Only text and timestamps ever leave the machine, never audio.
/// </summary>
/// <param name="Text">The word as transcribed, including disfluencies such as "um".</param>
/// <param name="Start">Start time in seconds.</param>
/// <param name="End">End time in seconds.</param>
/// <param name="Confidence">Recogniser confidence in <c>[0, 1]</c>.</param>
/// <param name="Anchor">
/// A point in seconds that lies inside the word's spoken audio, from the recogniser's alignment
/// (Whisper's DTW timestamp of the first token). More trustworthy than <paramref name="Start"/>
/// and <paramref name="End"/>, which are heuristic and often miss short words. Null when the
/// recogniser did not provide one.
/// </param>
public sealed record Word(string Text, double Start, double End, double Confidence, double? Anchor = null);
