using System.Diagnostics;
using Steamworks;

namespace MainframeEngine;

public static class SteamRichPresence
{
	/// <summary>
	/// Gives connect string from rich presence
	/// </summary>
	public static event Action<string>? OnJoinRequested;
	private static Callback<GameRichPresenceJoinRequested_t> _gameRichPresenceJoinRequested;

	static SteamRichPresence()
	{
		_gameRichPresenceJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(OnGameRichPresenceJoinRequested_t);
	}
		
	private static void OnGameRichPresenceJoinRequested_t(GameRichPresenceJoinRequested_t param)
	{
		var connect = GetFriendConnect((ulong)param.m_steamIDFriend);
		OnJoinRequested?.Invoke(connect);
	}
		
	public static int GetFriendGroupSize(ulong steamId)
	{
		if (!Steam.Valid)
			return -1;
			
		var size = SteamFriends.GetFriendRichPresence((CSteamID)steamId, "steam_player_group_size");
		return int.TryParse(size, out var count) ? count : -1;
	}

	public static string GetFriendConnect(ulong steamId)
	{
		if (!Steam.Valid)
			return string.Empty;
			
		var connect = SteamFriends.GetFriendRichPresence((CSteamID)steamId, "connect");
		return connect;
	}

	/// <summary>
	/// Sets group size
	/// <para></para>
	/// src: https://partner.steamgames.com/doc/api/ISteamFriends#SetRichPresence
	/// </summary>
	/// <param name="groupName"></param>
	/// <param name="count"></param>
	public static void SetGroup(string groupName, int count)
	{
		Set("steam_player_group", groupName);
		Set("steam_player_group_size", count.ToString());
	}

	/// <summary>
	/// Sets group size
	/// <para></para>
	/// src: https://partner.steamgames.com/doc/api/ISteamFriends#SetRichPresence
	/// </summary>
	/// <param name="count"></param>
	public static void SetGroup(int count)
	{
		SetGroup(Steam.Username, count);
	}

	public static void SetStatus(string status)
	{
		Set("status", status);
	}

	public static void SetConnect(string connect)
	{
		Set("connect", connect);
	}

	public static void SetDisplay(string key)
	{
		Set("steam_display", key);
	}

	/// <summary>
	/// src: https://partner.steamgames.com/doc/api/ISteamFriends#SetRichPresence
	/// </summary>
	/// <param name="key"></param>
	/// <param name="value"></param>
	public static void Set(string key, string value)
	{
		if (Steam.Valid && !SteamFriends.SetRichPresence(key, value))
			Trace.TraceError($"Could not set rich presence. {key}: {value}");
	}

	public static void Clear()
	{
		if (Steam.Valid)
			SteamFriends.ClearRichPresence();
	}
}