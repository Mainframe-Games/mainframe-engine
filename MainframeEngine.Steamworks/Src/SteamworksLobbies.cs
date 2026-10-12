using Steamworks;
using Steamworks.Data;

namespace MainframeEngine.Steamworks;

/// <summary>
/// Facepunch.Steamworks matchmaking behind the engine's <c>SteamLobby</c> and <c>SteamLobbyInfo</c>. Same rules as
/// <see cref="SteamworksApi"/>. The async calls complete inside <see cref="SteamworksApi.RunCallbacks"/>; the engine
/// adds timeouts and cancellation.
/// </summary>
internal static class SteamworksLobbies
{
    /// <summary>Creates a lobby (Facepunch creates it invisible; this makes it public or friends-only): its id, or 0.</summary>
    public static async Task<ulong> CreateLobbyAsync(bool friendsOnly, int maxPlayers)
    {
        if (await SteamMatchmaking.CreateLobbyAsync(maxPlayers).ConfigureAwait(false) is not { } lobby)
        {
            SteamworksApi.Warning?.Invoke("CreateLobby failed");
            return 0;
        }

        if (friendsOnly)
            lobby.SetFriendsOnly();
        else
            lobby.SetPublic();
        lobby.SetJoinable(true);
        return lobby.Id;
    }

    /// <summary>Joins a lobby; true once Steam confirms the enter.</summary>
    public static async Task<bool> JoinLobbyAsync(ulong lobbyId)
    {
        if (await SteamMatchmaking.JoinLobbyAsync(lobbyId).ConfigureAwait(false) is not null)
            return true;
        SteamworksApi.Warning?.Invoke($"JoinLobby {lobbyId} failed (full, locked, gone or refused)");
        return false;
    }

    /// <summary>Lobby ids from a lobby search (at most <paramref name="max"/>, their data requested); empty on failure.</summary>
    public static async Task<List<ulong>> RequestLobbyListAsync(int max)
    {
        var lobbies = await SteamMatchmaking.LobbyList.WithMaxResults(max).RequestAsync().ConfigureAwait(false);
        var ids = new List<ulong>();
        foreach (var lobby in lobbies ?? [])
        {
            lobby.Refresh();
            ids.Add(lobby.Id);
        }

        return ids;
    }

    public static void LeaveLobby(ulong lobbyId) => new Lobby(lobbyId).Leave();

    public static string GetLobbyData(ulong lobbyId, string key) => new Lobby(lobbyId).GetData(key) ?? string.Empty;

    public static bool SetLobbyData(ulong lobbyId, string key, string value) => new Lobby(lobbyId).SetData(key, value);

    public static int GetMemberCount(ulong lobbyId) => new Lobby(lobbyId).MemberCount;

    public static int GetMemberLimit(ulong lobbyId) => new Lobby(lobbyId).MaxMembers;

    public static ulong GetOwner(ulong lobbyId) => new Lobby(lobbyId).Owner.Id;

    /// <summary>Members of a lobby this user is in: (Steam id, persona name).</summary>
    public static (ulong Id, string Name)[] GetMembers(ulong lobbyId)
    {
        var members = new List<(ulong, string)>();
        foreach (var member in new Lobby(lobbyId).Members)
            members.Add((member.Id, member.Name ?? string.Empty));
        return [.. members];
    }
}
