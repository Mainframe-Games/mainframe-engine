namespace MainframeEngine.Video.Analysis;

/// <summary>A recogniser token with its timing in seconds. Engine-neutral so word assembly can be tested without a model.</summary>
/// <param name="Text">Token text as the engine emitted it, leading space included.</param>
/// <param name="Start">Heuristic start in seconds.</param>
/// <param name="End">Heuristic end in seconds.</param>
/// <param name="Probability">Engine confidence in <c>[0, 1]</c>.</param>
/// <param name="Anchor">Alignment-derived time inside the token's audio, in seconds, when the engine provides one.</param>
public readonly record struct TokenTiming(string Text, double Start, double End, float Probability, double? Anchor = null);
