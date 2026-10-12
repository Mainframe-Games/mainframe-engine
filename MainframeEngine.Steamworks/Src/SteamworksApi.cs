using System.Globalization;
using Steamworks;
using Steamworks.Data;

namespace MainframeEngine.Steamworks;

/// <summary>How <see cref="SteamworksApi.Initialize"/> ended.</summary>
internal enum SteamworksInitResult
{
    Ok,
    NativeLibraryMissing,
    SteamNotRunning,
    Failed,
}

/// <summary>
/// Facepunch.Steamworks behind the engine's <c>Steam</c> facade (its <c>SteamApi</c> adapter calls this). Primitive
/// types only, and no engine reference: the engine passes logging and event sinks in. Call
/// <see cref="Initialize"/> first; everything else only while it succeeded and <see cref="Shutdown"/> has not run.
/// </summary>
/// <remarks>
/// Lessons from Speed Golf's Facepunch integration (2026-09-25): callbacks are pumped by the engine once per frame
/// (<c>asyncCallbacks: false</c>; Facepunch's own loop pumped them a second time); events are subscribed only after a
/// successful init and removed again on shutdown (they are static); <c>Dispatch.OnDebugCallback</c> is never set (it
/// crashed the process with SIGBUS).
/// </remarks>
internal static class SteamworksApi
{
    /// <summary>Warnings (failed calls, timeouts).</summary>
    public static Action<string>? Warning { get; set; }

    /// <summary>Exceptions thrown inside Facepunch's callback dispatch.</summary>
    public static Action<string>? Error { get; set; }

    /// <summary>The user accepted a lobby invite in the overlay or friends list: (lobby id, friend id).</summary>
    public static Action<ulong, ulong>? LobbyInviteAccepted { get; set; }

    /// <summary>A lobby's own metadata changed: (lobby id).</summary>
    public static Action<ulong>? LobbyDataChanged { get; set; }

    /// <summary>A user entered (true) or left a lobby this user is in: (lobby id, user id, joined).</summary>
    public static Action<ulong, ulong, bool>? LobbyMemberChanged { get; set; }

    /// <summary>The user accepted a rich-presence join (a friend's "Join game"): (connect string).</summary>
    public static Action<string>? RichPresenceJoinRequested { get; set; }

    /// <summary><c>SteamClient.Init</c> with callbacks pumped by <see cref="RunCallbacks"/>.</summary>
    public static SteamworksInitResult Initialize(uint appId, out string message)
    {
        SteamworksNative.RegisterResolver();
        try
        {
            // Facepunch sets these too, but only in .NET's copy of the environment on macOS and Linux.
            var id = appId.ToString(CultureInfo.InvariantCulture);
            SteamworksNative.SetEnvironmentVariable("SteamAppId", id);
            SteamworksNative.SetEnvironmentVariable("SteamGameId", id);
            SteamClient.Init(appId, asyncCallbacks: false);
        }
        catch (DllNotFoundException e)
        {
            message = $"{SteamworksNative.FileName} could not be loaded: {e.Message}";
            return SteamworksInitResult.NativeLibraryMissing;
        }
        // NoSteamClient: Steam is installed but not running. On Linux without Steam installed steam_api instead fails
        // generically with "Failed to load module '~/.steam/sdk64/steamclient.so'": the same thing for a player.
        catch (Exception e) when (e.Message.Contains("NoSteamClient", StringComparison.Ordinal)
                                  || e.Message.Contains("steamclient", StringComparison.OrdinalIgnoreCase))
        {
            message = $"Steam client not running: {e.Message}";
            return SteamworksInitResult.SteamNotRunning;
        }
        catch (Exception e)
        {
            message = $"SteamClient.Init failed: {e.Message}";
            return SteamworksInitResult.Failed;
        }

        Dispatch.OnException = e => Error?.Invoke($"Steam callback threw {e}");
        // No RequestCurrentStats: the Steam client syncs stats itself since SDK 1.61.
        Subscribe();
        message = "Steam is running.";
        return SteamworksInitResult.Ok;
    }

