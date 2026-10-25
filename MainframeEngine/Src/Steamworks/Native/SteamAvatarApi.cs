using System.Runtime.CompilerServices;
using MainframeEngine.Steamworks;

namespace MainframeEngine;

/// <summary>
/// Avatar calls behind <see cref="Steam.GetAvatarAsync"/> (Facepunch.Steamworks through <c>MainframeEngine.Steamworks</c>).
/// Same rules as <see cref="SteamApi"/>: no shim types in signatures, never inlined, only called while
/// <see cref="Steam.Valid"/>.
/// </summary>
internal static class SteamAvatarApi
{
    /// <summary>The avatar's RGBA8 pixels, or null; completes on a thread-pool thread (Facepunch polls with <c>Task.Delay</c>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task<(uint Width, uint Height, byte[] Rgba)?> GetAvatarAsync(ulong steamId, SteamAvatarSize size) =>
        SteamworksAvatars.GetAvatarAsync(steamId, (int)size);
}
