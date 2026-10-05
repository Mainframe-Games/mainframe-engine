using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Steamworks;

namespace MainframeEngine;

/// <summary>
/// The only code that touches Steamworks.NET (with <see cref="SteamLobbyApi"/>). Every member is
/// <see cref="MethodImplOptions.NoInlining"/> and has no Steamworks type in its signature, and the class has no
/// Steamworks-typed static fields: compiling a caller therefore never loads the Steamworks.NET assembly, which
/// cannot load at all in arm64 processes. Callers must check <see cref="Steam.Valid"/> (or
/// <see cref="Steam.IsPlatformSupported"/> for <see cref="Initialize"/>) first.
/// </summary>
internal static class SteamApi
{
    private static Callbacks? _callbacks;

    /// <summary>Steam callbacks registered while Steam runs (a class so the fields' types load lazily).</summary>
    private sealed class Callbacks : IDisposable
    {
        private readonly Callback<GameLobbyJoinRequested_t>        _lobbyJoinRequested;
        private readonly Callback<LobbyDataUpdate_t>               _lobbyDataUpdated;
        private readonly Callback<LobbyChatUpdate_t>               _lobbyChatUpdated;
        private readonly Callback<GameRichPresenceJoinRequested_t> _richPresenceJoinRequested;

        public Callbacks()
        {
            _lobbyJoinRequested        = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);
            _lobbyDataUpdated          = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdated);
            _lobbyChatUpdated          = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdated);
            _richPresenceJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(OnRichPresenceJoinRequested);
        }

        // Handlers live here, not on SteamApi, so no SteamApi method signature mentions a Steamworks type.
        private static void OnLobbyJoinRequested(GameLobbyJoinRequested_t param) =>
            SteamLobbyApi.OnInviteAccepted(param.m_steamIDLobby.m_SteamID, param.m_steamIDFriend.m_SteamID);

        private static void OnLobbyDataUpdated(LobbyDataUpdate_t param)
        {
            // Member data updates (m_ulSteamIDMember != lobby) are not surfaced yet.
            if (param.m_bSuccess != 0 && param.m_ulSteamIDMember == param.m_ulSteamIDLobby)
                SteamLobby.RaiseLobbyUpdated(new SteamLobbyInfo(param.m_ulSteamIDLobby));
        }

        private static void OnLobbyChatUpdated(LobbyChatUpdate_t param)
        {
            var change = (EChatMemberStateChange)param.m_rgfChatMemberStateChange;
            var joined = (change & EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0;
            SteamLobby.RaiseMemberChanged(param.m_ulSteamIDLobby, param.m_ulSteamIDUserChanged, joined);
        }

        private static void OnRichPresenceJoinRequested(GameRichPresenceJoinRequested_t param) =>
            SteamRichPresence.RaiseJoinRequested(param.m_rgchConnect ?? string.Empty);

        public void Dispose()
        {
            _lobbyJoinRequested.Dispose();
            _lobbyDataUpdated.Dispose();
            _lobbyChatUpdated.Dispose();
            _richPresenceJoinRequested.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static (SteamStatus Status, string Message) Initialize(string nativeFileName)
    {
        if (!TryLoadNative())
        {
            return (SteamStatus.NativeLibraryMissing,
                $"'{nativeFileName}' was not found next to the app. It comes from the Steamworks SDK " +
                $"(redistributable_bin) and is not shipped yet; place it in MainframeEngine/runtimes/{RuntimeInformation.RuntimeIdentifier}/native/.");
        }

        if (!Packsize.Test())
            return (SteamStatus.InitFailed, "Steamworks.NET Packsize test failed: wrong Steamworks.NET build for this platform.");
        if (!DllCheck.Test())
            return (SteamStatus.InitFailed, "Steamworks.NET DllCheck failed: the steam_api native does not match Steamworks.NET 2024.8.0 (SDK 1.60).");

        var result = SteamAPI.InitEx(out var error);
        switch (result)
        {
            case ESteamAPIInitResult.k_ESteamAPIInitResult_OK:
                break;
            case ESteamAPIInitResult.k_ESteamAPIInitResult_NoSteamClient:
                return (SteamStatus.SteamNotRunning, $"Steam client not running: {error}");
            default:
                return (SteamStatus.InitFailed, $"SteamAPI_InitEx returned {result}: {error}");
        }

        _callbacks?.Dispose();
        _callbacks = new Callbacks();
        if (!SteamUserStats.RequestCurrentStats())
            Log.Warning("[Steam] RequestCurrentStats failed; achievements may be unavailable");
        return (SteamStatus.Running, "Steam is running.");
    }

    // Same name and probing as Steamworks.NET's own DllImport (steam_api64 on Windows x64, steam_api elsewhere).
    private static bool TryLoadNative()
    {
        var importName = OperatingSystem.IsWindows() && Environment.Is64BitProcess ? "steam_api64" : "steam_api";
        return NativeLibrary.TryLoad(importName, typeof(SteamAPI).Assembly, null, out _);
    }

    /// <summary><c>SteamAPI_RestartAppIfNecessary</c>; false without the native library.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool RestartAppIfNecessary(uint appId) => TryLoadNative() && SteamAPI.RestartAppIfNecessary(new AppId_t(appId));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void RunCallbacks() => SteamAPI.RunCallbacks();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Shutdown()
    {
        _callbacks?.Dispose();
        _callbacks = null;
        SteamAPI.Shutdown();
    }

    // --- User, friends, apps ------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ulong GetSteamId() => SteamUser.GetSteamID().m_SteamID;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string GetPersonaName() => SteamFriends.GetPersonaName();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string GetBranch() => SteamApps.GetCurrentBetaName(out var name, 128) ? name : string.Empty;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string GetLaunchCommandLine() =>
        SteamApps.GetLaunchCommandLine(out var commandLine, 1024) >= 0 ? commandLine : string.Empty;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static SteamFriend[] GetFriends()
    {
        const EFriendFlags flags = EFriendFlags.k_EFriendFlagAll;
        var count = Math.Max(0, SteamFriends.GetFriendCount(flags));
        var friends = new SteamFriend[count];
        for (var i = 0; i < count; i++)
        {
            var id = SteamFriends.GetFriendByIndex(i, flags);
            friends[i] = new SteamFriend(id.m_SteamID, SteamFriends.GetFriendPersonaName(id));
        }

        return friends;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string GetFriendPersonaName(ulong steamId) => SteamFriends.GetFriendPersonaName(new CSteamID(steamId));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsDlcInstalled(uint dlcId) => SteamApps.BIsDlcInstalled(new AppId_t(dlcId));

    /// <summary>Lobby ids of immediate friends currently in a lobby of this game.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static List<ulong> GetFriendLobbyIds(uint appId)
    {
        var ids   = new List<ulong>();
        var count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
        for (var i = 0; i < count; i++)
        {
            var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
            if (!SteamFriends.GetFriendGamePlayed(friend, out var game))
                continue;
            if (game.m_gameID.AppID().m_AppId != appId || !game.m_steamIDLobby.IsValid())
                continue;
            SteamMatchmaking.RequestLobbyData(game.m_steamIDLobby);
            ids.Add(game.m_steamIDLobby.m_SteamID);
        }

        return ids;
    }

    // --- Achievements -------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsAchievementUnlocked(string id) => SteamUserStats.GetAchievement(id, out var achieved) && achieved;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SetAchievement(string id) => SteamUserStats.SetAchievement(id);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ClearAchievement(string id) => SteamUserStats.ClearAchievement(id);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool StoreStats() => SteamUserStats.StoreStats();

    // --- Overlay ------------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsOverlayEnabled() => SteamUtils.IsOverlayEnabled();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OpenOverlayWebPage(string url) => SteamFriends.ActivateGameOverlayToWebPage(url);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OpenOverlayStore(uint appId) =>
        SteamFriends.ActivateGameOverlayToStore(new AppId_t(appId), EOverlayToStoreFlag.k_EOverlayToStoreFlag_None);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OpenOverlayInviteDialog(string connect) => SteamFriends.ActivateGameOverlayInviteDialogConnectString(connect);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OpenOverlayRemotePlayInvite() => SteamFriends.ActivateGameOverlayRemotePlayTogetherInviteDialog(CSteamID.Nil);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string GetIpCountry() => SteamUtils.GetIPCountry();

    // --- Rich presence ------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string GetFriendRichPresence(ulong steamId, string key) => SteamFriends.GetFriendRichPresence(new CSteamID(steamId), key);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SetRichPresence(string key, string? value) => SteamFriends.SetRichPresence(key, value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ClearRichPresence() => SteamFriends.ClearRichPresence();
}
