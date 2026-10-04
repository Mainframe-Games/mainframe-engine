using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Steamworks;

namespace MainframeEngine;

public class SteamLobbyInfo(ulong lobbyId) : IEquatable<SteamLobbyInfo>, IComparable<SteamLobbyInfo>
{
    public ulong LobbyId { get; } = lobbyId;
    public bool IsHost => HostId == Steam.SteamId;
    public int PlayerCount => SteamMatchmaking.GetNumLobbyMembers((CSteamID)LobbyId);
    public int MaxPlayers => SteamMatchmaking.GetLobbyMemberLimit((CSteamID)LobbyId);
    /// <summary>
    /// Returns lobby owner id, Unlike <see cref="HostId"/> you need to be in the lobby to get this value.
    /// </summary>
    public ulong OwnerId => SteamMatchmaking.GetLobbyOwner((CSteamID)LobbyId).m_SteamID;

    public ulong HostId
    {
        get => ulong.Parse(GetData(nameof(HostId)), CultureInfo.InvariantCulture);
        set => SetData(nameof(HostId), value.ToString(CultureInfo.InvariantCulture));
    }

    public string LobbyName
    {
        get => GetData(nameof(LobbyName));
        set => SetData(nameof(LobbyName), value);
    }

    public string AppVersion
    {
        get => GetData(nameof(AppVersion));
        set => SetData(nameof(AppVersion), value);
    }

    public bool IsAdvertising
    {
        get => GetData(nameof(IsAdvertising)) == true.ToString();
        set => SetData(nameof(IsAdvertising), value.ToString());
    }

    public string Country
    {
        get => GetData(nameof(Country));
        set => SetData(nameof(Country), value);
    }

    public override string ToString()
    {
        var str = new StringBuilder();

        var properties = GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);

        foreach (var propertyInfo in properties)
            str.Append(propertyInfo.Name).Append(": ").Append(propertyInfo.GetValue(this)).AppendLine();

        return str.ToString();
    }

    public IEnumerable<SteamFriend> GetPlayers()
    {
        var id = (CSteamID)LobbyId;
        var count = SteamMatchmaking.GetNumLobbyMembers(id);
        for (int i = 0; i < count; i++)
        {
            var memberId = SteamMatchmaking.GetLobbyMemberByIndex(id, i);
            var username = SteamFriends.GetFriendPersonaName(memberId);
            var player = new SteamFriend(memberId.m_SteamID, username);
            yield return player;
        }
    }

    private string GetData(string key)
    {
        return SteamMatchmaking.GetLobbyData((CSteamID)LobbyId, key);
    }

    private void SetData(string key, string value)
    {
        if (!SteamMatchmaking.SetLobbyData((CSteamID)LobbyId, key, value))
            Trace.TraceError($"Failed to set lobby data. {key}: {value}");
    }

    public bool Equals(SteamLobbyInfo? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return LobbyId == other.LobbyId;
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((SteamLobbyInfo)obj);
    }

    public override int GetHashCode()
    {
        return LobbyId.GetHashCode();
    }

    public int CompareTo(SteamLobbyInfo? other)
    {
        if (ReferenceEquals(this, other)) return 0;
        if (ReferenceEquals(null, other)) return 1;
        return LobbyId.CompareTo(other.LobbyId);
    }

    public static bool operator ==(SteamLobbyInfo? x, SteamLobbyInfo? y)
    {
        if (x is null && y is null)
            return true;

        return x?.Equals(y) ?? false;
    }
    public static bool operator !=(SteamLobbyInfo? x, SteamLobbyInfo? y)
    {
        return !(x == y);
    }

    public static bool operator <(SteamLobbyInfo? x, SteamLobbyInfo? y) => Compare(x, y) < 0;
    public static bool operator <=(SteamLobbyInfo? x, SteamLobbyInfo? y) => Compare(x, y) <= 0;
    public static bool operator >(SteamLobbyInfo? x, SteamLobbyInfo? y) => Compare(x, y) > 0;
    public static bool operator >=(SteamLobbyInfo? x, SteamLobbyInfo? y) => Compare(x, y) >= 0;

    private static int Compare(SteamLobbyInfo? x, SteamLobbyInfo? y) => x is null ? (y is null ? 0 : -1) : x.CompareTo(y);
}