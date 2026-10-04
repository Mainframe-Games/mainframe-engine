using MainframeEngine.Networking;

namespace MainframeEngine;

public enum PeerType : byte
{
    ENet,
    Steam,
}

/// <summary>
/// Scene-graph entry point for networking: owns an ENet server and/or client <see cref="MessageBus"/> (both for a
/// listen server) and pumps them in <see cref="OnUpdate"/>.
/// </summary>
/// <remarks>
/// Register every message type in <see cref="Messages"/> before starting; connection events and message handlers
/// live on the buses (<see cref="MessageBus.PeerConnected"/>, <see cref="MessageBus.Subscribe{T}"/>). Constructing
/// the node does not touch the network or the ENet native library; starting does.
/// </remarks>
public class NetworkNode : Node
{
    private static readonly Lazy<string> LocalIpLazy =
        new(static () => NetworkUtils.TryGetPrimaryLocalIPv4(out var address) ? address.ToString() : "127.0.0.1");

    /// <summary>Message types shared by server and client. Frozen when the first bus starts.</summary>
    public MessageRegistry Messages { get; } = new();

    /// <summary>The server bus, while a server is running.</summary>
    public MessageBus? Server { get; private set; }

    /// <summary>The client bus, while a client is running.</summary>
    public MessageBus? Client { get; private set; }

    /// <summary>
    /// The primary local IPv4 address, for LAN play. Looked up on first use (not at type initialization), and
    /// <c>127.0.0.1</c> when there is no IPv4 route.
    /// </summary>
    public static string LocalIp => LocalIpLazy.Value;

    public override void Dispose()
    {
        StopServer();
        StopClient();
        base.Dispose();
    }

    /// <summary>Polls both buses (dispatching handlers and events), then flushes anything they queued.</summary>
    public override void OnUpdate(in GameTime gameTime)
    {
        base.OnUpdate(in gameTime);
        Server?.Poll();
        Client?.Poll();
        Server?.Flush();
        Client?.Flush();
    }

    /// <summary>Starts (or restarts) an ENet server.</summary>
    /// <param name="port">UDP port to listen on.</param>
    /// <param name="maxClients">Connection limit.</param>
    /// <returns>The server bus, also available as <see cref="Server"/>.</returns>
    public MessageBus StartServer(in ushort port, in int maxClients)
    {
        StopServer();
        Server = new MessageBus(EnetTransport.Listen(port, maxClients), Messages);
        return Server;
    }

    /// <summary>Starts (or restarts) an ENet client connecting to <paramref name="ip"/>:<paramref name="port"/>.</summary>
    /// <returns>The client bus, also available as <see cref="Client"/>.</returns>
    public MessageBus StartClient(in string ip, in ushort port)
    {
        StopClient();
        Client = new MessageBus(EnetTransport.Connect(ip, port, Messages.Fingerprint), Messages);
        return Client;
    }

    /// <summary>Stops the server; connected clients are told <see cref="DisconnectReason.Shutdown"/>.</summary>
    public void StopServer()
    {
        Server?.Dispose();
        Server = null;
    }

    /// <summary>Stops the client; the server is told <see cref="DisconnectReason.Shutdown"/>.</summary>
    public void StopClient()
    {
        Client?.Dispose();
        Client = null;
    }
}
