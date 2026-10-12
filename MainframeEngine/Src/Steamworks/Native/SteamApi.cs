using System.Runtime.CompilerServices;
using MainframeEngine.Steamworks;

namespace MainframeEngine;

/// <summary>
/// The engine's side of <c>MainframeEngine.Steamworks</c> (Facepunch.Steamworks; with <see cref="SteamLobbyApi"/>).
/// Every member is <see cref="MethodImplOptions.NoInlining"/> and the class has no field of a shim type: compiling a
/// caller never loads the shim, which only resolves once <see cref="SteamworksLoader.Register"/> ran (and is missing if
/// the app was copied without its <c>steamworks/</c> folder). Callers must check <see cref="Steam.Valid"/> (or
/// <see cref="Steam.IsPlatformSupported"/> for <see cref="Initialize"/>) first.
/// </summary>
internal static class SteamApi
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static (SteamStatus Status, string Message) Initialize(uint appId)
    {
        SteamworksApi.Warning                   = static message => Log.Warning($"[Steam] {message}");
        SteamworksApi.Error                     = static message => Log.Error($"[Steam] {message}");
        SteamworksApi.LobbyInviteAccepted       = SteamLobbyApi.OnInviteAccepted;
        SteamworksApi.LobbyDataChanged          = static lobbyId => SteamLobby.RaiseLobbyUpdated(new SteamLobbyInfo(lobbyId));
        SteamworksApi.LobbyMemberChanged        = SteamLobby.RaiseMemberChanged;
        SteamworksApi.RichPresenceJoinRequested = SteamRichPresence.RaiseJoinRequested;

        var result = SteamworksApi.Initialize(appId, out var message);
        var status = result switch
        {
            SteamworksInitResult.Ok => SteamStatus.Running,
            SteamworksInitResult.NativeLibraryMissing => SteamStatus.NativeLibraryMissing,
            _ when IsSteamMissing(message) => SteamStatus.SteamNotRunning,
            _ => SteamStatus.InitFailed,
        };
        return (status, message);
    }

    /// <summary>
    /// Whether a <c>SteamClient.Init</c> failure means "no Steam client here", which a player sees the same way whatever
    /// steam_api calls it: NoSteamClient (installed, not running), or FailedGeneric when Steam is not installed at all —
    /// "Could not determine Steam client install directory" (Windows, macOS) or "Failed to load module
    /// '~/.steam/sdk64/steamclient.so'" (Linux).
    /// </summary>
    internal static bool IsSteamMissing(string message) =>
        message.Contains("NoSteamClient", StringComparison.Ordinal)
        || message.Contains("steamclient", StringComparison.OrdinalIgnoreCase)
        || message.Contains("Steam client install directory", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>SteamAPI_RestartAppIfNecessary</c>; false without the native library.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool RestartAppIfNecessary(uint appId) => SteamworksApi.RestartAppIfNecessary(appId);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void RunCallbacks() => SteamworksApi.RunCallbacks();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Shutdown() => SteamworksApi.Shutdown();

    // --- User, friends, apps ------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ulong GetSteamId() => SteamworksApi.GetSteamId();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string GetPersonaName() => SteamworksApi.GetPersonaName();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string GetBranch() => SteamworksApi.GetBranch();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string GetLaunchCommandLine() => SteamworksApi.GetLaunchCommandLine();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static SteamFriend[] GetFriends() => Array.ConvertAll(SteamworksApi.GetFriends(), static f => new SteamFriend(f.Id, f.Name));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string GetFriendPersonaName(ulong steamId) => SteamworksApi.GetFriendPersonaName(steamId);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsDlcInstalled(uint dlcId) => SteamworksApi.IsDlcInstalled(dlcId);

    /// <summary>Lobby ids of immediate friends currently in a lobby of this game.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static List<ulong> GetFriendLobbyIds(uint appId) => SteamworksApi.GetFriendLobbyIds(appId);

    // --- Achievements -------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsAchievementUnlocked(string id) => SteamworksApi.IsAchievementUnlocked(id);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SetAchievement(string id) => SteamworksApi.SetAchievement(id);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ClearAchievement(string id) => SteamworksApi.ClearAchievement(id);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool StoreStats() => SteamworksApi.StoreStats();

    // --- Overlay ------------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsOverlayEnabled() => SteamworksApi.IsOverlayEnabled();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OpenOverlayWebPage(string url) => SteamworksApi.OpenOverlayWebPage(url);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OpenOverlayStore(uint appId) => SteamworksApi.OpenOverlayStore(appId);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OpenOverlayInviteDialog(string connect) => SteamworksApi.OpenOverlayInviteDialog(connect);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OpenOverlayRemotePlayInvite() => SteamworksApi.OpenOverlayRemotePlayInvite();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string GetIpCountry() => SteamworksApi.GetIpCountry();

    // --- Rich presence ------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string GetFriendRichPresence(ulong steamId, string key) => SteamworksApi.GetFriendRichPresence(steamId, key);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SetRichPresence(string key, string? value) => SteamworksApi.SetRichPresence(key, value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ClearRichPresence() => SteamworksApi.ClearRichPresence();
}
