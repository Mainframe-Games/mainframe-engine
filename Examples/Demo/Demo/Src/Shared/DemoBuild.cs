using MainframeEngine;

namespace Demo;

/// <summary>Scene-builder helpers: children added during a build are owned by the scene root (so they are saved).</summary>
public static class DemoBuild
{
    public static T Add<T>(Node root, Node parent, T child) where T : Node
    {
        parent.AddChild(child);
        child.Owner = root;
        return child;
    }
}
