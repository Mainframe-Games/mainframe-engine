using System.Runtime.CompilerServices;
using Steamworks;

namespace MainframeEngine;

/// <summary>
/// Steamworks.NET matchmaking calls behind <see cref="SteamLobby"/> and <see cref="SteamLobbyInfo"/>. Same rules as
/// <see cref="SteamApi"/>: no Steamworks types in signatures, never inlined, only called while <see cref="Steam.Valid"/>.
/// Request/response calls use <c>CallResult&lt;T&gt;</c> (bound to the call handle), not global callbacks.
/// </summary>
internal static class SteamLobbyApi
{
    /// <summary>Creates a lobby; returns its id, or 0 on failure or timeout.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<ulong> CreateLobbyAsync(bool friendsOnly, int maxPlayers, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var type = friendsOnly ? ELobbyType.k_ELobbyTypeFriendsOnly : ELobbyType.k_ELobbyTypePublic;
        var done = new TaskCompletionSource<ulong>();
        using var call = CallResult<LobbyCreated_t>.Create((result, ioFailure) =>
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                Log.Warning($"[Steam] CreateLobby failed: {(ioFailure ? "I/O failure" : result.m_eResult.ToString())}");
                done.TrySetResult(0);
                return;
            }

            done.TrySetResult(result.m_ulSteamIDLobby);
        });
        call.Set(SteamMatchmaking.CreateLobby(type, maxPlayers));
        return await WaitAsync(done.Task, timeout, 0ul, "CreateLobby", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Joins a lobby; true once Steam confirms the enter.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<bool> JoinLobbyAsync(ulong lobbyId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<bool>();
        using var call = CallResult<LobbyEnter_t>.Create((result, ioFailure) =>
        {
            var response = (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse;
            var success  = !ioFailure && response == EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess;
            if (!success)
                Log.Warning($"[Steam] JoinLobby {lobbyId} failed: {(ioFailure ? "I/O failure" : response.ToString())}");
            done.TrySetResult(success);
        });
        call.Set(SteamMatchmaking.JoinLobby(new CSteamID(lobbyId)));
        return await WaitAsync(done.Task, timeout, false, "JoinLobby", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lobby ids from a lobby search (at most <paramref name="max"/>); empty on failure or timeout.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<List<ulong>> RequestLobbyListAsync(int max, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<List<ulong>>();
        using var call = CallResult<LobbyMatchList_t>.Create((result, ioFailure) =>
        {
            var ids = new List<ulong>();
            if (ioFailure)
            {
                Log.Warning("[Steam] RequestLobbyList failed: I/O failure");
                done.TrySetResult(ids);
                return;
            }

            for (var i = 0; i < (int)result.m_nLobbiesMatching; i++)
            {
                var id = SteamMatchmaking.GetLobbyByIndex(i);
                SteamMatchmaking.RequestLobbyData(id);
                ids.Add(id.m_SteamID);
            }

            done.TrySetResult(ids);
        });
        SteamMatchmaking.AddRequestLobbyListResultCountFilter(max);
        call.Set(SteamMatchmaking.RequestLobbyList());
        return await WaitAsync(done.Task, timeout, [], "RequestLobbyList", cancellationToken).ConfigureAwait(false);
    }

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
    public static void LeaveLobby(ulong lobbyId) => SteamMatchmaking.LeaveLobby(new CSteamID(lobbyId));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string GetLobbyData(ulong lobbyId, string key) => SteamMatchmaking.GetLobbyData(new CSteamID(lobbyId), key);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SetLobbyData(ulong lobbyId, string key, string value) => SteamMatchmaking.SetLobbyData(new CSteamID(lobbyId), key, value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int GetMemberCount(ulong lobbyId) => SteamMatchmaking.GetNumLobbyMembers(new CSteamID(lobbyId));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int GetMemberLimit(ulong lobbyId) => SteamMatchmaking.GetLobbyMemberLimit(new CSteamID(lobbyId));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ulong GetOwner(ulong lobbyId) => SteamMatchmaking.GetLobbyOwner(new CSteamID(lobbyId)).m_SteamID;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static SteamFriend[] GetMembers(ulong lobbyId)
    {
        var lobby   = new CSteamID(lobbyId);
        var count   = Math.Max(0, SteamMatchmaking.GetNumLobbyMembers(lobby));
        var members = new SteamFriend[count];
        for (var i = 0; i < count; i++)
        {
            var member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
            members[i] = new SteamFriend(member.m_SteamID, SteamFriends.GetFriendPersonaName(member));
        }

        return members;
    }
}
