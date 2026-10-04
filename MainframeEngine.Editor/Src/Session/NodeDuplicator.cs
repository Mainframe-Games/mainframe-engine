namespace MainframeEngine.Editor;

/// <summary>
/// Deep-copies scene nodes the way saving and loading would: the scene is packed (serialized) and instantiated again,
/// and the copies are taken out of the fresh instance. So copies are exact for every serializable aspect — exported
/// values, nested scene instances (still instances), missing-type nodes, persistent groups, and the persisted
/// connections between nodes of the copied subtree — and never share runtime state with the originals.
/// Inline resources are copied too; external (file) resources stay shared.
/// </summary>
internal static class NodeDuplicator
{
    /// <summary>Copies of <paramref name="nodes"/> (owned by <paramref name="sceneRoot"/>), detached and without owner.</summary>
    public static List<Node> Duplicate(Node sceneRoot, IReadOnlyList<Node> nodes)
    {
        ArgumentNullException.ThrowIfNull(sceneRoot);
        ArgumentNullException.ThrowIfNull(nodes);

        var paths = new NodePath[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            if (!sceneRoot.IsAncestorOf(nodes[i]))
                throw new ArgumentException($"'{nodes[i].Name}' is not inside the scene.", nameof(nodes));
            paths[i] = sceneRoot.GetPathTo(nodes[i]);
        }

        var copyRoot = PackedScene.Pack(sceneRoot).Instantiate();
        try
        {
            var copies = new List<Node>(nodes.Count);
            foreach (var path in paths)
            {
                var copy = copyRoot.GetNodeOrNull(path)
                           ?? throw new InvalidOperationException($"'{path}' was not saved with the scene (a runtime-only node cannot be duplicated).");
                copies.Add(copy);
            }

            // Detach after every lookup (paths may share parents). Owners outside the copy are cleared by the removal.
            foreach (var copy in copies)
                copy.Parent!.RemoveChild(copy);

            // Connections the packed scene re-bound belong to the throwaway copy root; re-make the ones inside each
            // copy so they belong to the edited scene (and are saved with it). Connections leaving a copy go with the
            // copy root below.
            foreach (var copy in copies)
                RebindConnections(copy, copy, copyRoot);
            return copies;
        }
        finally
        {
            copyRoot.Free();
        }
    }

    private static void RebindConnections(Node node, Node subtree, Node copyRoot)
    {
        foreach (var connection in node.GetSignalConnections().ToArray())
        {
            if (!ReferenceEquals(connection.OriginScene, copyRoot))
                continue;
            var target = connection.Target;
            if (!ReferenceEquals(target, subtree) && !subtree.IsAncestorOf(target))
                continue;
            node.Disconnect(connection.Signal, target, connection.Method);
            node.Connect(connection.Signal, target, connection.Method, connection.Flags);
        }

        foreach (var child in node.Children)
            RebindConnections(child, subtree, copyRoot);
    }
}
