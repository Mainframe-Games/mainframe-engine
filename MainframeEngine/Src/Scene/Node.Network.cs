using MainframeEngine.Networking;

namespace MainframeEngine;

public partial class Node
{
    /// <summary>Set by <see cref="MultiplayerApi"/> while the node is replicated (spawned, or a networked descendant).</summary>
    internal NetworkEntity? NetworkEntity;

    /// <summary>True while the node is replicated by a <see cref="MultiplayerApi"/>.</summary>
    public bool IsNetworked => NetworkEntity is { Node: not null };

    /// <summary>The replicated node's network id (the same on server and clients); 0 when not networked.</summary>
    public uint NetworkId => NetworkEntity?.NetId ?? 0;

    /// <summary>
    /// The peer allowed to call this node's <see cref="RpcMode.Authority"/> RPCs (and to drive it, e.g. a player's
    /// input): <see cref="MultiplayerApi.ServerPeerId"/> (default) or a client. The server sets it with
    /// <see cref="MultiplayerApi.SetAuthority"/>; replicated state stays server-authoritative either way.
    /// </summary>
    public PeerId NetworkAuthority => NetworkEntity?.Authority ?? MultiplayerApi.ServerPeerId;

    /// <summary>
    /// True on the peer that has authority over this node: the server for server-owned nodes, the owning client for
    /// client-owned ones. Always true for nodes that are not networked (single player).
    /// </summary>
    public bool IsNetworkAuthority => NetworkEntity is not { Api: { } api } entity || api.LocalPeerId == entity.Authority;

    /// <summary>The multiplayer API replicating this node, or the tree's (null without one).</summary>
    public MultiplayerApi? Multiplayer => NetworkEntity?.Api ?? _tree?.Servers.Get<MultiplayerApi>();
}
