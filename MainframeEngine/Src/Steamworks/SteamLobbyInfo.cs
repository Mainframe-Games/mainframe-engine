using System.Globalization;

namespace MainframeEngine;

/// <summary>
/// A Steam lobby by id. Properties read and write live lobby metadata (not cached); without Steam they return
/// empty/zero and setters do nothing. Writing metadata requires being the lobby owner.
/// </summary>
public sealed class SteamLobbyInfo(ulong lobbyId) : IEquatable<SteamLobbyInfo>, IComparable<SteamLobbyInfo>
{
    public ulong LobbyId { get; } = lobbyId;

    /// <summary>True when the stored <see cref="HostId"/> is this user.</summary>
    public bool IsHost => Steam.Valid && HostId == Steam.SteamId;

    public int PlayerCount => Steam.Valid ? SteamLobbyApi.GetMemberCount(LobbyId) : 0;

    public int MaxPlayers => Steam.Valid ? SteamLobbyApi.GetMemberLimit(LobbyId) : 0;

    /// <summary>Lobby owner. Unlike <see cref="HostId"/> you need to be in the lobby to read it.</summary>
    public ulong OwnerId => Steam.Valid ? SteamLobbyApi.GetOwner(LobbyId) : 0;

    /// <summary>Host SteamID stored in the metadata; 0 when missing or invalid.</summary>
    public ulong HostId
    {
        get => ulong.TryParse(GetData(nameof(HostId)), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
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
        get => string.Equals(GetData(nameof(IsAdvertising)), bool.TrueString, StringComparison.OrdinalIgnoreCase);
        set => SetData(nameof(IsAdvertising), value ? bool.TrueString : bool.FalseString);
    }

    public string Country
    {
        get => GetData(nameof(Country));
        set => SetData(nameof(Country), value);
    }

    /// <summary>Lobby members; empty without Steam.</summary>
    public IReadOnlyList<SteamFriend> GetPlayers() => Steam.Valid ? SteamLobbyApi.GetMembers(LobbyId) : [];

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"Lobby {LobbyId} '{LobbyName}' ({PlayerCount}/{MaxPlayers}), host {HostId}, version '{AppVersion}', country '{Country}'");

    private string GetData(string key) => Steam.Valid ? SteamLobbyApi.GetLobbyData(LobbyId, key) : string.Empty;

    private void SetData(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Steam.Valid && !SteamLobbyApi.SetLobbyData(LobbyId, key, value))
            Log.Warning($"[Steam] could not set lobby {LobbyId} data {key} = '{value}'");
    }

    public bool Equals(SteamLobbyInfo? other) => other is not null && LobbyId == other.LobbyId;

    public override bool Equals(object? obj) => obj is SteamLobbyInfo other && Equals(other);

    public override int GetHashCode() => LobbyId.GetHashCode();

    public int CompareTo(SteamLobbyInfo? other) => other is null ? 1 : LobbyId.CompareTo(other.LobbyId);

    public static bool operator ==(SteamLobbyInfo? x, SteamLobbyInfo? y) => x?.Equals(y) ?? y is null;

    public static bool operator !=(SteamLobbyInfo? x, SteamLobbyInfo? y) => !(x == y);

    public static bool operator <(SteamLobbyInfo? x, SteamLobbyInfo? y) => Compare(x, y) < 0;

    public static bool operator <=(SteamLobbyInfo? x, SteamLobbyInfo? y) => Compare(x, y) <= 0;

    public static bool operator >(SteamLobbyInfo? x, SteamLobbyInfo? y) => Compare(x, y) > 0;

    public static bool operator >=(SteamLobbyInfo? x, SteamLobbyInfo? y) => Compare(x, y) >= 0;

    private static int Compare(SteamLobbyInfo? x, SteamLobbyInfo? y) => x is null ? (y is null ? 0 : -1) : x.CompareTo(y);
}
