using System.Runtime.InteropServices;
using Steamworks;
using Steamworks.Data;

namespace MainframeEngine.Steamworks;

/// <summary>
/// Steam Networking Sockets over Valve's relay (SDR) behind the engine's <c>SteamSocketsTransport</c>: a relay listen
/// socket (a Facepunch <see cref="SocketManager"/>) or one relay connection (a <see cref="ConnectionManager"/>). Same
/// rules as <see cref="SteamworksApi"/>: primitive types only, main thread only, only while Steam runs.
/// </summary>
/// <remarks>
/// <para>
/// Endpoints are ints (a listen socket or a client connection). Facepunch reports connection state changes inside
/// <see cref="SteamworksApi.RunCallbacks"/>; they are queued per endpoint and the transport drains them from its poll,
/// so it sees every event in its own frame order. Nothing is accepted automatically: the transport accepts or refuses.
/// </para>
/// <para>
/// Receiving goes through the flat API (<c>ReceiveMessagesOnConnection</c>, <c>SteamNetworkingMessage_t_Release</c>)
/// instead of Facepunch's <c>Receive</c>: that one marshals every message into a boxed struct (a managed allocation per
/// packet) and does not report whether the message was reliable. Messages are read in place, so nothing is copied.
/// </para>
/// </remarks>
internal static unsafe partial class SteamworksSockets
{
    /// <summary><c>ESteamNetworkingConnectionState</c> values the engine sees (Facepunch's <see cref="ConnectionState"/>).</summary>
    public const int StateConnecting = (int)ConnectionState.Connecting;

    public const int StateConnected = (int)ConnectionState.Connected;

    public const int StateClosedByPeer = (int)ConnectionState.ClosedByPeer;

    public const int StateProblemDetectedLocally = (int)ConnectionState.ProblemDetectedLocally;

    /// <summary><c>k_nSteamNetworkingSend_Reliable</c>: on a received message the only valid bit of its flags.</summary>
    private const int SendReliable = (int)SendType.Reliable;

    private static readonly Dictionary<int, IEndpoint> Endpoints = [];
    private static int _nextEndpoint;

    private interface IEndpoint
    {
        Queue<(uint Connection, int State, ulong SteamId, int EndReason)> Events { get; }

        void Close();
    }

    /// <summary>The host's relay listen socket: queues every connection's state changes; never accepts by itself.</summary>
    private sealed class RelayHost : SocketManager, IEndpoint
    {
        public Queue<(uint Connection, int State, ulong SteamId, int EndReason)> Events { get; } = new();

        public override void OnConnectionChanged(Connection connection, ConnectionInfo info) =>
            Events.Enqueue((connection.Id, (int)info.State, info.Identity.SteamId, (int)info.EndReason));

        void IEndpoint.Close() => Close();
    }

    /// <summary>A client's relay connection to a host.</summary>
    private sealed class RelayClient : ConnectionManager, IEndpoint
    {
        public Queue<(uint Connection, int State, ulong SteamId, int EndReason)> Events { get; } = new();

        public override void OnConnectionChanged(ConnectionInfo info) =>
            Events.Enqueue((Connection.Id, (int)info.State, info.Identity.SteamId, (int)info.EndReason));

        void IEndpoint.Close()
        {
        }
    }

    /// <summary>
    /// <c>SteamNetworkingMessage_t</c>, only the fields read here. The offsets are the same in Facepunch's Win64 (8-byte
    /// packing) and Posix (4-byte) builds: every field before <c>m_nFlags</c> is naturally aligned either way.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct Message
    {
        [FieldOffset(0)]
        public nint Data;

        [FieldOffset(8)]
        public int Size;

        [FieldOffset(12)]
        public uint Connection;

        [FieldOffset(168)]
        public long MessageNumber;

        [FieldOffset(196)]
        public int Flags;
    }

    /// <summary>Offsets of the message fields read here, for the layout test against Facepunch's own declaration.</summary>
    public static (int Data, int Size, int Connection, int MessageNumber, int Flags) MessageFieldOffsets =>
    (
        (int)Marshal.OffsetOf<Message>(nameof(Message.Data)),
        (int)Marshal.OffsetOf<Message>(nameof(Message.Size)),
        (int)Marshal.OffsetOf<Message>(nameof(Message.Connection)),
        (int)Marshal.OffsetOf<Message>(nameof(Message.MessageNumber)),
        (int)Marshal.OffsetOf<Message>(nameof(Message.Flags))
    );

    /// <summary>Opens a relay listen socket on <paramref name="virtualPort"/>: its endpoint id, or 0.</summary>
    public static int Listen(int virtualPort)
    {
        SteamNetworkingUtils.InitRelayNetworkAccess();
        try
        {
            return Add(SteamNetworkingSockets.CreateRelaySocket<RelayHost>(virtualPort));
        }
        catch (ArgumentException)
        {
            // Facepunch throws registering the invalid socket Steam returns on failure.
            SteamworksApi.Warning?.Invoke($"CreateListenSocketP2P({virtualPort}) failed");
            return 0;
        }
    }

