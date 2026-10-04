using NVorbis;

namespace MainframeEngine.Audio;

/// <summary>OGG Vorbis through NVorbis (managed, MIT; ADR 0032). Decodes the first logical stream.</summary>
/// <remarks>
/// NVorbis 0.10.5's own seeking is not sample-accurate (it lands hundreds of samples past the target, by an amount that
/// depends on the file's block sizes, and <c>SeekTo(0)</c> on a fresh reader throws), so this decoder never uses it:
/// a backward seek reopens the reader on the rewound file and a forward seek decodes and discards up to the target.
/// Loops back to the start therefore cost only a header parse; loop points deep into a long file cost decoding up to
/// them on the streaming thread each time round.
/// </remarks>
internal sealed class VorbisDecoder : AudioDecoder
{
    private readonly Stream _stream;
    private readonly string _path;
    private VorbisReader _reader;
    private long _frame; // frames returned so far (the read position)
    private float[]? _discard;

    public VorbisDecoder(Stream stream, string path)
    {
        _stream = stream;
        _path = path;
        _reader = Open(stream, path);
        SourceChannels = _reader.Channels;
        SampleRate = _reader.SampleRate;
        TotalFrames = _reader.TotalSamples > 0 ? _reader.TotalSamples : -1;
        ValidateFormat(path);
    }

    public override string Format => "ogg";

    public override bool Seek(long frame)
    {
        frame = Math.Max(0, frame);
        if (frame < _frame)
        {
            _reader.Dispose();
            _stream.Position = 0;
            _reader = Open(_stream, _path);
            _frame = 0;
        }

        _discard ??= new float[1024 * SourceChannels];
        while (_frame < frame)
        {
            var want = (int)Math.Min(_discard.Length / SourceChannels, frame - _frame) * SourceChannels;
            var got = ReadSource(_discard.AsSpan(0, want));
            if (got == 0)
                break; // past the end: the next read returns 0
        }

        return true;
    }

    protected override int ReadSource(Span<float> destination)
    {
        var count = _reader.ReadSamples(destination);
        count -= count % SourceChannels;
        _frame += count / SourceChannels;
        return count;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reader.Dispose();
            _stream.Dispose();
        }

        base.Dispose(disposing);
    }

    private static VorbisReader Open(Stream stream, string path)
    {
        try
        {
            // The decoder owns the stream (it is reused when seeking backwards), so the reader must not close it.
            return new VorbisReader(stream, closeOnDispose: false) { ClipSamples = false }; // the mixer has headroom
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new InvalidDataException($"'{path}' is not a readable OGG Vorbis file: {e.Message}", e);
        }
    }
}
