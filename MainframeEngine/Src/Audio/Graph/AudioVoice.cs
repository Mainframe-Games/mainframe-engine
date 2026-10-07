using SoundFlow.Abstracts;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Metadata.Models;
using SoundFlow.Structs;

namespace MainframeEngine.Audio;

/// <summary>Where a voice is in its life, as seen by the audio thread.</summary>
internal enum VoiceStatus : byte
{
    Idle,
    Playing,
    Paused,
    Stopping,
}

/// <summary>
/// One pooled voice on the audio thread: a SoundFlow <see cref="SoundPlayer"/> (in its bus's mixer for the
/// voice's whole life) reading a retargetable <see cref="VoiceSource"/>, followed by a <see cref="SpatialSmoother"/>.
/// The source resamples (sample rate × pitch × doppler) and loops; the smoother applies gain, pan and the distance
/// low-pass with per-sample ramps. All state here belongs to the audio thread; the game thread only sends commands.
/// </summary>
internal sealed class AudioVoice : IDisposable
{
    public AudioVoice(AudioEngine engine, AudioFormat format, int index, int busIndex)
    {
        Index = index;
        BusIndex = busIndex;
        Source = new VoiceSource(format.SampleRate);
        Smoother = new SpatialSmoother(format.SampleRate);
        Player = new SoundPlayer(engine, format, Source)
        {
            Name = "Voice " + index,
            Volume = AudioGraph.UnityVolume,
            Enabled = false,
        };
        Player.AddModifier(Smoother);
    }

    public int Index { get; }
    public int BusIndex { get; }
    public SoundPlayer Player { get; }
    public VoiceSource Source { get; }
    public SpatialSmoother Smoother { get; }

    /// <summary>Generation of the play this voice is running (matches the game thread's handle).</summary>
    public int Generation { get; private set; }

    public VoiceStatus Status { get; private set; }

    /// <summary>
    /// The stream pipe for streamed sounds on this voice; created by the game thread on the voice's first streamed
    /// play (before the play command is published, so the audio thread always sees it).
    /// </summary>
    public AudioStreamChannel? StreamChannel { get; set; }

    /// <summary>
    /// Audio thread: an end (<see cref="VoiceEnd.Finished"/> or <see cref="VoiceEnd.Failed"/>) not yet reported to
    /// the game thread (retried while the event ring is full); <see cref="VoiceEnd.None"/> when nothing is pending.
    /// </summary>
    public VoiceEnd PendingEnd { get; set; }

    /// <summary>Audio thread: <see cref="VoiceSource.Underruns"/> already added to the root's total.</summary>
    public int ReportedUnderruns { get; set; }

    /// <summary>Last playback position in source frames (read by the game thread).</summary>
    public double Position;

    /// <summary>Starts <paramref name="source"/> (a clip, or a stream channel request) from scratch.</summary>
    public void Play(int generation, AudioSource source, AudioStreamChannel? channel, int streamGeneration,
        double startFrame, in VoiceParams parameters, bool positional, bool loop, long loopStart, long loopEnd, bool startPaused)
    {
        Generation = generation;
        PendingEnd = VoiceEnd.None;
        Source.Start(source, channel, streamGeneration, startFrame, loop, loopStart, loopEnd, parameters.Pitch);
        Smoother.Reset(parameters, positional);
        Volatile.Write(ref Position, startFrame);
        if (startPaused)
        {
            Status = VoiceStatus.Paused;
            Player.Pause();
            return;
        }

        Status = VoiceStatus.Playing;
        Player.Play();
    }

    /// <summary>A streamed voice seeks: read a new stream generation from <paramref name="startFrame"/>. Ignored when idle.</summary>
    public void RestartStream(int streamGeneration, double startFrame)
    {
        if (Status is VoiceStatus.Idle or VoiceStatus.Stopping)
            return;
        Source.RestartStream(streamGeneration, startFrame);
        Volatile.Write(ref Position, startFrame);
    }

    public void SetParams(in VoiceParams parameters)
    {
        Smoother.SetTarget(parameters);
        Source.TargetPitch = parameters.Pitch;
    }

    public void Pause()
    {
        if (Status != VoiceStatus.Playing)
            return;
        Status = VoiceStatus.Paused;
        Player.Pause();
    }

    public void Resume()
    {
        if (Status != VoiceStatus.Paused)
            return;
        Status = VoiceStatus.Playing;
        Player.Play();
    }

    /// <summary>Fades out over a few milliseconds, then goes idle (no click).</summary>
    public void Stop()
    {
        if (Status == VoiceStatus.Idle)
            return;
        if (Status == VoiceStatus.Paused || !Smoother.HasRendered)
        {
            Halt(); // nothing is sounding (paused, or stopped before its first block): no fade needed
            return;
        }

        Status = VoiceStatus.Stopping;
        Smoother.FadeOut();
    }

