namespace MainframeEngine.Editor;

/// <summary>
/// The editor's node lock (editor-viewport-tools G7.4; Godot's <c>_edit_lock_</c>): a locked node is in the reserved
/// persistent group <see cref="Group"/>, saved in the scene like any persistent group, so locks are shared through
/// version control and survive instancing. Click picking, box selection, the gizmo and handles skip locked nodes; the
/// Scene panel and the inspector still edit them.
/// </summary>
public static class EditorLock
{
    /// <summary>The reserved persistent group of locked nodes.</summary>
    public const string Group = "_editor_locked";

    /// <summary>True when <paramref name="node"/> is locked in the viewport.</summary>
    public static bool IsLocked(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.IsInGroup(Group);
    }
}
