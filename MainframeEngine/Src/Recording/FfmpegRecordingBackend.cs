using System.Buffers;
using System.Collections.Concurrent;
using System.Text;

namespace MainframeEngine;

/// <summary>
/// Records through the user's ffmpeg: raw frames into an encoder's stdin (writer thread, bounded queue of
/// 4), audio into a float WAV, both under <c>&lt;out&gt;.partial/</c> (MPEG-TS survives a crash), then a mux to MP4
/// and the sidecar. Encoder choice is probed once per process (per ffmpeg path) and cached.
/// </summary>
/// <remarks>
/// The writer thread never stops draining: if ffmpeg dies, its writes fail, the first failure is recorded and
/// every later queued frame is returned to the pool unwritten, so an offline-mode <c>TryAdd(…, Infinite)</c>
/// on the game thread can never block forever. <see cref="TryWriteVideo"/> then throws so the caller finds out.
/// </remarks>
internal sealed class FfmpegRecordingBackend(string ffmpeg) : IRecordingBackend
{
    private static readonly TimeSpan s_writerJoinTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Immutable, replaced by reference. Two recorders starting at once may both probe (the cost is one extra
    /// second); the results are identical, so the race is harmless.
    /// </summary>
    private static volatile EncoderChoice? s_choice;

    private readonly BlockingCollection<Frame> _frames = new(boundedCapacity: 4);
    private RecordingFormat? _format;
    private string _partial = "";
    private FfmpegProcess? _encoder;
    private Thread? _writer;
    private StreamingWavWriter? _wav;
    private volatile string? _writerError;
    private bool _disposed;

    private readonly record struct Frame(byte[] Buffer, int Length, int Copies);

    private sealed record EncoderChoice(string Ffmpeg, string Encoder);

    public void Begin(RecordingFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (_format is not null)
            throw new InvalidOperationException("The recording backend was already started.");

        // Encoder first: with no usable encoder nothing has touched the disk yet.
        var encoder = ChooseEncoder(ffmpeg);
        var partial = format.OutputPath + ".partial";
        try
        {
            Directory.CreateDirectory(partial);
            _encoder = new FfmpegProcess(ffmpeg,
                FfmpegArgs.Encode(encoder, format.Width, format.Height, format.Fps, format.Layout, Path.Combine(partial, "video.ts")),
                stdin: true);
            _wav = new StreamingWavWriter(File.Create(Path.Combine(partial, "audio.wav")), format.Channels, format.SampleRate);
            _writer = new Thread(WriteFrames) { Name = "Mainframe recorder video writer", IsBackground = true };
            _writer.Start();
        }
        catch
        {
            // Nothing must be left running: kill the encoder, close the WAV, and remove the directory if it is still empty.
            // (The exception is rethrown unchanged; this is cleanup, not handling.)
            _encoder?.Dispose();
            _encoder = null;
            _wav?.Dispose();
            _wav = null;
            TryRemoveEmpty(partial);
            throw;
        }

        _format = format;
        _partial = partial;
        Log.Info($"[Recorder] Recording {format.Width}x{format.Height}@{format.Fps} with {encoder} to {format.OutputPath}");
    }

    public bool TryWriteVideo(ReadOnlySpan<byte> pixels, int copies, bool wait)
    {
        if (_writerError is not null)
            throw new IOException(DescribeWriterError());
        var buffer = ArrayPool<byte>.Shared.Rent(pixels.Length);
        pixels.CopyTo(buffer);
        var item = new Frame(buffer, pixels.Length, copies);
        try
        {
            if (wait ? _frames.TryAdd(item, Timeout.Infinite) : _frames.TryAdd(item))
                return true;
        }
        catch (InvalidOperationException)
        {
            // Adding is completed (FinishAsync or Dispose ran): nothing will ever consume this frame.
        }

        ArrayPool<byte>.Shared.Return(buffer);
        return false;
    }

    public void WriteAudio(ReadOnlySpan<float> interleaved) => Wav.Write(interleaved);

    public void WriteSilence(long frames) => Wav.WriteSilence(frames);

    public async Task<RecordingResult> FinishAsync(long videoFrames, CaptureInfo info, CaptureSpans spans, CancellationToken ct)
    {
        var format = _format ?? throw new InvalidOperationException("The recording was never started.");
        try
        {
            _frames.CompleteAdding();
            // The writer drains at most the 4 queued frames; off the caller's thread because it may wait on the pipe.
            await Task.Run(() => _writer!.Join(), CancellationToken.None).ConfigureAwait(false);
            try
            {
                _encoder!.Input.Close();
            }
            catch (IOException e)
            {
                _writerError ??= e.Message;
            }

            var exit = await _encoder!.WaitAsync(ct).ConfigureAwait(false);
            if (exit != 0 || _writerError is not null)
                throw new IOException($"The encoder failed: {DescribeWriterError()}");

            Wav.Finish(SampleScheduler.SamplesThrough(videoFrames, format.SampleRate, format.Fps));
            Wav.Dispose();
            await FfmpegProcess.RunAsync(ffmpeg,
                FfmpegArgs.Mux(Path.Combine(_partial, "video.ts"), Path.Combine(_partial, "audio.wav"), format.OutputPath), ct).ConfigureAwait(false);
            CaptureSidecarWriter.Write(format.OutputPath, info, spans);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Error($"[Recorder] {e.Message} Partial files kept in {_partial}");
            return new RecordingResult
            {
                Path = format.OutputPath,
                DurationSeconds = info.DurationSeconds,
                DroppedFrames = info.DroppedFrames,
                Error = $"{e.Message} The partial recording is in {_partial}.",
            };
        }

        // The video and sidecar are final; failing to tidy the scratch folder must not turn a good recording into an error.
        try
        {
            Directory.Delete(_partial, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Recorder] Recording finished but could not remove {_partial}: {e.Message}");
        }

        return new RecordingResult { Path = format.OutputPath, DurationSeconds = info.DurationSeconds, DroppedFrames = info.DroppedFrames };
    }

    /// <summary>Safe after <see cref="FinishAsync"/>, after a failed <see cref="Begin"/>, and when called twice.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _frames.CompleteAdding();
        // Killing the encoder makes a writer blocked in a pipe write fail, after which it drains and returns every queued buffer.
        _encoder?.Dispose();
        if (_writer is null || _writer.Join(s_writerJoinTimeout))
            _frames.Dispose();
        _wav?.Dispose();
    }

