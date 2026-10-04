using System.Numerics;
using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

/// <summary>Reliable control-style message with a string.</summary>
public struct ChatMessage : INetworkTransferable
{
    public int Sequence;
    public string Text;

    public readonly void NetworkWrite(NetBufferWriter writer)
    {
        writer.Write(Sequence);
        writer.Write(Text);
    }

    public void NetworkRead(NetBufferReader reader)
    {
        Sequence = reader.ReadInt32();
        Text     = reader.ReadString();
    }
}

/// <summary>Snapshot-style message: fixed size, no allocation to decode.</summary>
public struct TransformMessage : INetworkTransferable
{
    public uint NodeId;
    public Vector3 Position;
    public Quaternion Rotation;

    public readonly void NetworkWrite(NetBufferWriter writer)
    {
        writer.Write(NodeId);
        writer.Write(Position);
        writer.Write(Rotation);
    }

    public void NetworkRead(NetBufferReader reader)
    {
        NodeId   = reader.ReadUInt32();
        Position = reader.ReadVector3();
        Rotation = reader.ReadQuaternion();
    }
}

/// <summary>Registered on one side only (unknown-message tests).</summary>
public struct PingMessage : INetworkTransferable
{
    public long Timestamp;

    public readonly void NetworkWrite(NetBufferWriter writer) => writer.Write(Timestamp);

    public void NetworkRead(NetBufferReader reader) => Timestamp = reader.ReadInt64();
}

internal static class TestRegistry
{
    public static MessageRegistry Create(byte protocolVersion = 1)
    {
        var registry = new MessageRegistry(protocolVersion);
        registry.Register<ChatMessage>();
        registry.Register<TransformMessage>();
        return registry;
    }
}

/// <summary>Collects everything a bus reports, for assertions.</summary>
internal sealed class BusRecorder
{
    public readonly List<PeerId> Connected = [];
    public readonly List<(PeerId Peer, DisconnectReason Reason)> Disconnected = [];
    public readonly List<(PeerId Peer, MessageDropReason Reason)> Dropped = [];
    public readonly List<(PeerId Peer, ushort MessageId)> SendFailures = [];
    public readonly List<(MessageContext Context, ChatMessage Message)> Chats = [];
    public readonly List<(MessageContext Context, TransformMessage Message)> Transforms = [];

    public BusRecorder(MessageBus bus)
    {
        bus.PeerConnected    += Connected.Add;
        bus.PeerDisconnected += (peer, reason) => Disconnected.Add((peer, reason));
        bus.MessageDropped   += (peer, reason) => Dropped.Add((peer, reason));
        bus.SendFailed       += (peer, id) => SendFailures.Add((peer, id));
        bus.Subscribe((in MessageContext context, in ChatMessage message) => Chats.Add((context, message)));
        bus.Subscribe((in MessageContext context, in TransformMessage message) => Transforms.Add((context, message)));
    }
}
