using Steamworks;

namespace MainframeEngine.Steamworks;

/// <summary>
/// Facepunch.Steamworks avatars behind the engine's <c>Steam.GetAvatarAsync</c>. Same rules as
/// <see cref="SteamworksApi"/>: primitive types only, called only while Steam runs.
/// </summary>
/// <remarks>
/// Facepunch first asks Steam for the user's persona (<c>RequestUserInformation</c>), then polls until the image is
/// downloaded, with <c>Task.Delay</c>: the task completes on a thread-pool thread, so the engine moves the result to the
/// main thread itself (inside <c>Steam.RunCallbacks</c>) and adds the timeout.
/// </remarks>
internal static class SteamworksAvatars
{
    /// <summary>
    /// The avatar of <paramref name="steamId"/> as tightly packed RGBA8 (straight alpha, sRGB): <paramref name="size"/>
    /// 0 = small (32×32), 1 = medium (64×64), 2 = large (184×184). Null when Steam has no image for the user.
    /// </summary>
    public static async Task<(uint Width, uint Height, byte[] Rgba)?> GetAvatarAsync(ulong steamId, int size)
    {
        var request = size switch
        {
            0 => SteamFriends.GetSmallAvatarAsync(steamId),
            1 => SteamFriends.GetMediumAvatarAsync(steamId),
            _ => SteamFriends.GetLargeAvatarAsync(steamId),
        };
        if (await request.ConfigureAwait(false) is not { } image || image.Data is null || image.Width == 0 || image.Height == 0)
            return null;
        return (image.Width, image.Height, image.Data);
    }
}
