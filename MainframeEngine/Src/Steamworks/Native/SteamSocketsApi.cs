using System.Runtime.CompilerServices;
using MainframeEngine.Networking;
using MainframeEngine.Steamworks;

namespace MainframeEngine;

/// <summary>
/// Steam Networking Sockets calls behind <see cref="SteamSocketsTransport"/> (Facepunch's relay sockets through
/// <c>MainframeEngine.Steamworks</c>). Same rules as <see cref="SteamApi"/>: no shim types in signatures or fields,
/// every member never inlined, only used while <see cref="Steam.Valid"/>.
/// </summary>
internal sealed class SteamSocketsApi : ISteamSocketsBackend
{
    public static readonly SteamSocketsApi Instance = new();

    private SteamSocketsApi()
    {
    }

    public bool IsRunning => Steam.Valid;

    public ulong LocalSteamId => Steam.SteamId;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Listen(int virtualPort) => SteamworksSockets.Listen(virtualPort);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Connect(ulong steamId, int virtualPort, out uint connection) => SteamworksSockets.Connect(steamId, virtualPort, out connection);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void CloseEndpoint(int endpoint) => SteamworksSockets.CloseEndpoint(endpoint);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool TryDequeueEvent(int endpoint, out SteamSocketEvent socketEvent)
    {
        if (!SteamworksSockets.TryDequeueEvent(endpoint, out var connection, out var state, out var steamId, out var endReason))
        {
            socketEvent = default;
            return false;
        }

        var kind = state switch
        {
            SteamworksSockets.StateConnecting => SteamSocketEventKind.Connecting,
            SteamworksSockets.StateConnected => SteamSocketEventKind.Connected,
            SteamworksSockets.StateClosedByPeer => SteamSocketEventKind.ClosedByPeer,
            SteamworksSockets.StateProblemDetectedLocally => SteamSocketEventKind.ProblemDetectedLocally,
            _ => SteamSocketEventKind.Other, // FindingRoute, None (after a local close)
        };
        socketEvent = new SteamSocketEvent(connection, kind, steamId, endReason);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool Accept(uint connection) => SteamworksSockets.Accept(connection);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Close(uint connection, int reasonCode, bool linger) => SteamworksSockets.Close(connection, reasonCode, linger);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void SetUserData(uint connection, long userData) => SteamworksSockets.SetUserData(connection, userData);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool Send(uint connection, ReadOnlySpan<byte> payload, bool reliable) => SteamworksSockets.Send(connection, payload, reliable);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Flush(uint connection) => SteamworksSockets.Flush(connection);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Receive(uint connection, Span<nint> messages) => SteamworksSockets.Receive(connection, messages);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SteamSocketMessage ReadMessage(nint message)
    {
        SteamworksSockets.ReadMessage(message, out var data, out var size, out var reliable, out var number);
        return new SteamSocketMessage(data, size, reliable, number);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ReleaseMessage(nint message) => SteamworksSockets.ReleaseMessage(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SetFakeConditions(float lagMs, float lossPercent) => SteamworksSockets.SetFakeConditions(lagMs, lossPercent);

    /// <summary>The shim's <c>SteamNetworkingMessage_t</c> field offsets (data, size, connection, message number, flags).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static (int Data, int Size, int Connection, int MessageNumber, int Flags) MessageFieldOffsets() => SteamworksSockets.MessageFieldOffsets;
}
