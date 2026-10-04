using System.Runtime.InteropServices;

namespace MainframeEngine.Networking;

// Engine messages of the multiplayer layer. They use the top ids of the MessageRegistry (MessageRegistry.MaxMessages
// - 16 and up), so game messages registered in order (0, 1, 2…) never collide with them and both ends agree.
// Messages whose payload is variable (spawn, snapshot, RPC) write straight from the MultiplayerApi's state and hand
// the reader back to it, so encoding and decoding allocate nothing.

internal static class ReplicationMessageIds
{
    public const ushort First = MessageRegistry.MaxMessages - 16;
    public const ushort Hello = First;
    public const ushort Welcome = First + 1;
    public const ushort Spawn = First + 2;
    public const ushort Despawn = First + 3;
    public const ushort Snapshot = First + 4;
    public const ushort Ack = First + 5;
    public const ushort Rpc = First + 6;
    public const ushort Authority = First + 7;

    public static void Register(MessageRegistry registry)
    {
        registry.Register<HelloMessage>(Hello);
        registry.Register<WelcomeMessage>(Welcome);
        registry.Register<SpawnMessage>(Spawn);
        registry.Register<DespawnMessage>(Despawn);
        registry.Register<SnapshotMessage>(Snapshot);
        registry.Register<AckMessage>(Ack);
        registry.Register<RpcMessage>(Rpc);
        registry.Register<AuthorityMessage>(Authority);
    }
}

/// <summary>Client → server, first message: the client's replication fingerprint.</summary>
[StructLayout(LayoutKind.Auto)]
internal struct HelloMessage : INetworkTransferable
{
    public uint ReplicationFingerprint;

    public readonly void NetworkWrite(NetBufferWriter writer) => writer.Write(ReplicationFingerprint);

    public void NetworkRead(NetBufferReader reader) => ReplicationFingerprint = reader.ReadUInt32();
}

/// <summary>Server → client after a valid hello: the client's id and the server's clock.</summary>
[StructLayout(LayoutKind.Auto)]
internal struct WelcomeMessage : INetworkTransferable
{
    public ulong PeerId;
    public ushort TickRate;
    public uint Tick;

    public readonly void NetworkWrite(NetBufferWriter writer)
    {
        writer.Write(PeerId);
        writer.Write(TickRate);
        writer.Write(Tick);
    }

    public void NetworkRead(NetBufferReader reader)
    {
        PeerId = reader.ReadUInt64();
        TickRate = reader.ReadUInt16();
        Tick = reader.ReadUInt32();
    }
}

/// <summary>Server → client: a spawned scene with the full state of its networked nodes.</summary>
[StructLayout(LayoutKind.Auto)]
internal struct SpawnMessage : INetworkTransferable
{
    public NetworkEntity? Entity;
    public NetBufferReader? Reader;

    public readonly void NetworkWrite(NetBufferWriter writer) => MultiplayerApi.WriteSpawn(writer, Entity!);

    public void NetworkRead(NetBufferReader reader) => Reader = reader;
}

/// <summary>Server → client: a networked node left the server's tree.</summary>
[StructLayout(LayoutKind.Auto)]
internal struct DespawnMessage : INetworkTransferable
{
    public uint NetId;

    public readonly void NetworkWrite(NetBufferWriter writer) => writer.WriteVarUInt32(NetId);

    public void NetworkRead(NetBufferReader reader) => NetId = reader.ReadVarUInt32();
}

/// <summary>Server → client, unreliable, every net tick: the members changed since the client's acknowledged tick.</summary>
[StructLayout(LayoutKind.Auto)]
internal struct SnapshotMessage : INetworkTransferable
{
    public MultiplayerApi? Api;
    public PeerState? Peer;
    public NetBufferReader? Reader;

    public readonly void NetworkWrite(NetBufferWriter writer) => Api!.WriteSnapshot(writer, Peer!);

    public void NetworkRead(NetBufferReader reader) => Reader = reader;
}

/// <summary>Client → server, unreliable, every net tick: newest fully applied snapshot (also the heartbeat).</summary>
[StructLayout(LayoutKind.Auto)]
internal struct AckMessage : INetworkTransferable
{
    public uint Tick;

    public readonly void NetworkWrite(NetBufferWriter writer) => writer.Write(Tick);

    public void NetworkRead(NetBufferReader reader) => Tick = reader.ReadUInt32();
}

/// <summary>Either direction: an RPC call (node id, wire index, arguments).</summary>
[StructLayout(LayoutKind.Auto)]
internal struct RpcMessage : INetworkTransferable
{
    public uint NetId;
    public int WireIndex;
    public NetBufferWriter? Arguments;
    public NetBufferReader? Reader;

    public readonly void NetworkWrite(NetBufferWriter writer)
    {
        writer.WriteVarUInt32(NetId);
        writer.WriteVarUInt32((uint)WireIndex);
        writer.Write(Arguments!.WrittenSpan);
    }

    public void NetworkRead(NetBufferReader reader)
    {
        NetId = reader.ReadVarUInt32();
        WireIndex = (int)Math.Min(reader.ReadVarUInt32(), int.MaxValue);
        Reader = reader;
    }
}

/// <summary>Server → client: a node's authority changed.</summary>
[StructLayout(LayoutKind.Auto)]
internal struct AuthorityMessage : INetworkTransferable
{
    public uint NetId;
    public ulong Authority;

    public readonly void NetworkWrite(NetBufferWriter writer)
    {
        writer.WriteVarUInt32(NetId);
        writer.Write(Authority);
    }

    public void NetworkRead(NetBufferReader reader)
    {
        NetId = reader.ReadVarUInt32();
        Authority = reader.ReadUInt64();
    }
}
