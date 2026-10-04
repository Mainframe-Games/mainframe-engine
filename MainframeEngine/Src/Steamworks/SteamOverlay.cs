namespace MainframeEngine;

/// <summary>Steam overlay shortcuts. No-ops without Steam.</summary>
public static class SteamOverlay
{
    public static bool IsEnabled() => Steam.Valid && SteamApi.IsOverlayEnabled();

    public static void OpenWeb(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        if (Steam.Valid)
            SteamApi.OpenOverlayWebPage(url);
    }

    public static void OpenStore(uint appId)
    {
        if (Steam.Valid)
            SteamApi.OpenOverlayStore(appId);
    }

    /// <summary>Opens the overlay's invite dialog; invited friends launch with <c>-connect &lt;connect&gt;</c>.</summary>
    /// <param name="connect">The connect string to join with, e.g. a SteamID or lobby code.</param>
    public static void InviteFriendToGame(string connect)
    {
        ArgumentNullException.ThrowIfNull(connect);
        if (Steam.Valid)
            SteamApi.OpenOverlayInviteDialog($"-connect {connect}");
    }

    public static void InviteFriendToRemotePlay()
    {
        if (Steam.Valid)
            SteamApi.OpenOverlayRemotePlayInvite();
    }
}
