using System.Diagnostics;
using ENet;

namespace MainframeEngine.Networking;

/// <summary>
/// https://github.com/nxrighthere/ENet-CSharp
/// </summary>
public sealed class EnetServer : IDisposable
{
    private readonly Host _server;
    private readonly Stopwatch _stopwatch = new();
    private readonly Dictionary<NetworkPeerId, Peer> _connectedPeers = [];
    
    internal EnetServer(in ushort port, in int maxClients)
    {
        _server = new Host();
        var address = new Address { Port = port };
        _server.Create(address, maxClients, 2);
        Console.WriteLine($"Server started on port: {port}");
    }

    public void Dispose()
    {
        _server.Flush();
        _server.Dispose();
    }

    internal unsafe void Poll()
    {
        _stopwatch.Restart();

        var polled = false;

        while (!polled)
        {
            if (_server.CheckEvents(out var netEvent) <= 0)
            {
                if (_server.Service(0, out netEvent) <= 0)
                    break;

                polled = true;
            }

            switch (netEvent.Type)
            {
                case EventType.None:
                    break;

                case EventType.Connect:
                    Console.WriteLine(
                        $"[Server] Client connected - ID: {netEvent.Peer.ID}, IP: {netEvent.Peer.IP}"
                    );
                    _connectedPeers.Add(netEvent.Peer.ID, netEvent.Peer);
                    break;

                case EventType.Disconnect:
                    Console.WriteLine(
                        $"[Server] Client disconnected - ID: {netEvent.Peer.ID}, IP: {netEvent.Peer.IP}"
                    );
                    break;

                case EventType.Timeout:
                    Console.WriteLine(
                        $"[Server] Client timeout - ID: {netEvent.Peer.ID}, IP: {netEvent.Peer.IP}"
                    );
                    break;

                case EventType.Receive:
                    Console.WriteLine(
                        $"[Server] Packet received from Peer - ID: {netEvent.Peer.ID}, IP: {netEvent.Peer.IP}, Channel ID: {netEvent.ChannelID}, Data length: {netEvent.Packet.Length}"
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

    public unsafe void SendToPeers(in byte channel, in ReadOnlySpan<byte> data, in PacketFlags flags = PacketFlags.None)
    {
        Console.WriteLine($"[Server] Sending data {data.Length} bytes");

        fixed (byte* dataPtr = data)
        {
            var packet = new Packet();
            packet.Create((nint)dataPtr, data.Length, flags);
            foreach (var peer in _connectedPeers.Values)
            {
                if (!peer.Send(channel, ref packet))
                    throw new Exception($"Failed to send packet. channelId: {channel}");
            }
            packet.Dispose();
        }
    }

    private void Read(in NetBufferReader reader)
    {
        var message = reader.ReadString();
        Console.WriteLine($"[Server] Message: {message}");
    }
}