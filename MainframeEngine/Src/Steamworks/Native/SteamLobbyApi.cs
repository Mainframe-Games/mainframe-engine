using System.Runtime.CompilerServices;
using MainframeEngine.Steamworks;

namespace MainframeEngine;

/// <summary>
/// Matchmaking calls behind <see cref="SteamLobby"/> and <see cref="SteamLobbyInfo"/> (Facepunch.Steamworks through
/// <c>MainframeEngine.Steamworks</c>). Same rules as <see cref="SteamApi"/>: no shim types in signatures, never inlined,
/// only called while <see cref="Steam.Valid"/>. Async results complete inside <see cref="Steam.RunCallbacks"/>; this
/// adds the timeout and cancellation.
/// </summary>
internal static class SteamLobbyApi
{
    /// <summary>Creates a lobby; returns its id, or 0 on failure or timeout.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task<ulong> CreateLobbyAsync(bool friendsOnly, int maxPlayers, TimeSpan timeout, CancellationToken cancellationToken) =>
        WaitAsync(SteamworksLobbies.CreateLobbyAsync(friendsOnly, maxPlayers), timeout, 0ul, "CreateLobby", cancellationToken);

    /// <summary>Joins a lobby; true once Steam confirms the enter.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task<bool> JoinLobbyAsync(ulong lobbyId, TimeSpan timeout, CancellationToken cancellationToken) =>
        WaitAsync(SteamworksLobbies.JoinLobbyAsync(lobbyId), timeout, false, "JoinLobby", cancellationToken);

    /// <summary>Lobby ids from a lobby search (at most <paramref name="max"/>); empty on failure or timeout.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task<List<ulong>> RequestLobbyListAsync(int max, TimeSpan timeout, CancellationToken cancellationToken) =>
        WaitAsync(SteamworksLobbies.RequestLobbyListAsync(max), timeout, [], "RequestLobbyList", cancellationToken);

    private static async Task<T> WaitAsync<T>(Task<T> task, TimeSpan timeout, T fallback, string operation, CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warning($"[Steam] {operation} timed out after {timeout.TotalSeconds:0.#} s");
            return fallback;
        }
    }

    /// <summary>The user accepted an invite (overlay or friends list).</summary>
    public static void OnInviteAccepted(ulong lobbyId, ulong friendId)
    {
        Log.Info($"[Steam] invite from {friendId} accepted, joining lobby {lobbyId}");
        _ = SteamLobby.JoinFromInviteAsync(lobbyId);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void LeaveLobby(ulong lobbyId) => SteamworksLobbies.LeaveLobby(lobbyId);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string GetLobbyData(ulong lobbyId, string key) => SteamworksLobbies.GetLobbyData(lobbyId, key);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SetLobbyData(ulong lobbyId, string key, string value) => SteamworksLobbies.SetLobbyData(lobbyId, key, value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int GetMemberCount(ulong lobbyId) => SteamworksLobbies.GetMemberCount(lobbyId);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int GetMemberLimit(ulong lobbyId) => SteamworksLobbies.GetMemberLimit(lobbyId);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ulong GetOwner(ulong lobbyId) => SteamworksLobbies.GetOwner(lobbyId);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static SteamFriend[] GetMembers(ulong lobbyId) =>
        Array.ConvertAll(SteamworksLobbies.GetMembers(lobbyId), static m => new SteamFriend(m.Id, m.Name));
}
