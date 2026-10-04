using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;

namespace MainframeEngine.Networking;

public static class NetworkUtils
{
    public const ushort DefaultPort = 6969;

    public const string RegionOce = "oce";
    public const string RegionUse = "use";
    public const string RegionUsw = "usw";
    public const string RegionEu = "eu";
    public const string RegionAsia = "asia";

    /// <summary>Region keys, in display order.</summary>
    public static IReadOnlyList<string> RegionKeys { get; } = [RegionOce, RegionUse, RegionUsw, RegionEu, RegionAsia];

    /// <summary>Region key → display name.</summary>
    public static IReadOnlyDictionary<string, string> Regions { get; } = new Dictionary<string, string>
    {
        [RegionOce] = "Australia / NZ",
        [RegionUse] = "US East",
        [RegionUsw] = "US West",
        [RegionEu] = "Europe",
        [RegionAsia] = "Asia",
    }.ToFrozenDictionary();

    // One client for the process (a client per call exhausts sockets); created on first use, not at type init.
    private static readonly Lazy<HttpClient> Http = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(10) });

    /// <summary>
    /// The primary local IPv4 address: the interface the OS would route public traffic through. Opens a UDP socket
    /// and "connects" it without sending anything.
    /// </summary>
    /// <returns>False (and <see cref="IPAddress.Loopback"/>) when there is no IPv4 route, e.g. offline.</returns>
    public static bool TryGetPrimaryLocalIPv4(out IPAddress address)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // Never contacted: connecting a UDP socket only makes the OS choose the outgoing interface.
            socket.Connect("8.8.8.8", 80);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
            {
                address = endPoint.Address;
                return true;
            }
        }
        catch (SocketException)
        {
        }

        address = IPAddress.Loopback;
        return false;
    }

    /// <summary>Like <see cref="TryGetPrimaryLocalIPv4"/>, but throws when there is no IPv4 route.</summary>
    /// <exception cref="InvalidOperationException">No suitable network interface.</exception>
    public static IPAddress GetPrimaryLocalIPv4() =>
        TryGetPrimaryLocalIPv4(out var address)
            ? address
            : throw new InvalidOperationException("Could not determine the local IPv4 address.");

    /// <summary>
    /// The machine's public IP: the <c>PUBLIC_IP</c> environment variable if set, otherwise asked from
    /// https://api.ipify.org (10 s timeout).
    /// </summary>
    /// <exception cref="HttpRequestException">The lookup failed.</exception>
    public static async Task<string> GetPublicIpAsync(CancellationToken cancellationToken = default)
    {
        var ip = Environment.GetEnvironmentVariable("PUBLIC_IP");
        if (!string.IsNullOrWhiteSpace(ip))
            return ip;

        ip = await Http.Value.GetStringAsync(new Uri("https://api.ipify.org"), cancellationToken).ConfigureAwait(false);
        return ip.Trim();
    }

    /// <summary>A new server id, <c>&lt;region&gt;-&lt;guid&gt;</c>; the region defaults to <see cref="GetRegion"/>.</summary>
    public static string GetServerNewId(in string? inRegion = null)
    {
        var region = inRegion ?? GetRegion();
        return $"{region}-{Guid.NewGuid():N}";
    }

    /// <summary>The <c>REGION</c> environment variable, or <c>"unknown"</c>.</summary>
    public static string GetRegion() => Environment.GetEnvironmentVariable("REGION") ?? "unknown";
}
