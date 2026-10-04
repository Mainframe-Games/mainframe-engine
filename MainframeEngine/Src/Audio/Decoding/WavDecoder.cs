using System.Buffers.Binary;

namespace MainframeEngine.Audio;

/// <summary>
/// Managed RIFF/WAVE decoder: PCM 8 (unsigned), 16, 24 and 32-bit integer, IEEE float 32/64, plain or
/// <c>WAVE_FORMAT_EXTENSIBLE</c>. Unknown chunks are skipped; the stream must be seekable.
/// </summary>
internal sealed class WavDecoder : AudioDecoder
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    private readonly Stream _stream;
    private readonly long _dataStart;
    private readonly long _dataLength;
    private readonly int _bitsPerSample;
    private readonly bool _isFloat;
    private readonly int _blockAlign;
    private readonly byte[] _buffer = new byte[16 * 1024];
    private long _position; // bytes into the data chunk

    public WavDecoder(Stream stream, string path)
    {
        _stream = stream;
        Span<byte> header = stackalloc byte[12];
        ReadExactly(header, path);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException($"'{path}' is not a RIFF/WAVE file.");

        var haveFormat = false;
        ushort formatTag = 0;
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> fmt = stackalloc byte[40];
        while (true)
        {
            if (_stream.Read(chunk) < 8)
                throw new InvalidDataException($"'{path}': no data chunk.");
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (size < 16)
                    throw new InvalidDataException($"'{path}': fmt chunk too small.");
                var take = (int)Math.Min(size, (uint)fmt.Length);
                ReadExactly(fmt[..take], path);
                Skip(size - (uint)take + (size & 1));
                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                SourceChannels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                _blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
                _bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                if (formatTag == FormatExtensible)
                {
                    if (take < 26)
                        throw new InvalidDataException($"'{path}': truncated WAVE_FORMAT_EXTENSIBLE header.");
                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]); // sub-format GUID's first two bytes
                }

                haveFormat = true;
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                if (!haveFormat)
                    throw new InvalidDataException($"'{path}': data chunk before fmt chunk.");
                _dataStart = _stream.Position;
                var remaining = _stream.Length - _dataStart;
                _dataLength = size == uint.MaxValue || size > remaining ? remaining : size; // streamed writers leave size open
                break;
            }
            else
            {
                Skip(size + (size & 1));
            }
        }

        _isFloat = formatTag == FormatFloat;
        if (formatTag is not (FormatPcm or FormatFloat))
            throw new NotSupportedException($"'{path}': WAV encoding {formatTag} is not supported (PCM or IEEE float only).");
        if (_isFloat ? _bitsPerSample is not (32 or 64) : _bitsPerSample is not (8 or 16 or 24 or 32))
            throw new NotSupportedException($"'{path}': {_bitsPerSample}-bit {(_isFloat ? "float" : "PCM")} WAV is not supported.");
        ValidateFormat(path);
        if (_blockAlign != SourceChannels * (_bitsPerSample / 8))
            throw new InvalidDataException($"'{path}': inconsistent block align {_blockAlign}.");

        _dataLength -= _dataLength % _blockAlign;
        TotalFrames = _dataLength / _blockAlign;
    }

    public override string Format => "wav";

    public override bool Seek(long frame)
    {
        _position = Math.Clamp(frame, 0, TotalFrames) * _blockAlign;
        _stream.Position = _dataStart + _position;
        return true;
    }

    protected override int ReadSource(Span<float> destination)
    {
        var bytesPerSample = _bitsPerSample / 8;
        var frames = (int)Math.Min(destination.Length / SourceChannels, (_dataLength - _position) / _blockAlign);
        var written = 0;
        while (frames > 0)
        {
            var chunkFrames = Math.Min(frames, _buffer.Length / _blockAlign); // ≥ 1: block align ≤ 8 ch × 8 bytes
            var bytes = _buffer.AsSpan(0, chunkFrames * _blockAlign);
            var got = 0;
            while (got < bytes.Length)
            {
                var n = _stream.Read(bytes[got..]);
                if (n == 0)
                    break;
                got += n;
            }

            var gotFrames = got / _blockAlign;
            var samples = gotFrames * SourceChannels;
            Convert(bytes[..(gotFrames * _blockAlign)], destination.Slice(written, samples), bytesPerSample);
            written += samples;
            _position += gotFrames * _blockAlign;
            frames -= gotFrames;
            if (gotFrames < chunkFrames)
                break; // truncated file
        }

        return written;
    }

    private void Convert(ReadOnlySpan<byte> bytes, Span<float> destination, int bytesPerSample)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            var b = bytes.Slice(i * bytesPerSample, bytesPerSample);
            destination[i] = (_isFloat, bytesPerSample) switch
            {
                (true, 4) => BinaryPrimitives.ReadSingleLittleEndian(b),
                (true, _) => (float)BinaryPrimitives.ReadDoubleLittleEndian(b),
                (false, 1) => (b[0] - 128) / 128f,
                (false, 2) => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
                (false, 3) => ((b[0] << 8) | (b[1] << 16) | (b[2] << 24)) / 2147483648f,
                _ => BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648f,
            };
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _stream.Dispose();
        base.Dispose(disposing);
    }

    private void ReadExactly(Span<byte> buffer, string path)
    {
        try
        {
            _stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException($"'{path}': truncated WAV header.", e);
        }
    }

    private void Skip(long bytes)
    {
        if (bytes > 0)
            _stream.Seek(bytes, SeekOrigin.Current);
    }
}
