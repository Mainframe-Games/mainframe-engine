namespace MainframeEngine.Networking;

/// <summary>
/// Networks the node at <see cref="RootPath"/> when it is part of a scene every peer loads (Godot's
/// <c>MultiplayerSynchronizer</c> on a scene node): when the server runs, the root is bound with
/// <see cref="MultiplayerApi.Bind"/>, so its <c>[Replicated]</c> members (and its networked descendants') reach every client,
/// late joiners included. Which members replicate is declared in code with <c>[Replicated]</c> (Godot's replication config
/// is not used). Inside a spawned scene the root is already networked and this node does nothing.
/// </summary>
[EditorIcon("arrows-right-left", Family = EditorIconFamily.Logic)]
public class MultiplayerSynchronizer : Node
{
    private MultiplayerApi? _api;

    /// <summary>The node to network, relative to this one (Godot's <c>root_path</c>; default the parent).</summary>
    [Export]
    public NodePath RootPath { get; set; } = "..";

    /// <summary>Godot's <c>replication_interval</c>, kept for reference: the engine replicates on its net tick.</summary>
    [Export]
    public float ReplicationInterval { get; set; }

    protected override void OnReady()
    {
        base.OnReady();
        _api = Multiplayer;
        _api?.AddSynchronizer(this);
        BindIfServer();
    }

    protected override void OnExitTree()
    {
        _api?.RemoveSynchronizer(this);
        _api = null;
        base.OnExitTree();
    }

    internal void BindIfServer()
    {
        if (_api is not { Mode: MultiplayerMode.Server } api || GetNodeOrNull(RootPath) is not { } root || root.NetworkEntity is not null)
            return;
        api.Bind(root);
    }
}
