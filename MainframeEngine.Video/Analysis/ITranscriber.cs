using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Analysis;

/// <summary>
/// Phase 2 seam: local speech-to-text producing word-level timestamps. Implementations run entirely
/// on the user's machine (Whisper.net, Vosk). Nothing here may upload audio anywhere.
/// </summary>
public interface ITranscriber
{
    /// <summary>Transcribes the audio track of <paramref name="mediaPath"/> into timestamped words.</summary>
    /// <param name="mediaPath">Absolute path to the source recording. Opened read-only.</param>
    /// <param name="progress">Fraction complete in <c>[0, 1]</c>.</param>
    /// <param name="cancellationToken">Cancels the transcription.</param>
    Task<IReadOnlyList<Word>> TranscribeAsync(
        string mediaPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}
