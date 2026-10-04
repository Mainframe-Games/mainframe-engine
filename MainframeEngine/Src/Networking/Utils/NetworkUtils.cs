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

    public static string[] RegionKeys => [RegionOce, RegionUse, RegionUsw, RegionEu, RegionAsia];

    public static readonly Dictionary<string, string> Regions = new()
    {
        [RegionOce] = "Australia / NZ",
        [RegionUse] = "US East",
        [RegionUsw] = "US West",
        [RegionEu] = "Europe",
        [RegionAsia] = "Asia",
    };

    /// <summary>
    /// Retrieves the primary local IPv4 address of the current machine by creating a UDP socket and
    /// connecting to an external address without sending any data. This operation allows the operating
    /// system to determine the appropriate local network interface.
    /// </summary>
    /// <returns>
    /// The primary local IPv4 address as an <see cref="IPAddress"/> instance.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the primary local IPv4 address cannot be determined or no suitable network interface
    /// is available.
    /// </exception>
    public static IPAddress GetPrimaryLocalIPv4()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // The address/port here is never actually contacted; it’s used so the OS selects an interface
        socket.Connect("8.8.8.8", 80);
        if (socket.LocalEndPoint is IPEndPoint ep)
            return ep.Address;
        throw new InvalidOperationException("Could not determine local IPv4 address.");
    }

    /// <summary>
    /// Retrieves the public IP address of the current machine by making an HTTP request
    /// to an external service (https://api.ipify.org).
    /// </summary>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains
    /// the public IP address as a string.
    /// </returns>
    /// <exception cref="HttpRequestException">
    /// Thrown if there is an issue with the HTTP request while trying to retrieve the public IP address.
    /// </exception>
    public static async Task<string> GetPublicIpAsync()
    {
        var ip = Environment.GetEnvironmentVariable("PUBLIC_IP");
        if (!string.IsNullOrWhiteSpace(ip))
            return ip;

        using var http = new HttpClient();
        ip = await http.GetStringAsync("https://api.ipify.org");
        return ip.Trim();
    }

    /// <summary>
    /// Generates a new unique server identifier that incorporates the current region and a GUID.
    /// The region is determined by reading the "REGION" environment variable. If the variable is not set,
    /// the default region will be set to "unknown".
    /// </summary>
    /// <returns>
    /// A string representing the new server identifier in the format "<region>-<guid>".
    /// </returns>
    public static string GetServerNewId(in string? inRegion = null)
    {
        var region = inRegion ?? GetRegion();
        var serverId = $"{region}-{Guid.NewGuid():N}";
        return serverId;
    }

    /// <summary>
    /// Retrieves the region identifier for the current environment by reading the
    /// "REGION" environment variable. If the environment variable is not set,
    /// a default value of "unknown" is returned.
    /// </summary>
    /// <returns>
    /// The region identifier as a string. Returns "unknown" if the "REGION"
    /// environment variable is not defined.
    /// </returns>
    public static string GetRegion()
    {
        return Environment.GetEnvironmentVariable("REGION") ?? "unknown";
    }
}