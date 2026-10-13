using MainframeEngine.Video.Models;
using MainframeEngine.Video.Media;
using Whisper.net;

namespace MainframeEngine.Video.Analysis;

/// <summary>
/// Local speech-to-text with Whisper.net on the CPU. Decodes the audio with ffmpeg, runs the
/// model with token timestamps, and assembles words. Whisper was trained on transcripts with the
/// disfluencies removed, so left alone it drops most "um"s; the initial prompt is written the way
/// a transcript with fillers looks, and is carried into every window, to steer it back.
/// </summary>
public sealed class WhisperTranscriber : ITranscriber
{
    public const string DisfluentPrompt =
        "Um, so, uh, I was going to, um, show you this. Er, hmm, let me, uh, think. Ah, okay, so, um, here we go.";

    /// <summary>Share of the progress bar given to audio decoding; the rest is recognition.</summary>
    private const double DecodeShare = 0.15;

    private readonly FfmpegLocation _ffmpeg;
    private readonly string _modelPath;
    private readonly WhisperModel _model;

    /// <param name="ffmpeg">Used to decode the audio.</param>
    /// <param name="modelPath">A downloaded ggml model file; see <c>ModelStore</c>.</param>
    /// <param name="model">Which model the file holds, so the matching DTW alignment heads are used.</param>
    public WhisperTranscriber(FfmpegLocation ffmpeg, string modelPath, WhisperModel model)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        _ffmpeg = ffmpeg;
        _modelPath = modelPath;
        _model = model;
    }

    /// <summary>The alignment heads preset shipped in whisper.cpp for each model we offer.</summary>
    public static WhisperAlignmentHeadsPreset AlignmentHeadsFor(WhisperModel model) => model switch
    {
        WhisperModel.TinyEn => WhisperAlignmentHeadsPreset.TinyEn,
        WhisperModel.BaseEn => WhisperAlignmentHeadsPreset.BaseEn,
        WhisperModel.SmallEn => WhisperAlignmentHeadsPreset.SmallEn,
        WhisperModel.MediumEn => WhisperAlignmentHeadsPreset.MediumEn,
        _ => WhisperAlignmentHeadsPreset.None,
    };

    public async Task<IReadOnlyList<Word>> TranscribeAsync(string mediaPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);

        var samples = await new PcmExtractor(_ffmpeg)
            .ExtractAsync(mediaPath, 0, progress is null ? null : new ScaledProgress(progress, 0, DecodeShare), cancellationToken)
            .ConfigureAwait(false);

        // whisper.cpp is synchronous and CPU-bound; keep it off the caller's thread.
        var words = await Task.Run(() => RecognizeAsync(samples, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        progress?.Report(1.0);
        return words;
    }

    private async Task<List<Word>> RecognizeAsync(ReadOnlyMemory<float> samples, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var factory = LoadFactory();
        await using var processor = factory.CreateBuilder()
            .WithLanguage("en")
            .WithTokenTimestamps()
            .WithThreads(Math.Clamp(Environment.ProcessorCount, 1, 8))
            .WithPrompt(DisfluentPrompt)
            .WithCarryInitialPrompt(true)
            .WithProgressHandler(percent => progress?.Report(DecodeShare + (1 - DecodeShare) * Math.Clamp(percent, 0, 100) / 100.0))
            .Build();

        var words = new List<Word>();
        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
        {
            if (segment.Tokens is null)
            {
                continue;
            }

            // DtwTimestamp is -1 when alignment is unavailable for a token.
            words.AddRange(WordAssembler.FromTokens(segment.Tokens.Select(t =>
                new TokenTiming(t.Text ?? string.Empty, t.Start / 100.0, t.End / 100.0, t.Probability,
                    t.DtwTimestamp >= 0 ? t.DtwTimestamp / 100.0 : null))));
        }

        return words.OrderBy(w => w.Start).ToList();
    }

    /// <summary>Loads the Whisper model, wrapping any failure in a <see cref="ModelLoadException"/> naming the model path.</summary>
    private WhisperFactory LoadFactory()
    {
        try
        {
            // DTW alignment gives each token an anchor inside its real audio. The heuristic
            // timestamps alone put short fillers on the neighbouring pause more often than not.
            return WhisperFactory.FromPath(_modelPath, new WhisperFactoryOptions
            {
                UseDtwTimeStamps = true,
                HeadsPreset = AlignmentHeadsFor(_model),
            });
        }
        catch (Exception ex)
        {
            throw new ModelLoadException(
                $"The speech model at {_modelPath} could not be loaded: {ex.Message}",
                _modelPath,
                ex);
        }
    }

    /// <summary>Maps a child's <c>[0, 1]</c> onto <c>[offset, offset + share]</c> of the parent's.</summary>
    private sealed class ScaledProgress : IProgress<double>
    {
        private readonly IProgress<double> _parent;
        private readonly double _offset;
        private readonly double _share;

        public ScaledProgress(IProgress<double> parent, double offset, double share)
        {
            _parent = parent;
            _offset = offset;
            _share = share;
        }

        public void Report(double value) => _parent.Report(_offset + _share * Math.Clamp(value, 0, 1));
    }
}
