using System.Runtime.InteropServices;

namespace MainframeEngine.Networking;

/// <summary>Totals and rates of a <see cref="MultiplayerApi"/> (see <see cref="MultiplayerApi.Stats"/>).</summary>
[StructLayout(LayoutKind.Auto)]
public record struct NetworkStats
{
    /// <summary>Bytes sent, including headers, over every channel (from <see cref="MessageBus.Stats"/>).</summary>
    public long BytesSent { get; set; }

    /// <summary>Bytes received and dispatched.</summary>
    public long BytesReceived { get; set; }

    /// <summary>Bytes per second sent, averaged over the last full second.</summary>
    public double SendBytesPerSecond { get; set; }

    /// <summary>Bytes per second received, averaged over the last full second.</summary>
    public double ReceiveBytesPerSecond { get; set; }

    /// <summary>Server: snapshots sent to all clients; client: snapshots applied.</summary>
    public long Snapshots { get; set; }

    /// <summary>Client: snapshots ignored because a newer one was already applied (late, duplicated or reordered).</summary>
    public long SnapshotsDiscarded { get; set; }

    /// <summary>Size in bytes of the last snapshot sent (server, last client) or received (client).</summary>
    public int LastSnapshotBytes { get; set; }

    public long Spawns { get; set; }

    public long Despawns { get; set; }

    public long RpcsSent { get; set; }

    public long RpcsReceived { get; set; }

    /// <summary>RPCs refused by the authority checks (server: from clients; any peer: refused before sending).</summary>
    public long RpcsRejected { get; set; }

    /// <summary>Server: client RPCs whose body threw (logged; see <see cref="MultiplayerApi.RpcFailed"/>).</summary>
    public long RpcsFailed { get; set; }

    /// <summary>Networked nodes currently registered (roots and networked descendants).</summary>
    public int NetworkedNodes { get; set; }
}

/// <summary>Per-client view of a server (see <see cref="MultiplayerApi.GetPeerStats"/>).</summary>
/// <param name="Peer">The client.</param>
/// <param name="Ready">Handshake complete.</param>
/// <param name="AckedTick">Newest snapshot the client has fully applied.</param>
/// <param name="BytesSent">Bytes sent to this client.</param>
/// <param name="SnapshotsSent">Snapshots sent to this client.</param>
/// <param name="LastSnapshotBytes">Size of the last snapshot sent to this client.</param>
/// <param name="RoundTripTime">Smoothed snapshot → acknowledgement time, in seconds (0 until measured).</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct PeerNetworkStats(
    PeerId Peer,
    bool Ready,
    uint AckedTick,
    long BytesSent,
    long SnapshotsSent,
    long LastSnapshotBytes,
    double RoundTripTime);
