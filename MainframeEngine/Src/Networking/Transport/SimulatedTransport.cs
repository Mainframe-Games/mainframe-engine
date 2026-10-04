using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MainframeEngine.Networking;

/// <summary>Bad-network settings for a <see cref="SimulatedTransport"/>.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NetworkConditions
{
    /// <summary>Fraction (0–1) of <see cref="NetChannel.Unreliable"/> packets dropped.</summary>
    public double Loss { get; init; }

    /// <summary>Fraction (0–1) of <see cref="NetChannel.Unreliable"/> packets delivered twice.</summary>
    public double Duplication { get; init; }

    /// <summary>One-way delay added to every packet, in seconds.</summary>
    public double Latency { get; init; }

    /// <summary>
    /// Extra random delay (0 to this many seconds) per packet. Unreliable packets may overtake each other (reordering);
    /// reliable ones keep their order, as a reliable channel would.
    /// </summary>
    public double Jitter { get; init; }

    /// <summary>A perfect network.</summary>
    public static NetworkConditions None => default;
}

/// <summary>
/// Wraps a transport and degrades what it sends: drops, duplicates, delays and reorders packets with a seeded random
/// generator, so tests of replication under loss are deterministic. Reliable packets are never dropped or duplicated
/// (the inner transport's reliability layer would recover them) but are delayed in order. Wrap both ends to degrade
/// both directions.
/// </summary>
/// <remarks>
/// Only peers the inner transport reported as connected accept packets (like a real transport). Disconnecting a peer
/// first hands its queued reliable packets to the inner transport, as ENet delivers them before the disconnect, and
/// drops its queued unreliable ones. Delayed packets are copied into pooled arrays and handed to the inner transport once due, during
/// <see cref="Poll"/> or <see cref="Flush"/>; steady-state traffic does not allocate. Time comes from
/// <paramref name="clock"/> (seconds), the wall clock by default; tests pass a manual one.
/// </remarks>
/// <param name="inner">The real transport; disposed with this one.</param>
/// <param name="conditions">Initial settings (<see cref="Conditions"/> can change at any time).</param>
/// <param name="seed">Random seed: the same seed and traffic give the same losses.</param>
/// <param name="clock">Current time in seconds; defaults to a stopwatch.</param>
public sealed class SimulatedTransport(ITransport inner, NetworkConditions conditions, int seed = 0, Func<double>? clock = null)
    : ITransport, ITransportListener
{
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct Pending(PeerId Peer, NetChannel Channel, byte[]? Buffer, int Length);

    private readonly ITransport _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly Random _random = new(seed);
    private readonly Func<double> _clock = clock ?? CreateStopwatchClock();
    private readonly PriorityQueue<Pending, (double Due, long Sequence)> _queue = new();
    private readonly Dictionary<PeerId, double> _lastReliableDue = [];
    private readonly HashSet<PeerId> _peers = [];
    private readonly List<(Pending Packet, (double Due, long Sequence) Priority)> _requeue = [];
    private ITransportListener? _listener;
    private long _sequence;
    private bool _disposed;

    /// <summary>The current bad-network settings.</summary>
    public NetworkConditions Conditions { get; set; } = conditions;

    /// <summary>The wrapped transport.</summary>
    public ITransport Inner => _inner;

    public bool IsServer => _inner.IsServer;

    /// <summary>Packets handed to the inner transport.</summary>
    public long Delivered { get; private set; }

    /// <summary>Unreliable packets dropped on purpose.</summary>
    public long Dropped { get; private set; }

    /// <summary>Extra copies sent.</summary>
    public long Duplicated { get; private set; }

    /// <summary>Packets waiting for their delay.</summary>
    public int InFlight => _queue.Count;

    private static Func<double> CreateStopwatchClock()
    {
        var start = Stopwatch.GetTimestamp();
        return () => Stopwatch.GetElapsedTime(start).TotalSeconds;
    }

    public void Poll(ITransportListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Release();
        _listener = listener;
        try
        {
            _inner.Poll(this); // tracks connected peers, then forwards
        }
        finally
        {
            _listener = null;
        }
    }

    void ITransportListener.OnPeerConnected(PeerId peer, uint connectData)
    {
        _peers.Add(peer);
        _listener?.OnPeerConnected(peer, connectData);
    }

    void ITransportListener.OnPeerDisconnected(PeerId peer, DisconnectReason reason)
    {
        _peers.Remove(peer);
        _lastReliableDue.Remove(peer);
        _listener?.OnPeerDisconnected(peer, reason);
    }

    void ITransportListener.OnReceive(PeerId peer, NetChannel channel, ReadOnlySpan<byte> payload) =>
        _listener?.OnReceive(peer, channel, payload);

    public bool Send(PeerId peer, NetChannel channel, ReadOnlySpan<byte> payload)
    {
        if (_disposed || !_peers.Contains(peer))
            return false;

        var conditions = Conditions;
        var reliable = channel == NetChannel.Reliable;
        if (!reliable && conditions.Loss > 0 && _random.NextDouble() < conditions.Loss)
        {
            Dropped++;
            return true; // lost on the wire: the sender cannot tell
        }

        var copies = !reliable && conditions.Duplication > 0 && _random.NextDouble() < conditions.Duplication ? 2 : 1;
        var now = _clock();
        for (var copy = 0; copy < copies; copy++)
        {
            var due = now + Math.Max(0, conditions.Latency) + (conditions.Jitter > 0 ? _random.NextDouble() * conditions.Jitter : 0);
            if (reliable)
            {
                // In order per peer: never due before the previous reliable packet.
                if (_lastReliableDue.TryGetValue(peer, out var last) && due < last)
                    due = last;
                _lastReliableDue[peer] = due;
            }

            var buffer = payload.Length == 0 ? null : ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.CopyTo(buffer);
            _queue.Enqueue(new Pending(peer, channel, buffer, payload.Length), (due, _sequence++));
            if (copy == 1)
                Duplicated++;
        }

        Release();
        return true;
    }

    public void Disconnect(PeerId peer, DisconnectReason reason)
    {
        if (_disposed)
            return;

        // Reliable packets already queued for the peer go out first; its unreliable ones are dropped.
        _requeue.Clear();
        while (_queue.TryDequeue(out var pending, out var priority))
        {
            if (pending.Peer != peer)
            {
                _requeue.Add((pending, priority));
                continue;
            }

            if (pending.Channel == NetChannel.Reliable)
            {
                _inner.Send(pending.Peer, pending.Channel, pending.Buffer is null ? ReadOnlySpan<byte>.Empty : pending.Buffer.AsSpan(0, pending.Length));
                Delivered++;
            }

            if (pending.Buffer is not null)
                ArrayPool<byte>.Shared.Return(pending.Buffer);
        }

        foreach (var (packet, priority) in _requeue)
            _queue.Enqueue(packet, priority);
        _requeue.Clear();
        _lastReliableDue.Remove(peer);
        _peers.Remove(peer);
        _inner.Disconnect(peer, reason);
    }

    public void Flush()
    {
        if (_disposed)
            return;
        Release();
        _inner.Flush();
    }

    /// <summary>Hands every packet whose delay has passed to the inner transport, in due order.</summary>
    private void Release()
    {
        if (_queue.Count == 0)
            return;
        var now = _clock();
        while (_queue.TryPeek(out var pending, out var priority) && priority.Due <= now)
        {
            _queue.Dequeue();
            var buffer = pending.Buffer;
            try
            {
                _inner.Send(pending.Peer, pending.Channel, buffer is null ? ReadOnlySpan<byte>.Empty : buffer.AsSpan(0, pending.Length));
                Delivered++;
            }
            finally
            {
                if (buffer is not null)
                    ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        while (_queue.TryDequeue(out var pending, out _))
        {
            if (pending.Buffer is not null)
                ArrayPool<byte>.Shared.Return(pending.Buffer);
        }

        _inner.Dispose();
    }
}
