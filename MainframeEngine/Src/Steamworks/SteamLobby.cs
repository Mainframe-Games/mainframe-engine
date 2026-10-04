namespace MainframeEngine;

/// <summary>
/// Steam lobbies: create, join (including invites accepted in the overlay), leave and search. Every call is a no-op
/// returning null/empty without Steam. Async results complete inside <see cref="Steam.RunCallbacks"/> (main thread),
/// or after <see cref="Timeout"/> (logged, null/empty result).
/// </summary>
public static class SteamLobby
{
    /// <summary>Result count limit for lobby searches.</summary>
    public const int MaxLobbies = 60;

    /// <summary>Largest lobby Steam allows.</summary>
    public const int MaxLobbyMembers = 250;

    /// <summary>How long async lobby calls wait for Steam before giving up.</summary>
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Lobby metadata changed (for <see cref="Current"/> or a lobby from a search).</summary>
    public static event Action<SteamLobbyInfo>? OnLobbyUpdated;

    /// <summary>This user entered a lobby through <see cref="JoinLobbyAsync"/> or an accepted invite.</summary>
    public static event Action<SteamLobbyInfo>? OnLobbyJoined;

    /// <summary>A user entered (<c>joined</c> true) or left a lobby this user is in: (lobbyId, steamId, joined).</summary>
    public static event Action<ulong, ulong, bool>? OnMemberChanged;

    /// <summary>The lobby this user is in, if any.</summary>
    public static SteamLobbyInfo? Current { get; private set; }

    /// <summary>Creates a lobby, makes it <see cref="Current"/> and advertises it in rich presence.</summary>
    /// <param name="lobbyName">Stored as lobby metadata.</param>
    /// <param name="maxPlayers">1 to <see cref="MaxLobbyMembers"/>.</param>
    /// <param name="appVersion">Stored as lobby metadata when given (lets clients filter incompatible builds).</param>
    /// <param name="friendsOnly">Friends-only instead of public.</param>
    /// <param name="connectAddress">
    /// Stored as <see cref="SteamLobbyInfo.ConnectAddress"/> when given: how members reach the host's server (e.g.
    /// <c>NetworkAddress.FormatList(NetworkAddress.Steam(Steam.SteamId), NetworkAddress.Enet(ip, port))</c>).
    /// </param>
    /// <param name="cancellationToken">Cancels the wait (throws <see cref="OperationCanceledException"/>).</param>
    /// <returns>The lobby, or null without Steam, on failure or on timeout.</returns>
    public static async Task<SteamLobbyInfo?> CreateLobbyAsync(
        string lobbyName,
        int maxPlayers,
        string? appVersion = null,
        bool friendsOnly = false,
        string? connectAddress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lobbyName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPlayers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPlayers, MaxLobbyMembers);
        if (!Steam.Valid)
            return null;

        var lobbyId = await SteamLobbyApi.CreateLobbyAsync(friendsOnly, maxPlayers, Timeout, cancellationToken).ConfigureAwait(false);
        if (lobbyId == 0 || !Steam.Valid)
            return null;

        var lobby = new SteamLobbyInfo(lobbyId)
        {
            HostId        = Steam.SteamId,
            LobbyName     = lobbyName,
            IsAdvertising = true,
            Country       = SteamApi.GetIpCountry(),
        };
        if (appVersion is not null)
            lobby.AppVersion = appVersion;
        if (connectAddress is not null)
            lobby.ConnectAddress = connectAddress;

        Current = lobby;
        SteamRichPresence.SetConnect(Steam.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SteamRichPresence.SetGroup(lobby.PlayerCount);
        return lobby;
    }

    /// <summary>Joins a lobby and makes it <see cref="Current"/> once Steam confirms.</summary>
    /// <returns>The lobby, or null without Steam, when refused (full, locked, gone…) or on timeout.</returns>
    public static async Task<SteamLobbyInfo?> JoinLobbyAsync(ulong lobbyId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(lobbyId);
        if (!Steam.Valid)
            return null;

        var entered = await SteamLobbyApi.JoinLobbyAsync(lobbyId, Timeout, cancellationToken).ConfigureAwait(false);
        if (!entered || !Steam.Valid)
            return null;

        var lobby = new SteamLobbyInfo(lobbyId);
        Current = lobby;
        OnLobbyJoined?.Invoke(lobby);
        return lobby;
    }

    /// <summary>Leaves <see cref="Current"/>, if any.</summary>
    public static void LeaveLobby()
    {
        var lobby = Current;
        Current = null;
        if (lobby is not null && Steam.Valid)
            SteamLobbyApi.LeaveLobby(lobby.LobbyId);
    }

    /// <summary>
    /// Lists lobbies: public ones from a Steam search (up to <see cref="MaxLobbies"/>), or with
    /// <paramref name="friendsOnly"/> the lobbies of friends playing this game. Empty without Steam.
    /// </summary>
    public static async Task<List<SteamLobbyInfo>> GetLobbyListAsync(bool friendsOnly, CancellationToken cancellationToken = default)
    {
        if (!Steam.Valid)
            return [];

        var ids = friendsOnly
            ? SteamApi.GetFriendLobbyIds(Steam.AppId)
            : await SteamLobbyApi.RequestLobbyListAsync(MaxLobbies, Timeout, cancellationToken).ConfigureAwait(false);

        var lobbies = new List<SteamLobbyInfo>(ids.Count);
        foreach (var id in ids)
        {
            if (!lobbies.Exists(l => l.LobbyId == id))
                lobbies.Add(new SteamLobbyInfo(id));
        }

        return lobbies;
    }

    internal static async Task JoinFromInviteAsync(ulong lobbyId)
    {
        try
        {
            if (Current?.LobbyId == lobbyId)
                return;
            LeaveLobby();
            await JoinLobbyAsync(lobbyId).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Fire-and-forget from a Steam callback: report instead of losing the exception.
            Log.Error($"[Steam] joining lobby {lobbyId} from an invite failed: {e}");
        }
    }

    internal static void RaiseLobbyUpdated(SteamLobbyInfo lobby) => OnLobbyUpdated?.Invoke(lobby);

    internal static void RaiseMemberChanged(ulong lobbyId, ulong steamId, bool joined) =>
        OnMemberChanged?.Invoke(lobbyId, steamId, joined);

    /// <summary>Forgets <see cref="Current"/> (Steam shutdown).</summary>
    internal static void Reset() => Current = null;
}
