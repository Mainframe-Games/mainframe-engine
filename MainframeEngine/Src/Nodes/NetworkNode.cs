using ENet;
using MainframeEngine.Networking;

namespace MainframeEngine;

public enum PeerType : byte
{
    ENet,
    Steam,
}

/// <summary>
/// Represents a node in the network responsible for managing server and client objects
/// and their respective lifecycle. Inherits from the <see cref="Node"/> base class.
/// </summary>
public class NetworkNode : Node
{
    public EnetServer? Server { get; private set; }
    public EnetClient? Client { get; private set; }

    /// <summary>
    /// Represents the primary local IPv4 address of the network node. This value is initialized
    /// using a utility method to determine the primary IP address of the hosting machine. It is
    /// generally used to identify the local node in LAN-based networking setups.
    /// </summary>
    public static readonly string LocalIp = NetworkUtils.GetPrimaryLocalIPv4().ToString();

    public NetworkNode()
    {
        Library.Initialize();
    }

    public override void Dispose()
    {
        Server?.Dispose();
        Client?.Dispose();
        NetBufferPool.Destroy();
        Library.Deinitialize();
        base.Dispose();
    }

    public override void OnUpdate(in GameTime gameTime)
    {
        base.OnUpdate(in gameTime);
        Server?.Poll();
        Client?.Poll();
    }

    /// <summary>
    /// Initializes and starts the server on the specified port with a maximum number of clients.
    /// </summary>
    /// <param name="port">The port on which the server will listen for connections.</param>
    /// <param name="maxClients">The maximum number of clients that can connect to the server.</param>
    public void StartServer(in ushort port, in int maxClients)
    {
        Server = new EnetServer(port, maxClients);
    }

    /// <summary>
    /// Initializes and connects the client to the specified server IP address and port.
    /// </summary>
    /// <param name="ip">The IP address of the server to connect to.</param>
    /// <param name="port">The port on which the server is listening for connections.</param>
    public void StartClient(in string ip, in ushort port)
    {
        Client = new EnetClient(ip, port);
    }
}