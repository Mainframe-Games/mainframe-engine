namespace MainframeEngine.Editor.Music;

/// <summary>
/// A built-in insert effect, processed in process on the render thread (no plugin host round trip).
/// </summary>
/// <remarks>
/// <para>One instance per <see cref="PluginInsert"/> per snapshot builder (the live player and each render have their
/// own), so DSP state (filter memories, delay lines, reverb tails) survives every snapshot rebuild.</para>
/// <para>Parameters: the UI thread calls <see cref="Apply"/> on every build; it writes the changed targets and bumps a
/// version without locking. The render thread sees the new version at its next block, copies the targets into
/// <see cref="Values"/> and calls <see cref="OnParameters"/>, where subclasses set smoothing targets.</para>
/// <para>Bypass stays in the chain: the wet signal crossfades to the (latency-matched) dry one over 10 ms, so bypassing
/// never clicks and plugin delay compensation never changes.</para>
/// <para>Subclasses must not allocate in <see cref="ProcessBlock"/> (buffers are sized in the constructor at the
/// instance's fixed sample rate) and flush denormals in their recursive state (<see cref="Dsp.Flush"/>).</para>
/// </remarks>
public abstract class BuiltinEffect : IEffect
{
    private const float BypassFadeSeconds = 0.01f;

    // A bypass longer than this clears the effect's state when it comes back (no stale tail from long ago).
    private const float BypassResetSeconds = 2f;

    private readonly float[] _targets;
    private readonly float[] _dry = new float[SongEngine.BlockFrames * 2];
    private readonly float[]? _dryDelay;
    private int _dryDelayPosition;
    private int _version = 1;
    private int _seenVersion;
    private int _bypass;
    private int _reset;
    private int _gainReductionBits;
    private float _wet = -1; // < 0: not started (jumps to its target on the first block)
    private long _bypassedFrames;

