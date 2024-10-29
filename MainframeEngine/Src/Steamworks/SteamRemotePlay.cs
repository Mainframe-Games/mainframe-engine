namespace MainframeEngine;

// TODO: probably wont use RemotePlay features
// public static class SteamRemotePlay
// {
// 	private static Callback<SteamRemotePlaySessionConnected_t> _remotePlaySessionConnected;
// 	private static Callback<SteamRemotePlaySessionDisconnected_t> _remotePlaySessionDisconnected;
//
// 	public static bool IsActive => Steam.Valid && Steamworks.SteamRemotePlay.GetSessionCount() > 0;
// 	public static uint SessionId => Steam.Valid ? Steamworks.SteamRemotePlay.GetSessionID(0).m_RemotePlaySessionID : 0;
// 	public static List<SteamFriend> Friends { get; } = new List<SteamFriend>();
//
// 	static SteamRemotePlay()
// 	{
// 		_remotePlaySessionConnected = Callback<SteamRemotePlaySessionConnected_t>.Create(SteamRemotePlayOnSessionConnected);
// 		_remotePlaySessionDisconnected = Callback<SteamRemotePlaySessionDisconnected_t>.Create(SteamRemotePlayOnSessionDisconnected);
// 	}
//
// 	private static SteamFriend CreatePlayerFromRemotePlaySessionId(uint sessionId)
// 	{
// 		var steamId = Steamworks.SteamRemotePlay.GetSessionSteamID((RemotePlaySessionID_t)sessionId).m_SteamID;
// 		var username = Steamworks.SteamRemotePlay.GetSessionClientName((RemotePlaySessionID_t)sessionId);
// 		return new SteamFriend(steamId, username);
// 	}
//
// 	private static void SteamRemotePlayOnSessionConnected(SteamRemotePlaySessionConnected_t param)
// 	{
// 		var player = CreatePlayerFromRemotePlaySessionId((uint)param.m_unSessionID);
// 		Friends.Add(player);
// 	}
//
// 	private static void SteamRemotePlayOnSessionDisconnected(SteamRemotePlaySessionDisconnected_t param)
// 	{
// 		var player = CreatePlayerFromRemotePlaySessionId((uint)param.m_unSessionID);
// 		Friends.Remove(player);
// 	}
// }