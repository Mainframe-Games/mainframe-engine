using System.Globalization;
using Whisper.net.Ggml;

namespace MainframeEngine.Video.Analysis;

/// <summary>The English-only Whisper models the app offers. Larger is slower and more accurate.</summary>
public enum WhisperModel
{
    TinyEn,
    BaseEn,
    SmallEn,
    MediumEn,
}

/// <param name="Model">The enum value this metadata describes.</param>
/// <param name="Key">Stable settings key and Hugging Face name, e.g. <c>base.en</c>.</param>
/// <param name="GgmlType">The Whisper.net GGML model identifier used to fetch this model.</param>
/// <param name="ApproximateBytes">Download size, used only to show a progress fraction.</param>
public sealed record WhisperModelInfo(WhisperModel Model, string Key, GgmlType GgmlType, long ApproximateBytes)
{
    public static IReadOnlyList<WhisperModelInfo> All { get; } =
    [
        new(WhisperModel.TinyEn, "tiny.en", GgmlType.TinyEn, 77_700_000),
        new(WhisperModel.BaseEn, "base.en", GgmlType.BaseEn, 148_000_000),
        new(WhisperModel.SmallEn, "small.en", GgmlType.SmallEn, 487_600_000),
        new(WhisperModel.MediumEn, "medium.en", GgmlType.MediumEn, 1_533_800_000),
    ];

    public static WhisperModelInfo Default => For(WhisperModel.BaseEn);

    public string FileName => $"ggml-{Key}.bin";

    public string DisplayName => string.Create(CultureInfo.InvariantCulture, $"{Key} (~{ApproximateBytes / 1_000_000} MB)");

    public static WhisperModelInfo For(WhisperModel model)
        => All.FirstOrDefault(i => i.Model == model)
           ?? throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown Whisper model.");

    /// <summary>Case-insensitive key lookup. Anything unrecognised is the default, so a hand-edited settings file cannot break the app.</summary>
    public static WhisperModel Parse(string? key)
        => All.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase))?.Model ?? WhisperModel.BaseEn;
}