    protected BuiltinEffect(BuiltinEffectInfo info, int sampleRate, int latencyFrames = 0)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        Info = info;
        SampleRate = sampleRate;
        LatencyFrames = Math.Max(0, latencyFrames);
        _targets = new float[info.Params.Length];
        Values = new float[info.Params.Length];
        for (var i = 0; i < _targets.Length; i++)
            _targets[i] = info.Params[i].Default;
        if (LatencyFrames > 0)
            _dryDelay = new float[LatencyFrames * 2];
    }

    public BuiltinEffectInfo Info { get; }

    public int SampleRate { get; }

    /// <summary>Fixed for the instance (plugin delay compensation).</summary>
    public int LatencyFrames { get; }

    /// <summary>Render thread: the current parameter values (index = <see cref="BuiltinEffectInfo.Params"/> index).</summary>
    protected float[] Values { get; }

    /// <summary>UI thread: whether the insert is bypassed (as last applied).</summary>
    public bool Bypassed => Volatile.Read(ref _bypass) == 1;

    /// <summary>UI thread: pushes an insert's parameters (missing ones read their defaults) and bypass to the render thread.</summary>
    public void Apply(IReadOnlyDictionary<string, float>? values, bool bypass)
    {
        var changed = false;
        var parameters = Info.Params;
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            var value = values is not null && values.TryGetValue(p.Id, out var v) ? p.Clamp(v) : p.Default;
            if (Volatile.Read(ref _targets[i]) == value)
                continue;
            Volatile.Write(ref _targets[i], value);
            changed = true;
        }

        if (changed)
            Interlocked.Increment(ref _version);
        Volatile.Write(ref _bypass, bypass ? 1 : 0);
    }

    /// <summary>Any thread: clears the effect's state (tails, envelopes) before its next block.</summary>
    public void RequestReset() => Volatile.Write(ref _reset, 1);

    void IEffect.Reset() => RequestReset();

    /// <summary>UI thread: the most gain reduction (dB, ≥ 0) since the last call, then resets it (compressor, limiter, gate).</summary>
    public float TakeGainReductionDb() => PeakBits.Take(ref _gainReductionBits);

    /// <summary>Render thread: processes interleaved stereo in place (any length; split into engine-sized blocks).</summary>
    public void Process(Span<float> stereo, in EffectContext context)
    {
        var frames = stereo.Length / 2;
        var context2 = context;
        for (var start = 0; start < frames; start += SongEngine.BlockFrames)
        {
            var count = Math.Min(SongEngine.BlockFrames, frames - start);
            ProcessChunk(stereo.Slice(start * 2, count * 2), context2);
            context2 = context2 with { PositionFrames = context2.PositionFrames + count };
        }
    }

    private void ProcessChunk(Span<float> stereo, in EffectContext context)
    {
        var version = Volatile.Read(ref _version);
        if (version != _seenVersion)
        {
            _seenVersion = version;
            for (var i = 0; i < _targets.Length; i++)
                Values[i] = Volatile.Read(ref _targets[i]);
            OnParameters(Values);
        }

        var frames = stereo.Length / 2;
        var target = Volatile.Read(ref _bypass) == 1 ? 0f : 1f;
        if (_wet < 0)
            _wet = target;
        if (Interlocked.Exchange(ref _reset, 0) == 1)
            ResetState();

        if (_wet == 0 && target == 0)
        {
            // Fully bypassed: the dry path only (still delayed by the latency), the effect idles.
            DelayDry(stereo);
            _bypassedFrames += frames;
            return;
        }

        if (_bypassedFrames > BypassResetSeconds * SampleRate)
            ResetState();
        _bypassedFrames = 0;

        var dry = _dry.AsSpan(0, stereo.Length);
        stereo.CopyTo(dry);
        DelayDry(dry);
        ProcessBlock(stereo, context);

        if (_wet == 1 && target == 1)
            return;
        var step = 1f / (BypassFadeSeconds * SampleRate) * (target > _wet ? 1 : -1);
        var wet = _wet;
        for (var f = 0; f < frames; f++)
        {
            wet = Math.Clamp(wet + step, 0f, 1f);
            stereo[2 * f] = dry[2 * f] + (stereo[2 * f] - dry[2 * f]) * wet;
            stereo[2 * f + 1] = dry[2 * f + 1] + (stereo[2 * f + 1] - dry[2 * f + 1]) * wet;
        }

        _wet = wet;
    }

    // The dry signal delayed by LatencyFrames (a ring), so dry and wet line up in the bypass crossfade.
    private void DelayDry(Span<float> stereo)
    {
        if (_dryDelay is not { } ring)
            return;
        var length = LatencyFrames;
        for (var f = 0; f < stereo.Length / 2; f++)
        {
            var at = 2 * _dryDelayPosition;
            (ring[at], stereo[2 * f]) = (stereo[2 * f], ring[at]);
            (ring[at + 1], stereo[2 * f + 1]) = (stereo[2 * f + 1], ring[at + 1]);
            _dryDelayPosition = _dryDelayPosition + 1 == length ? 0 : _dryDelayPosition + 1;
        }
    }

    /// <summary>Render thread: new parameter values arrived (set targets; jump them on the first call).</summary>
    protected abstract void OnParameters(ReadOnlySpan<float> values);

    /// <summary>Render thread: processes at most <see cref="SongEngine.BlockFrames"/> interleaved stereo frames in place.</summary>
    protected abstract void ProcessBlock(Span<float> stereo, in EffectContext context);

    /// <summary>Render thread: clears tails and envelopes (delay lines, filter memories).</summary>
    protected abstract void ResetState();

    /// <summary>Render thread: records <paramref name="db"/> of gain reduction (the meter keeps the most until taken).</summary>
    protected void ReportGainReduction(float db)
    {
        if (db > 0 && float.IsFinite(db))
            PeakBits.Accumulate(ref _gainReductionBits, db);
    }

    /// <summary>The value of a toggle parameter.</summary>
    protected static bool On(float value) => value >= 0.5f;
}

/// <summary>Lock-free "highest value since last taken" in an int (meters: the render thread raises, the UI takes).</summary>
internal static class PeakBits
{
    public static void Accumulate(ref int bits, float value)
    {
        while (true)
        {
            var current = Volatile.Read(ref bits);
            if (BitConverter.Int32BitsToSingle(current) >= value)
                return;
            if (Interlocked.CompareExchange(ref bits, BitConverter.SingleToInt32Bits(value), current) == current)
                return;
        }
    }

    public static float Take(ref int bits) => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref bits, 0));
}