    /// <summary><c>SteamAPI_RestartAppIfNecessary</c>; false when steam_api cannot be loaded.</summary>
    public static bool RestartAppIfNecessary(uint appId)
    {
        SteamworksNative.RegisterResolver();
        try
        {
            return SteamClient.RestartAppIfNecessary(appId);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    public static void RunCallbacks() => SteamClient.RunCallbacks();

    public static void Shutdown()
    {
        Unsubscribe();
        Dispatch.OnException = null;
        SteamClient.Shutdown();
    }

    private static void Subscribe()
    {
        Unsubscribe();
        SteamFriends.OnGameLobbyJoinRequested       += OnGameLobbyJoinRequested;
        SteamFriends.OnGameRichPresenceJoinRequested += OnGameRichPresenceJoinRequested;
        SteamMatchmaking.OnLobbyDataChanged          += OnLobbyDataChanged;
        SteamMatchmaking.OnLobbyMemberJoined         += OnLobbyMemberJoined;
        SteamMatchmaking.OnLobbyMemberLeave          += OnLobbyMemberLeft;
        SteamMatchmaking.OnLobbyMemberDisconnected   += OnLobbyMemberLeft;
        SteamMatchmaking.OnLobbyMemberKicked         += OnLobbyMemberKicked;
        SteamMatchmaking.OnLobbyMemberBanned         += OnLobbyMemberKicked;
    }

    private static void Unsubscribe()
    {
        SteamFriends.OnGameLobbyJoinRequested       -= OnGameLobbyJoinRequested;
        SteamFriends.OnGameRichPresenceJoinRequested -= OnGameRichPresenceJoinRequested;
        SteamMatchmaking.OnLobbyDataChanged          -= OnLobbyDataChanged;
        SteamMatchmaking.OnLobbyMemberJoined         -= OnLobbyMemberJoined;
        SteamMatchmaking.OnLobbyMemberLeave          -= OnLobbyMemberLeft;
        SteamMatchmaking.OnLobbyMemberDisconnected   -= OnLobbyMemberLeft;
        SteamMatchmaking.OnLobbyMemberKicked         -= OnLobbyMemberKicked;
        SteamMatchmaking.OnLobbyMemberBanned         -= OnLobbyMemberKicked;
    }

    private static void OnGameLobbyJoinRequested(Lobby lobby, SteamId friend) => LobbyInviteAccepted?.Invoke(lobby.Id, friend);

    private static void OnGameRichPresenceJoinRequested(Friend friend, string connect) =>
        RichPresenceJoinRequested?.Invoke(connect ?? string.Empty);

    private static void OnLobbyDataChanged(Lobby lobby) => LobbyDataChanged?.Invoke(lobby.Id);

    private static void OnLobbyMemberJoined(Lobby lobby, Friend member) => LobbyMemberChanged?.Invoke(lobby.Id, member.Id, true);

    private static void OnLobbyMemberLeft(Lobby lobby, Friend member) => LobbyMemberChanged?.Invoke(lobby.Id, member.Id, false);

    private static void OnLobbyMemberKicked(Lobby lobby, Friend member, Friend by) => LobbyMemberChanged?.Invoke(lobby.Id, member.Id, false);

    // --- User, friends, apps ------------------------------------------------------------------------------------

    public static ulong GetSteamId() => SteamClient.SteamId;

    public static string GetPersonaName() => SteamClient.Name ?? string.Empty;

    public static string GetBranch() => SteamApps.CurrentBetaName ?? string.Empty;

    public static string GetLaunchCommandLine() => SteamApps.CommandLine ?? string.Empty;

    /// <summary>Immediate friends: (Steam id, persona name).</summary>
    public static (ulong Id, string Name)[] GetFriends()
    {
        var friends = new List<(ulong, string)>();
        foreach (var friend in SteamFriends.GetFriends())
            friends.Add((friend.Id, friend.Name ?? string.Empty));
        return [.. friends];
    }

    public static string GetFriendPersonaName(ulong steamId) => new Friend(steamId).Name ?? string.Empty;

    public static bool IsDlcInstalled(uint dlcId) => SteamApps.IsDlcInstalled(dlcId);

    /// <summary>Lobby ids of immediate friends currently in a lobby of <paramref name="appId"/> (their data requested).</summary>
    public static List<ulong> GetFriendLobbyIds(uint appId)
    {
        var ids = new List<ulong>();
        foreach (var friend in SteamFriends.GetFriends())
        {
            if (friend.GameInfo is not { } game || (uint)(game.GameID & 0xFFFFFF) != appId || game.Lobby is not { } lobby)
                continue;
            lobby.Refresh();
            ids.Add(lobby.Id);
        }

        return ids;
    }

    // --- Achievements -------------------------------------------------------------------------------------------

    public static bool IsAchievementUnlocked(string id) => new Achievement(id).State;

    public static bool SetAchievement(string id) => new Achievement(id).Trigger(apply: false);

    public static bool ClearAchievement(string id) => new Achievement(id).Clear();

    public static bool StoreStats() => SteamUserStats.StoreStats();

    // --- Overlay, utils -----------------------------------------------------------------------------------------

    public static bool IsOverlayEnabled() => SteamUtils.IsOverlayEnabled;

    public static void OpenOverlayWebPage(string url) => SteamFriends.OpenWebOverlay(url);

    public static void OpenOverlayStore(uint appId) => SteamFriends.OpenStoreOverlay(appId);

    public static void OpenOverlayInviteDialog(string connect) => SteamworksNative.OpenInviteDialog(connect);

    public static void OpenOverlayRemotePlayInvite() => SteamworksNative.OpenRemotePlayInviteDialog();

    public static string GetIpCountry() => SteamUtils.IpCountry ?? string.Empty;

    // --- Rich presence ------------------------------------------------------------------------------------------

    public static string GetFriendRichPresence(ulong steamId, string key) => new Friend(steamId).GetRichPresence(key) ?? string.Empty;

    /// <summary>Sets a key; a null or empty value deletes it.</summary>
    public static bool SetRichPresence(string key, string? value) => SteamFriends.SetRichPresence(key, value ?? string.Empty);

    public static void ClearRichPresence() => SteamFriends.ClearRichPresence();
}