    private StreamingWavWriter Wav => _wav ?? throw new InvalidOperationException("The recording was never started.");

    private string DescribeWriterError()
    {
        var tail = _encoder?.ErrorTail;
        return string.IsNullOrEmpty(tail) ? _writerError ?? "unknown error" : $"{_writerError} {tail}".Trim();
    }

    private void WriteFrames()
    {
        var input = _encoder!.Input;
        foreach (var (buffer, length, copies) in _frames.GetConsumingEnumerable())
        {
            try
            {
                // After a failure keep consuming (but stop writing) so producers blocked on a full queue are released.
                if (_writerError is null)
                {
                    for (var i = 0; i < copies; i++)
                        input.Write(buffer, 0, length);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException)
            {
                _writerError = e.Message;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static string ChooseEncoder(string ffmpeg)
    {
        if (s_choice is { } cached && cached.Ffmpeg == ffmpeg)
            return cached.Encoder;

        var listing = new StringBuilder();
        using (var list = new FfmpegProcess(ffmpeg, FfmpegArgs.ListEncoders(), stdin: false, onStdout: line => { lock (listing) listing.AppendLine(line); }))
        {
            // WaitAsync returns only after stdout reached EOF, so the listing is complete when it is parsed.
            if (list.WaitAsync(CancellationToken.None).GetAwaiter().GetResult() != 0)
                throw new IOException($"ffmpeg could not list its encoders: {list.ErrorTail}");
        }

        string text;
        lock (listing)
            text = listing.ToString();
        foreach (var candidate in EncoderSelector.Candidates(FfmpegResolver.CurrentOs, EncoderSelector.ParseEncoders(text)))
        {
            try
            {
                FfmpegProcess.RunAsync(ffmpeg, FfmpegArgs.Probe(candidate), CancellationToken.None).GetAwaiter().GetResult();
                s_choice = new EncoderChoice(ffmpeg, candidate);
                return candidate;
            }
            catch (IOException)
            {
                Log.Info($"[Recorder] {candidate} is listed but does not work here; trying the next encoder.");
            }
        }

        throw new IOException("This ffmpeg has no usable H.264 encoder (tried hardware encoders, libx264 and libopenh264).");
    }

    private static void TryRemoveEmpty(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not empty or not ours to remove: leave it; the original failure is what the caller sees.
        }
    }
}