    /// <summary>Stops immediately.</summary>
    public void Halt()
    {
        Status = VoiceStatus.Idle;
        Source.Release();
        Player.Pause();
        Player.Enabled = false;
    }

    public void Seek(double frame)
    {
        Source.SeekMemory(frame);
        Volatile.Write(ref Position, frame);
    }

    /// <summary>
    /// After the mix: publishes the position and reports whether the voice just ended naturally (the source ran
    /// out) or finished fading out after a stop.
    /// </summary>
    public VoiceEnd AfterMix()
    {
        if (Status == VoiceStatus.Idle)
            return VoiceEnd.None;
        Volatile.Write(ref Position, Source.PlaybackFrame);
        if (Status == VoiceStatus.Stopping && Smoother.FadedOut)
        {
            Halt();
            return VoiceEnd.Stopped;
        }

        if (Status == VoiceStatus.Playing && Source.Ended)
        {
            var failed = Source.Failed;
            Halt();
            return failed ? VoiceEnd.Failed : VoiceEnd.Finished;
        }

        return VoiceEnd.None;
    }

    public void Dispose() => Player.Dispose(); // also disposes the source
}

internal enum VoiceEnd : byte
{
    None,
    Finished,
    Stopped,
    /// <summary>A streamed file failed to open or decode.</summary>
    Failed,
}

/// <summary>Per-voice mix parameters the game thread computes each frame (gain already includes attenuation).</summary>
internal struct VoiceParams
{
    public float Gain;
    public float PanLeft;
    public float PanRight;
    /// <summary>Distance low-pass cutoff in Hz (0 = open).</summary>
    public float LowPassHz;
    /// <summary>Playback rate multiplier: pitch scale × doppler.</summary>
    public float Pitch;

    public static VoiceParams Default => new() { Gain = 1f, PanLeft = 1f, PanRight = 1f, Pitch = 1f };
}

/// <summary>
/// A SoundFlow data provider that a pooled voice keeps for life and that is pointed at a new sound on each play.
/// Produces stereo device-rate frames by linear-interpolation resampling of an in-memory clip or of the voice's
/// stream channel, with seamless loops. Never reports end-of-stream to SoundFlow (it reports a "live" length of 0
/// and fills silence), so the player never disables itself; the voice decides when it is done.
/// </summary>
internal sealed class VoiceSource : ISoundDataProvider
{
    private readonly int _deviceRate;
    private AudioSource? _source;
    private AudioClipData? _clip;
    private AudioStreamChannel? _channel;
    private AudioStreamGeneratorPlayback? _generator;
    private int _streamGeneration;
    private int _streamChannels; // 0 until the stream generation is synced
    private bool _loop;
    private long _loopStart;
    private long _loopEnd;
    private double _rateRatio; // source rate / device rate
    private float _pitch = 1f;

    // Clip playback: fractional frame position.
    private double _position;

    // Stream playback: the two frames being interpolated and the fraction between them.
    private float _s0L, _s0R, _s1L, _s1R;
    private double _fraction;
    private long _streamFrames; // frames consumed from the ring (for the reported position)
    private bool _primed;

    public VoiceSource(int deviceRate)
    {
        _deviceRate = deviceRate;
    }

    private float _targetPitch = 1f;
    private long _limit; // stream: absolute ring position this block may read up to

    /// <summary>Playback-rate multiplier (pitch × doppler); NaN or non-positive values become 1, the rest is clamped.</summary>
    public float TargetPitch
    {
        get => _targetPitch;
        set => _targetPitch = SanitizePitch(value);
    }

    /// <summary>True once a non-looping source has played out (or its stream failed).</summary>
    public bool Ended { get; private set; }

    /// <summary>True when the stream ended because its file could not be decoded (no <c>Finished</c>).</summary>
    public bool Failed { get; private set; }

    /// <summary>Stream reads that found the ring empty (decoder not keeping up).</summary>
    public int Underruns { get; private set; }

    /// <summary>Current position in source frames.</summary>
    public double PlaybackFrame
    {
        get
        {
            if (_clip is not null)
                return _position;
            if (_source is null)
                return 0;
            var frame = _streamStart + _streamFrames;
            if (_loop && _loopEndOrLength > _loopStart && frame >= _loopEndOrLength)
                frame = _loopStart + (frame - _loopStart) % (_loopEndOrLength - _loopStart);
            return frame;
        }
    }

    private long _streamStart;
    private long _loopEndOrLength;

