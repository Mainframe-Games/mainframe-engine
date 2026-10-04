using System.Diagnostics.CodeAnalysis;

namespace MainframeEngine.Networking;

/// <summary>
/// Planned <see cref="ITransport"/> over Steam Networking Sockets (<c>ISteamNetworkingSockets</c>). <b>Stub:</b> the
/// factories always fail today, because the Steamworks natives cannot be shipped yet (see docs/design/steamworks.md).
/// </summary>
/// <remarks>
/// Intended design, so callers can be written against it now:
/// <list type="bullet">
/// <item><description>Peers are Steam users: <see cref="PeerId"/> is the 64-bit SteamID (P2P, relayed by Valve's SDR).</description></item>
/// <item><description><see cref="NetChannel.Reliable"/> → <c>k_nSteamNetworkingSend_Reliable</c>, <see cref="NetChannel.Unreliable"/> →
/// <c>k_nSteamNetworkingSend_UnreliableNoNagle</c>; both on one connection, the lane given by the channel.</description></item>
/// <item><description>The connect data (<see cref="MessageRegistry.Fingerprint"/>) travels as the connection's user data.</description></item>
/// <item><description>Lobby metadata <c>connect = "steam:&lt;steamId&gt;"</c> selects this transport, <c>"enet:ip:port"</c> selects <see cref="EnetTransport"/>.</description></item>
/// </list>
/// </remarks>
public static class SteamSocketsTransport
{
    /// <summary>True when Steam is running and this transport is implemented. Always false for now.</summary>
    public static bool IsAvailable => false;

    /// <summary>Would listen for P2P connections on <paramref name="virtualPort"/>. Always fails for now.</summary>
    public static bool TryListen(int virtualPort, [NotNullWhen(true)] out ITransport? transport)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(virtualPort);
        transport = null;
        return false;
    }

    /// <summary>Would connect to the Steam user <paramref name="steamId"/>. Always fails for now.</summary>
    public static bool TryConnect(ulong steamId, int virtualPort, uint connectData, [NotNullWhen(true)] out ITransport? transport)
    {
        ArgumentOutOfRangeException.ThrowIfZero(steamId);
        ArgumentOutOfRangeException.ThrowIfNegative(virtualPort);
        _ = connectData;
        transport = null;
        return false;
    }
}
