using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace MainframeEngine.Networking;

/// <summary>
/// The 7-byte header in front of every message payload: <c>protocolVersion</c> (byte), <c>messageId</c>
/// (ushort) and <c>tick</c> (uint), little-endian.
/// </summary>
/// <param name="ProtocolVersion">Game protocol version (<see cref="MessageRegistry.ProtocolVersion"/>); mismatches are dropped.</param>
/// <param name="MessageId">Id assigned by <see cref="MessageRegistry"/>.</param>
/// <param name="Tick">Sender's simulation tick (<see cref="MessageBus.Tick"/>), e.g. the server tick of a snapshot.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct MessageHeader(byte ProtocolVersion, ushort MessageId, uint Tick)
{
    /// <summary>Encoded size in bytes.</summary>
    public const int Size = 7;

    /// <summary>Decodes a header from the start of <paramref name="source"/>; false if it is too short.</summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out MessageHeader header)
    {
        if (source.Length < Size)
        {
            header = default;
            return false;
        }

        header = new MessageHeader(
            source[0],
            BinaryPrimitives.ReadUInt16LittleEndian(source[1..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[3..]));
        return true;
    }

    /// <summary>Encodes the header into the start of <paramref name="destination"/>; false if it is too short.</summary>
    public bool TryWrite(Span<byte> destination)
    {
        if (destination.Length < Size)
            return false;

        destination[0] = ProtocolVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[1..], MessageId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[3..], Tick);
        return true;
    }

    /// <summary>Appends the encoded header to <paramref name="writer"/>.</summary>
    public void Write(NetBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        TryWrite(writer.GetSpan(Size));
        writer.Advance(Size);
    }
}

/// <summary>Where a received message came from.</summary>
/// <param name="Sender">The peer that sent it.</param>
/// <param name="Channel">The channel it arrived on.</param>
/// <param name="Header">Its header.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct MessageContext(PeerId Sender, NetChannel Channel, MessageHeader Header);

/// <summary>Handles one received message of type <typeparamref name="T"/>. Runs on the thread that polls the bus.</summary>
public delegate void MessageHandler<T>(in MessageContext context, in T message) where T : struct, INetworkTransferable;