    public void Start(AudioSource source, AudioStreamChannel? channel, int streamGeneration, double startFrame, bool loop,
        long loopStart, long loopEnd, float pitch)
    {
        _source = source;
        _clip = source as AudioClipData;
        _generator = (source as AudioGeneratorSource)?.Playback;
        _channel = _clip is null && _generator is null ? channel : null;
        _streamGeneration = streamGeneration;
        _streamChannels = 0;
        _loop = loop;
        var length = source.Frames > 0 ? source.Frames : long.MaxValue;
        _loopStart = Math.Clamp(loopStart, 0, Math.Max(0, length - 1));
        _loopEnd = loopEnd > _loopStart && loopEnd <= length ? loopEnd : length;
        _loopEndOrLength = _loopEnd;
        _rateRatio = (double)source.SampleRate / _deviceRate;
        TargetPitch = pitch;
        _pitch = TargetPitch;
        _position = double.IsFinite(startFrame) ? Math.Max(0, startFrame) : 0;
        RestartStream(streamGeneration, _position);
    }

    /// <summary>Stream sources: start reading a new stream generation (a seek) without touching the clip state.</summary>
    public void RestartStream(int streamGeneration, double startFrame)
    {
        _streamGeneration = streamGeneration;
        _streamChannels = 0;
        _streamStart = double.IsFinite(startFrame) ? (long)Math.Max(0, startFrame) : 0;
        _streamFrames = 0;
        _fraction = 0;
        _primed = false;
        _s0L = _s0R = _s1L = _s1R = 0;
        Ended = false;
        Failed = false;
    }

    private static float SanitizePitch(float pitch) => float.IsNaN(pitch) || pitch <= 0f ? 1f : Math.Clamp(pitch, 0.01f, 16f);

    public void Release()
    {
        _source = null;
        _clip = null;
        _channel = null;
        _generator = null;
        Ended = false;
        Failed = false;
    }

    public void SeekMemory(double frame)
    {
        if (_clip is not null)
            _position = Math.Clamp(frame, 0, _clip.Frames);
    }

    // --- ISoundDataProvider -----------------------------------------------------------------------

    public int ReadBytes(Span<float> buffer)
    {
        var frames = buffer.Length / 2;
        var written = 0;
        if (!Ended && _source is not null)
        {
            // Ramp pitch changes over the block (doppler, PitchScale) instead of stepping them.
            var startStep = _rateRatio * _pitch;
            var endStep = _rateRatio * TargetPitch;
            _pitch = TargetPitch;
            written = _clip is not null
                ? ReadClip(_clip, buffer, frames, startStep, endStep)
                : _generator is not null
                    ? ReadGenerator(_generator, buffer, frames, startStep, endStep)
                    : ReadStream(buffer, frames, startStep, endStep);
        }

        buffer[(written * 2)..].Clear();
        return buffer.Length;
    }

    private int ReadClip(AudioClipData clip, Span<float> buffer, int frames, double startStep, double endStep)
    {
        var samples = clip.Samples;
        var stereo = clip.Channels == 2;
        var length = clip.Frames;
        var loopLength = _loopEnd - _loopStart;
        var looping = _loop && loopLength > 0;
        var end = looping ? _loopEnd : length;
        var stepDelta = frames > 1 ? (endStep - startStep) / frames : 0;
        var step = startStep;
        var position = _position;

        for (var f = 0; f < frames; f++)
        {
            if (position >= end)
            {
                if (!looping)
                {
                    _position = length;
                    Ended = true;
                    return f;
                }

                position = _loopStart + (position - _loopStart) % loopLength;
            }

            var i0 = (long)position;
            var t = (float)(position - i0);
            var i1 = i0 + 1;
            if (i1 >= end)
                i1 = looping ? _loopStart : -1;

            float l0, r0, l1 = 0, r1 = 0;
            if (stereo)
            {
                l0 = samples[2 * i0];
                r0 = samples[2 * i0 + 1];
                if (i1 >= 0)
                {
                    l1 = samples[2 * i1];
                    r1 = samples[2 * i1 + 1];
                }
            }
            else
            {
                l0 = r0 = samples[i0];
                if (i1 >= 0)
                    l1 = r1 = samples[i1];
            }

            buffer[2 * f] = l0 + (l1 - l0) * t;
            buffer[2 * f + 1] = r0 + (r1 - r0) * t;
            position += step;
            step += stepDelta;
        }

        _position = position;
        return frames;
    }