    /// <summary>Starts a relay connection to <paramref name="steamId"/>: its endpoint id (0 on failure) and connection.</summary>
    public static int Connect(ulong steamId, int virtualPort, out uint connection)
    {
        SteamNetworkingUtils.InitRelayNetworkAccess();
        try
        {
            var client = SteamNetworkingSockets.ConnectRelay<RelayClient>(steamId, virtualPort);
            connection = client.Connection.Id;
            return Add(client);
        }
        catch (ArgumentException)
        {
            // Facepunch throws registering the invalid connection Steam returns on failure.
            SteamworksApi.Warning?.Invoke($"ConnectP2P({steamId}, {virtualPort}) failed");
            connection = 0;
            return 0;
        }
    }

    private static int Add(IEndpoint endpoint)
    {
        var id = ++_nextEndpoint;
        Endpoints[id] = endpoint;
        return id;
    }

    /// <summary>Closes a listen socket (its remaining connections close abruptly) and forgets the endpoint.</summary>
    public static void CloseEndpoint(int endpoint)
    {
        if (Endpoints.Remove(endpoint, out var e))
            e.Close();
    }

    /// <summary>The next queued state change of an endpoint's connections.</summary>
    public static bool TryDequeueEvent(int endpoint, out uint connection, out int state, out ulong steamId, out int endReason)
    {
        if (Endpoints.TryGetValue(endpoint, out var e) && e.Events.TryDequeue(out var item))
        {
            (connection, state, steamId, endReason) = item;
            return true;
        }

        (connection, state, steamId, endReason) = (0, 0, 0, 0);
        return false;
    }

    public static bool Accept(uint connection) => ((Connection)connection).Accept() == Result.OK;

    /// <summary>Closes a connection, telling the peer <paramref name="reasonCode"/> (1000–1999 for the app's own reasons).</summary>
    public static void Close(uint connection, int reasonCode, bool linger) =>
        ((Connection)connection).Close(linger, reasonCode, "Mainframe Engine");

    public static void SetUserData(uint connection, long userData)
    {
        Connection c = connection;
        c.UserData = userData;
    }

    /// <summary>Queues a copy of <paramref name="payload"/> (Facepunch's pooled native buffers). False when Steam refuses it.</summary>
    public static bool Send(uint connection, ReadOnlySpan<byte> payload, bool reliable)
    {
        if (payload.IsEmpty)
            return false; // Facepunch throws on 0 bytes
        fixed (byte* data = payload)
        {
            var type = reliable ? SendType.Reliable : SendType.Unreliable | SendType.NoNagle;
            return ((Connection)connection).SendMessage((nint)data, payload.Length, type) == Result.OK;
        }
    }

    public static void Flush(uint connection) => ((Connection)connection).Flush();

    /// <summary>Takes up to <c>messages.Length</c> received messages of a connection; each must be released.</summary>
    public static int Receive(uint connection, Span<nint> messages)
    {
        fixed (nint* buffer = messages)
            return ReceiveMessagesOnConnection(SteamNetworkingSocketsInterface(), connection, buffer, messages.Length);
    }

    /// <summary>A received message's payload (valid until it is released) and delivery.</summary>
    public static void ReadMessage(nint message, out nint data, out int size, out bool reliable, out long number)
    {
        var m = (Message*)message;
        data = m->Data;
        size = m->Size;
        reliable = (m->Flags & SendReliable) != 0;
        number = m->MessageNumber;
    }

    public static void ReleaseMessage(nint message) => MessageRelease(message);

    /// <summary>Steam's global fake lag (ms) and loss (percent) on sends and receives: a debugging network conditioner.</summary>
    public static void SetFakeConditions(float lagMs, float lossPercent)
    {
        SteamNetworkingUtils.FakeSendPacketLag = lagMs;
        SteamNetworkingUtils.FakeRecvPacketLag = lagMs;
        SteamNetworkingUtils.FakeSendPacketLoss = lossPercent;
        SteamNetworkingUtils.FakeRecvPacketLoss = lossPercent;
    }

    // ISteamNetworkingSockets v012 is the version Facepunch 2.4.1 and this steam_api use.
    [LibraryImport(SteamworksNative.Library, EntryPoint = "SteamAPI_SteamNetworkingSockets_SteamAPI_v012")]
    private static partial nint SteamNetworkingSocketsInterface();

    [LibraryImport(SteamworksNative.Library, EntryPoint = "SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnConnection")]
    private static partial int ReceiveMessagesOnConnection(nint self, uint connection, nint* messages, int maxMessages);

    [LibraryImport(SteamworksNative.Library, EntryPoint = "SteamAPI_SteamNetworkingMessage_t_Release")]
    private static partial void MessageRelease(nint message);
}
