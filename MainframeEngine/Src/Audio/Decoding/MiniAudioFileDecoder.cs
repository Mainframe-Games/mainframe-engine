using SoundFlow.Backends.MiniAudio;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Metadata;
using SoundFlow.Metadata.Models;
using SoundFlow.Structs;

namespace MainframeEngine.Audio;

/// <summary>
/// MP3 and FLAC through SoundFlow's miniaudio codecs (native). The header is read with SoundFlow's managed
/// metadata reader for the channel count and rate; miniaudio then decodes to float at that format.
/// </summary>
internal sealed class MiniAudioFileDecoder : AudioDecoder
{
    private readonly Stream _stream;
    private readonly ISoundDecoder _decoder;

    public MiniAudioFileDecoder(Stream stream, string path, string format)
    {
        _stream = stream;
        Format = format;
        var info = SoundMetadataReader.Read(stream, new ReadOptions
        {
            ReadTags = false,
            ReadAlbumArt = false,
            ReadCueSheet = false,
            DurationAccuracy = DurationAccuracy.FastEstimate,
        });
        if (!info.IsSuccess || info.Value is null)
            throw new InvalidDataException($"'{path}': cannot read the {format} header ({info.Error?.Message ?? "unknown error"}).");

        SourceChannels = info.Value.ChannelCount;
        SampleRate = info.Value.SampleRate;
        ValidateFormat(path);
        stream.Position = 0;

        ISoundDecoder? decoder;
        try
        {
            decoder = new MiniAudioCodecFactory().CreateDecoder(stream, format, new AudioFormat
            {
                Format = SampleFormat.F32,
                Channels = SourceChannels,
                Layout = AudioFormat.GetLayoutFromChannels(SourceChannels),
                SampleRate = SampleRate,
            });
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new NotSupportedException($"'{path}': {format} decoding needs SoundFlow's miniaudio native library, which failed to load ({e.Message}).", e);
        }

        _decoder = decoder ?? throw new NotSupportedException($"'{path}': no {format} decoder.");
        TotalFrames = _decoder.Length > 0 ? _decoder.Length / SourceChannels : -1;
    }

    public override string Format { get; }

    public override bool Seek(long frame) => _decoder.Seek((int)Math.Min(int.MaxValue, Math.Max(0, frame) * SourceChannels));

    protected override int ReadSource(Span<float> destination)
    {
        var count = _decoder.Decode(destination);
        return count - count % SourceChannels;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _decoder.Dispose();
            _stream.Dispose();
        }

        base.Dispose(disposing);
    }
}
