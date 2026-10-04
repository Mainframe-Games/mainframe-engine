using System.Globalization;

namespace MainframeEngine;

/// <summary>Steam rich presence (<see href="https://partner.steamgames.com/doc/api/ISteamFriends#SetRichPresence"/>). No-ops without Steam.</summary>
public static class SteamRichPresence
{
    /// <summary>A friend asked to join through rich presence; gives the connect string.</summary>
    public static event Action<string>? OnJoinRequested;

    internal static void RaiseJoinRequested(string connect) => OnJoinRequested?.Invoke(connect);

    /// <summary>A friend's <c>steam_player_group_size</c>, or -1.</summary>
    public static int GetFriendGroupSize(ulong steamId)
    {
        if (!Steam.Valid)
            return -1;

        var size = SteamApi.GetFriendRichPresence(steamId, "steam_player_group_size");
        return int.TryParse(size, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : -1;
    }

    /// <summary>A friend's <c>connect</c> string, or empty.</summary>
    public static string GetFriendConnect(ulong steamId) =>
        Steam.Valid ? SteamApi.GetFriendRichPresence(steamId, "connect") : string.Empty;

    /// <summary>Sets <c>steam_player_group</c> and <c>steam_player_group_size</c>.</summary>
    public static void SetGroup(string groupName, int count)
    {
        Set("steam_player_group", groupName);
        Set("steam_player_group_size", count.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Sets the group to this user's name with <paramref name="count"/> members.</summary>
    public static void SetGroup(int count) => SetGroup(Steam.Username, count);

    public static void SetStatus(string status) => Set("status", status);

    public static void SetConnect(string connect) => Set("connect", connect);

    public static void SetDisplay(string key) => Set("steam_display", key);

    public static void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        if (Steam.Valid && !SteamApi.SetRichPresence(key, value))
            Log.Warning($"[Steam] could not set rich presence {key} = '{value}'");
    }

    public static void Clear()
    {
        if (Steam.Valid)
            SteamApi.ClearRichPresence();
    }
}
