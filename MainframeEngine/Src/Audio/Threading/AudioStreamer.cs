namespace MainframeEngine.Audio;

/// <summary>
/// The streaming thread: keeps every <see cref="AudioStreamChannel"/> topped up by decoding its file (OGG, WAV,
/// MP3, FLAC) ahead of playback, handles loops by seeking the decoder, and closes files when a voice stops. File I/O
/// and decoder allocations happen here, never on the game or audio thread. Started on the first streamed play.
/// </summary>
internal sealed class AudioStreamer : IDisposable
{
    private const int ChunkFrames = 4096;

    private readonly Lock _gate = new(); // guards _channels against registration from the game thread
    private readonly List<AudioStreamChannel> _channels = [];
    private AudioStreamChannel[] _snapshot = [];
    private readonly AutoResetEvent _wake = new(false);
    private readonly float[] _scratch = new float[ChunkFrames * 2];
    private readonly Dictionary<AudioStreamChannel, Producer> _producers = [];
    private Thread? _thread;
    private volatile bool _stop;
    private int _errors;

    /// <summary>Decode errors so far (files that failed to open or decode).</summary>
    public int Errors => Volatile.Read(ref _errors);

    /// <summary>Game thread: makes <paramref name="channel"/> serviced (idempotent) and starts the thread.</summary>
    public void Register(AudioStreamChannel channel)
    {
        lock (_gate)
        {
            if (_channels.Contains(channel))
                return;
            _channels.Add(channel);
            _snapshot = [.. _channels];
        }

        if (_thread is null)
        {
            _thread = new Thread(Run) { Name = "Mainframe audio streaming", IsBackground = true, Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
    }

    /// <summary>Game thread: stops servicing <paramref name="channel"/> (its graph was retired); its decoder is closed.</summary>
    public void Unregister(AudioStreamChannel channel)
    {
        lock (_gate)
        {
            if (_channels.Remove(channel))
                _snapshot = [.. _channels];
        }
    }

    /// <summary>Game thread: a request changed; service it now rather than at the next poll.</summary>
    public void Wake() => _wake.Set();

    private void Run()
    {
        while (!_stop)
        {
            AudioStreamChannel[] channels;
            lock (_gate)
                channels = _snapshot;

            var busy = false;
            foreach (var channel in channels)
                busy |= Service(channel);
            CloseRemoved(channels);

            if (!busy)
                _wake.WaitOne(5);
        }

        foreach (var producer in _producers.Values)
            producer.Decoder?.Dispose();
        _producers.Clear();
    }

    // Decoders of channels that were unregistered (retired graphs) are closed here, on the thread that owns them.
    private void CloseRemoved(AudioStreamChannel[] live)
    {
        if (_producers.Count <= live.Length)
            return;
        List<AudioStreamChannel>? gone = null;
        foreach (var channel in _producers.Keys)
        {
            if (Array.IndexOf(live, channel) < 0)
                (gone ??= []).Add(channel);
        }

        if (gone is null)
            return;
        foreach (var channel in gone)
        {
            _producers[channel].Decoder?.Dispose();
            _producers.Remove(channel);
        }
    }

    // Returns true when more work is immediately available (keep looping without sleeping).
    private bool Service(AudioStreamChannel channel)
    {
        if (!channel.TryReadRequest(out var request))
            return true; // the game thread is mid-write; look again shortly

        if (!_producers.TryGetValue(channel, out var producer))
        {
            producer = new Producer();
            _producers[channel] = producer;
        }

        if (request.Generation != producer.Generation)
            Start(channel, producer, request);

        if (producer.Decoder is null || producer.Finished)
            return false;

        return Fill(channel, producer);
    }

    private void Start(AudioStreamChannel channel, Producer producer, AudioStreamChannel.StreamRequest request)
    {
        producer.Decoder?.Dispose();
        producer.Decoder = null;
        producer.Generation = request.Generation;
        producer.Finished = false;
        producer.Request = request;

        if (request.Source is not { } source)
        {
            channel.BeginGeneration(request.Generation, 1);
            channel.EndOfGeneration(request.Generation);
            producer.Finished = true;
            return;
        }

        try
        {
            var decoder = AudioDecoder.Open(source.Path);
            producer.Decoder = decoder;
            producer.Frame = Math.Max(0, request.StartFrame);
            if (producer.Frame > 0 && !decoder.Seek(producer.Frame))
                producer.Frame = 0;
            channel.BeginGeneration(request.Generation, decoder.Channels);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _errors);
            Log.Error($"[Audio] Streaming '{source.Path}' failed: {e.Message}");
            channel.BeginGeneration(request.Generation, 1);
            channel.EndOfGeneration(request.Generation);
            producer.Finished = true;
        }
    }

    private bool Fill(AudioStreamChannel channel, Producer producer)
    {
        var decoder = producer.Decoder!;
        var channels = decoder.Channels;
        var request = producer.Request;
        var loopEnd = request.Loop && request.LoopEnd > request.LoopStart ? request.LoopEnd : long.MaxValue;

        var writable = channel.WritableSamples / channels;
        if (writable < ChunkFrames / 2)
            return false; // ring nearly full: plenty buffered

        var frames = (int)Math.Min(Math.Min(writable, ChunkFrames), loopEnd - producer.Frame);
        int read;
        try
        {
            read = frames > 0 ? decoder.Read(_scratch.AsSpan(0, frames * channels)) / channels : 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or ObjectDisposedException)
        {
            Interlocked.Increment(ref _errors);
            Log.Error($"[Audio] Decoding '{request.Source?.Path}' failed: {e.Message}");
            read = 0;
            producer.Finished = true;
            channel.EndOfGeneration(producer.Generation);
            return false;
        }

        if (read > 0)
        {
            channel.Write(_scratch.AsSpan(0, read * channels));
            producer.Frame += read;
        }

        var atEnd = read == 0 || producer.Frame >= loopEnd;
        if (!atEnd)
            return true;

        if (request.Loop && (read > 0 || producer.Frame > request.LoopStart))
        {
            // Loop: rewind the decoder; the consumer sees one continuous sample stream.
            try
            {
                if (decoder.Seek(request.LoopStart))
                {
                    producer.Frame = request.LoopStart;
                    return true;
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or ObjectDisposedException)
            {
                Interlocked.Increment(ref _errors);
                Log.Error($"[Audio] Looping '{request.Source?.Path}' failed: {e.Message}");
            }
        }

        producer.Finished = true;
        channel.EndOfGeneration(producer.Generation);
        return false;
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread is { } thread && !thread.Join(TimeSpan.FromSeconds(2)))
            Log.Warning("[Audio] Streaming thread did not stop within 2 s.");
        _thread = null;
        _wake.Dispose();
    }

    private sealed class Producer
    {
        public int Generation;
        public AudioDecoder? Decoder;
        public long Frame;
        public bool Finished;
        public AudioStreamChannel.StreamRequest Request;
    }
}
