using Steamworks;

namespace MainframeEngine;

public static class SteamOverlay
{
    public static bool IsEnabled()
    {
        return Steam.Valid && SteamUtils.IsOverlayEnabled();
    }

    public static void OpenWeb(string url)
    {
        if (Steam.Valid)
            SteamFriends.ActivateGameOverlayToWebPage(url);
    }

    public static void OpenStore(uint dlcId)
    {
        if (Steam.Valid)
            SteamFriends.ActivateGameOverlayToStore((AppId_t)dlcId, EOverlayToStoreFlag.k_EOverlayToStoreFlag_None);
    }

    /// <summary>
    /// Activate steam overload to show friends invite list
    /// </summary>
    /// <param name="connect">The connect string of the user to connect to. Can be SteamId or Lobby Code etc...</param>
    public static void InviteFriendToGame(string connect)
    {
        if (Steam.Valid)
            SteamFriends.ActivateGameOverlayInviteDialogConnectString($"-connect {connect}");
    }

    public static void InviteFriendToRemotePlay()
    {
        if (Steam.Valid)
            SteamFriends.ActivateGameOverlayRemotePlayTogetherInviteDialog(CSteamID.Nil);
    }
}