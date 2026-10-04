using System.Runtime.InteropServices;
using ENet;

namespace MainframeEngine.Networking;

/// <summary>
/// https://github.com/nxrighthere/ENet-CSharp
/// </summary>
public sealed class EnetClient : IDisposable
{
    private readonly Host _client;
    private readonly Peer? _peer;

    internal EnetClient(in string ip, in ushort port)
    {
        _client = new Host();
        var address = new Address();

        address.SetHost(ip);
        address.Port = port;
        _client.Create();

        _peer = _client.Connect(address, 2);
    }

    public void Dispose()
    {
        _peer?.Disconnect(0);
        _client.Flush();
        _client.Dispose();
    }

    internal unsafe void Poll()
    {
        var polled = false;

        while (!polled)
        {
            if (_client.CheckEvents(out Event netEvent) <= 0)
            {
                if (_client.Service(0, out netEvent) <= 0)
                    break;

                polled = true;
            }

            switch (netEvent.Type)
            {
                case EventType.None:
                    break;

                case EventType.Connect:
                    Console.WriteLine("Client connected to server");
                    break;

                case EventType.Disconnect:
                    Console.WriteLine("Client disconnected from server");
                    break;

                case EventType.Timeout:
                    Console.WriteLine("Client connection timeout");
                    break;

                case EventType.Receive:
                    Console.WriteLine(
                        $"Packet received from server - Channel ID: {netEvent.ChannelID}, Data length: {netEvent.Packet.Length}"
                    );

                    var data = new ReadOnlySpan<byte>((void*)netEvent.Packet.Data, netEvent.Packet.Length);
                    using (var reader = NetBufferPool.GetReader(data, data.Length))
                        Read(reader);

                    netEvent.Packet.Dispose();
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    public unsafe void Send(in byte channelId, in ReadOnlySpan<byte> data, in PacketFlags flags = PacketFlags.None)
    {
        fixed (byte* dataPtr = data)
        {
            var packet = new Packet();
            packet.Create((nint)dataPtr, data.Length, flags);
            if (_peer?.Send(channelId, ref packet) == false)
                Console.WriteLine($"[Client] Failed to send packet. channelId: {channelId}");
            packet.Dispose();
        }
    }

    private static void Read(in NetBufferReader reader)
    {
        var number = reader.ReadByte();
        var message = reader.ReadString();

        Console.WriteLine($"[Client] Received: {number} - {message}");
    }
}