    private int ReadStream(Span<float> buffer, int frames, double startStep, double endStep)
    {
        var channel = _channel;
        if (channel is null)
        {
            Ended = true;
            return 0;
        }

        if (_streamChannels == 0)
        {
            _streamChannels = channel.TrySync(_streamGeneration);
            if (_streamChannels == 0)
                return 0; // the streaming thread has not opened the file yet: silence, hold position
        }

        // Snapshot the producer's state once per block: "finished" first, then how far this generation's samples go
        // (never into a newer generation's samples).
        _producerDone = channel.EndGeneration == _streamGeneration;
        _producerFailed = channel.ErrorGeneration == _streamGeneration;
        _limit = channel.ReadLimit(_streamGeneration);

        if (!_primed)
        {
            // Load the first two frames so interpolation has both ends.
            if (channel.Readable(_limit) < 2 * _streamChannels)
                return CheckStreamEnd(channel, 0, countUnderrun: false); // first samples not decoded yet
            TryReadFrame(channel, out _s0L, out _s0R);
            TryReadFrame(channel, out _s1L, out _s1R);
            _primed = true;
            _streamFrames = 0;
        }

        var stepDelta = frames > 1 ? (endStep - startStep) / frames : 0;
        var step = startStep;
        for (var f = 0; f < frames; f++)
        {
            var t = (float)_fraction;
            buffer[2 * f] = _s0L + (_s1L - _s0L) * t;
            buffer[2 * f + 1] = _s0R + (_s1R - _s0R) * t;
            _fraction += step;
            step += stepDelta;
            while (_fraction >= 1.0)
            {
                if (!TryReadFrame(channel, out var l, out var r))
                {
                    // Out of data: either the end, or the decoder fell behind (hold and retry next block).
                    _fraction = Math.Min(_fraction, 1.0);
                    return CheckStreamEnd(channel, f + 1, countUnderrun: true);
                }

                _fraction -= 1.0;
                _s0L = _s1L;
                _s0R = _s1R;
                _s1L = l;
                _s1R = r;
                _streamFrames++;
            }
        }

        return frames;
    }

    // Generator rings: linear interpolation like streams; an empty ring holds the position and plays silence.
    private int ReadGenerator(AudioStreamGeneratorPlayback ring, Span<float> buffer, int frames, double startStep, double endStep)
    {
        if (!_primed)
        {
            if (ring.FramesQueued < 2)
                return 0;
            ring.TryRead(out _s0L, out _s0R);
            ring.TryRead(out _s1L, out _s1R);
            _primed = true;
        }

        var stepDelta = frames > 1 ? (endStep - startStep) / frames : 0;
        var step = startStep;
        for (var f = 0; f < frames; f++)
        {
            var t = (float)_fraction;
            buffer[2 * f] = _s0L + (_s1L - _s0L) * t;
            buffer[2 * f + 1] = _s0R + (_s1R - _s0R) * t;
            _fraction += step;
            step += stepDelta;
            while (_fraction >= 1.0)
            {
                if (!ring.TryRead(out var l, out var r))
                {
                    _fraction = Math.Min(_fraction, 1.0);
                    if (ring.Started)
                    {
                        ring.CountUnderrun();
                        Underruns++;
                    }

                    return f + 1;
                }

                _fraction -= 1.0;
                _s0L = _s1L;
                _s0R = _s1R;
                _s1L = l;
                _s1R = r;
                _streamFrames++;
            }
        }

        return frames;
    }

    // Out of samples: the end when the producer has finished this generation (and too little is left to
    // interpolate), otherwise the decoder is behind — output silence and retry next block.
    private int CheckStreamEnd(AudioStreamChannel channel, int written, bool countUnderrun)
    {
        var needed = _primed ? _streamChannels : 2 * _streamChannels;
        if (_producerDone && channel.Readable(_limit) < needed)
        {
            Ended = true;
            Failed = _producerFailed;
        }
        else if (countUnderrun)
        {
            Underruns++;
        }

        return written;
    }

    private bool _producerDone;
    private bool _producerFailed;

    private bool TryReadFrame(AudioStreamChannel channel, out float left, out float right)
    {
        var channels = _streamChannels;
        if (channel.Readable(_limit) < channels)
        {
            left = right = 0;
            return false;
        }

        left = channel.Peek(0);
        right = channels == 2 ? channel.Peek(1) : left;
        channel.Advance(channels);
        return true;
    }

    public void Seek(int offset)
    {
        // SoundFlow only seeks providers through the player's Seek API, which the engine never calls.
    }

    public int Position => 0;

    /// <summary>0: a "live" source as far as SoundFlow is concerned (the voice tracks its own end).</summary>
    public int Length => 0;

    public bool CanSeek => false;

    public SampleFormat SampleFormat => SampleFormat.F32;

    public int SampleRate => _deviceRate;

    public bool IsDisposed { get; private set; }

    public SoundFormatInfo? FormatInfo => null;

#pragma warning disable CS0067 // never raised: the engine reports ends through its own event queue
    public event EventHandler<EventArgs>? EndOfStreamReached;
    public event EventHandler<PositionChangedEventArgs>? PositionChanged;
#pragma warning restore CS0067

    public void Dispose()
    {
        IsDisposed = true;
        Release();
    }
}
