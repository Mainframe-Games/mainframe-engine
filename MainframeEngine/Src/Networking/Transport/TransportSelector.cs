using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MainframeEngine.Networking;

/// <summary>
/// Where a server can be reached, as text: <c>enet:host:port</c> (UDP), <c>steam:&lt;steamId&gt;</c> (Steam Networking
/// Sockets, P2P) or <c>loopback:&lt;name&gt;</c> (an in-process server, <see cref="LoopbackTransportFactory"/>). Steam
/// lobbies advertise a <c>;</c>-separated list of these in their <c>connect</c> metadata
/// (<see cref="SteamLobbyInfo.ConnectAddress"/>), best first.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NetworkAddress(string Scheme, string Host, ushort Port)
{
    public const string EnetScheme = "enet";
    public const string SteamScheme = "steam";
    public const string LoopbackScheme = "loopback";

    /// <summary>The SteamID of a <c>steam:</c> address (0 otherwise).</summary>
    public ulong SteamId =>
        Scheme == SteamScheme && ulong.TryParse(Host, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

    public static NetworkAddress Enet(string host, ushort port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return new NetworkAddress(EnetScheme, host, port);
    }

    public static NetworkAddress Steam(ulong steamId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(steamId);
        return new NetworkAddress(SteamScheme, steamId.ToString(CultureInfo.InvariantCulture), 0);
    }

    public static NetworkAddress Loopback(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new NetworkAddress(LoopbackScheme, name, 0);
    }

    /// <summary>Parses one address. IPv6 hosts are written in brackets: <c>enet:[::1]:7777</c>.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out NetworkAddress address)
    {
        address = default;
        text = text.Trim();
        var colon = text.IndexOf(':');
        if (colon <= 0)
            return false;
        var scheme = text[..colon].ToString().ToLowerInvariant();
        var rest = text[(colon + 1)..];

        switch (scheme)
        {
            case EnetScheme:
                {
                    var portColon = rest.LastIndexOf(':');
                    if (portColon <= 0 || !ushort.TryParse(rest[(portColon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                        return false;
                    var host = rest[..portColon];
                    if (host is ['[', .., ']'])
                        host = host[1..^1];
                    if (host.IsWhiteSpace())
                        return false;
                    address = new NetworkAddress(scheme, host.ToString(), port);
                    return true;
                }
            case SteamScheme:
                if (!ulong.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId) || steamId == 0)
                    return false;
                address = new NetworkAddress(scheme, steamId.ToString(CultureInfo.InvariantCulture), 0);
                return true;
            case LoopbackScheme:
                if (rest.IsWhiteSpace())
                    return false;
                address = new NetworkAddress(scheme, rest.ToString(), 0);
                return true;
            default:
                if (rest.IsWhiteSpace())
                    return false;
                address = new NetworkAddress(scheme, rest.ToString(), 0); // a game-registered transport
                return true;
        }
    }

    /// <summary>Parses a <c>;</c>-separated list, skipping entries that do not parse.</summary>
    public static List<NetworkAddress> ParseList(string? text)
    {
        var list = new List<NetworkAddress>();
        if (string.IsNullOrWhiteSpace(text))
            return list;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParse(part, out var address))
                list.Add(address);
            else
                Log.Warning($"[Net] ignoring malformed address '{part}'");
        }

        return list;
    }

    /// <summary>Joins addresses into a connect string, best first.</summary>
    public static string FormatList(params ReadOnlySpan<NetworkAddress> addresses)
    {
        var parts = new string[addresses.Length];
        for (var i = 0; i < addresses.Length; i++)
            parts[i] = addresses[i].ToString();
        return string.Join(';', parts);
    }

    public override string ToString() => Scheme switch
    {
        EnetScheme => Host.Contains(':', StringComparison.Ordinal)
            ? string.Create(CultureInfo.InvariantCulture, $"{Scheme}:[{Host}]:{Port}")
            : string.Create(CultureInfo.InvariantCulture, $"{Scheme}:{Host}:{Port}"),
        _ => $"{Scheme}:{Host}",
    };
}

/// <summary>Opens a client transport to a <see cref="NetworkAddress"/> of one scheme.</summary>
public interface ITransportFactory
{
    /// <summary>The address scheme this factory handles (<c>enet</c>, <c>steam</c>…).</summary>
    string Scheme { get; }

    /// <summary>
    /// Starts connecting; false (with a reason) when this transport cannot be used here (no native library, Steam not
    /// running, unknown server…). Must not throw for those.
    /// </summary>
    bool TryConnect(in NetworkAddress address, uint connectData, [NotNullWhen(true)] out ITransport? transport, out string? failure);
}

/// <summary>
/// Picks the transport for a connect string, trying each address in order until one opens: the handoff from a
/// lobby's <c>connect</c> metadata to the network. <see cref="Default"/> knows ENet and Steam sockets; games and tests
/// add factories (e.g. <see cref="LoopbackTransportFactory"/>).
/// </summary>
public sealed class TransportSelector
{
    private readonly Dictionary<string, ITransportFactory> _factories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ENet (<c>enet:</c>) and Steam Networking Sockets (<c>steam:</c>, unavailable until Steam ships natives).</summary>
    public static TransportSelector Default { get; } = CreateDefault();

    /// <summary>A selector with the ENet and Steam factories.</summary>
    public static TransportSelector CreateDefault()
    {
        var selector = new TransportSelector();
        selector.Register(new EnetTransportFactory());
        selector.Register(new SteamSocketsTransportFactory());
        return selector;
    }

    /// <summary>Adds (or replaces) the factory for its scheme.</summary>
    public void Register(ITransportFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories[factory.Scheme] = factory;
    }

    public bool Supports(string scheme) => _factories.ContainsKey(scheme);

    /// <summary>Tries every address of <paramref name="connectString"/> in order; the first that opens wins.</summary>
    /// <param name="connectString">One address or a <c>;</c>-separated list (a lobby's <c>connect</c> metadata).</param>
    /// <param name="connectData">Sent to the server with the connection: the <see cref="MessageRegistry.Fingerprint"/>.</param>
    /// <param name="transport">The opened transport.</param>
    /// <param name="address">The address it connects to.</param>
    public bool TryConnect(string connectString, uint connectData, [NotNullWhen(true)] out ITransport? transport, out NetworkAddress address)
    {
        ArgumentNullException.ThrowIfNull(connectString);
        foreach (var candidate in NetworkAddress.ParseList(connectString))
        {
            if (TryConnect(in candidate, connectData, out transport, out var failure))
            {
                address = candidate;
                return true;
            }

            Log.Info($"[Net] cannot use '{candidate}': {failure}");
        }

        transport = null;
        address = default;
        return false;
    }

    /// <summary>Opens a transport to one address; false (with a reason) when its scheme is unknown or unusable here.</summary>
    public bool TryConnect(in NetworkAddress address, uint connectData, [NotNullWhen(true)] out ITransport? transport, out string? failure)
    {
        if (!_factories.TryGetValue(address.Scheme, out var factory))
        {
            transport = null;
            failure = "no transport for this scheme";
            return false;
        }

        return factory.TryConnect(in address, connectData, out transport, out failure);
    }
}

/// <summary><c>enet:host:port</c> → <see cref="EnetTransport.Connect"/>.</summary>
public sealed class EnetTransportFactory : ITransportFactory
{
    public string Scheme => NetworkAddress.EnetScheme;

    /// <summary>Connection timeout passed to <see cref="EnetTransport.Connect"/> (0: ENet's default).</summary>
    public uint TimeoutMs { get; init; }

    public bool TryConnect(in NetworkAddress address, uint connectData, [NotNullWhen(true)] out ITransport? transport, out string? failure)
    {
        try
        {
            transport = EnetTransport.Connect(address.Host, address.Port, connectData, TimeoutMs);
            failure = null;
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or ArgumentException or InvalidOperationException or BadImageFormatException)
        {
            transport = null;
            failure = e.Message;
            return false;
        }
    }
}

/// <summary><c>steam:&lt;steamId&gt;</c> → <see cref="SteamSocketsTransport.TryConnect"/> (fails while the transport is a stub).</summary>
public sealed class SteamSocketsTransportFactory : ITransportFactory
{
    public string Scheme => NetworkAddress.SteamScheme;

    /// <summary>Steam virtual port.</summary>
    public int VirtualPort { get; init; }

    public bool TryConnect(in NetworkAddress address, uint connectData, [NotNullWhen(true)] out ITransport? transport, out string? failure)
    {
        if (address.SteamId == 0)
        {
            transport = null;
            failure = "not a SteamID";
            return false;
        }

        if (SteamSocketsTransport.TryConnect(address.SteamId, VirtualPort, connectData, out transport))
        {
            failure = null;
            return true;
        }

        failure = SteamSocketsTransport.IsAvailable ? "connection refused" : "Steam Networking Sockets are not available (no Steam natives)";
        return false;
    }
}

/// <summary>
/// <c>loopback:&lt;name&gt;</c> → a client of an in-process <see cref="LoopbackTransport"/> server registered under that
/// name: single-player "listen servers" and tests of the lobby handoff.
/// </summary>
public sealed class LoopbackTransportFactory : ITransportFactory
{
    private readonly Dictionary<string, LoopbackTransport> _servers = new(StringComparer.Ordinal);

    public string Scheme => NetworkAddress.LoopbackScheme;

    /// <summary>Makes <paramref name="server"/> reachable as <c>loopback:<paramref name="name"/></c>.</summary>
    public NetworkAddress Add(string name, LoopbackTransport server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(server);
        if (!server.IsServer)
            throw new ArgumentException("Only server endpoints accept connections.", nameof(server));
        _servers[name] = server;
        return NetworkAddress.Loopback(name);
    }

    public bool TryConnect(in NetworkAddress address, uint connectData, [NotNullWhen(true)] out ITransport? transport, out string? failure)
    {
        if (_servers.TryGetValue(address.Host, out var server) && !server.IsDisposed)
        {
            transport = server.ConnectClient(connectData);
            failure = null;
            return true;
        }

        transport = null;
        failure = "no such loopback server";
        return false;
    }
}
