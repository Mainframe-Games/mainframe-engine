using System.Text;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// The 3D joints' inspector (ADR 0211): above the generated rows, icon buttons to Rebuild the joint (re-capture its frames
/// from the current poses) and to Swap A/B (one undo entry).
/// </summary>
[CustomInspector(typeof(Joint3D))]
public sealed class Joint3DInspector : ICustomInspector
{
    public string? GetHeaderRml(object target) => JointInspectors.HeaderRml();

    public void OnAction(object target, string action, EditedScene scene) => JointInspectors.OnAction((Node)target, action, scene);
}

/// <summary>The 2D joints' inspector: Rebuild and Swap A/B, as <see cref="Joint3DInspector"/>.</summary>
[CustomInspector(typeof(Joint2D))]
public sealed class Joint2DInspector : ICustomInspector
{
    public string? GetHeaderRml(object target) => JointInspectors.HeaderRml();

    public void OnAction(object target, string action, EditedScene scene) => JointInspectors.OnAction((Node)target, action, scene);
}

internal static class JointInspectors
{
    public const string RebuildAction = "joint-rebuild";
    public const string SwapAction = "joint-swap";

    public static string HeaderRml() => new StringBuilder(384)
        .Append("<div class=\"joint-actions\">")
        .Append("<button class=\"tool-button\" data-action=\"").Append(RebuildAction)
        .Append("\" data-tooltip=\"Rebuild — join the bodies again from where the joint and its bodies are now (clears Broken)\">")
        .Append("<span class=\"icon icon-sm icon-refresh\"></span></button>")
        .Append("<button class=\"tool-button\" data-action=\"").Append(SwapAction)
        .Append("\" data-tooltip=\"Swap A/B — exchange NodeA and NodeB\">")
        .Append("<span class=\"icon icon-sm icon-arrows-right-left\"></span></button>")
        .Append("</div>")
        .ToString();

    public static void OnAction(Node joint, string action, EditedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        switch (action)
        {
            case RebuildAction:
                Rebuild(joint);
                break;
            case SwapAction:
                Swap(joint, scene);
                break;
        }
    }

    private static void Rebuild(Node joint)
    {
        if (joint is Joint3D joint3D)
            joint3D.Rebuild();
        else if (joint is Joint2D joint2D)
            joint2D.Rebuild();
    }

    // NodeA ↔ NodeB as one undoable edit (two property changes).
    private static void Swap(Node joint, EditedScene scene)
    {
        var type = TypeRegistry.GetRequired(joint.GetType());
        var a = type.FindProperty("NodeA") ?? throw new InvalidOperationException($"{type.Type.Name}.NodeA is not exported.");
        var b = type.FindProperty("NodeB") ?? throw new InvalidOperationException($"{type.Type.Name}.NodeB is not exported.");
        var pathA = a.GetValue(joint);
        var pathB = b.GetValue(joint);
        if (Equals(pathA, pathB))
            return;
        scene.History.Commit(new CompositeAction($"Swap A/B of {joint.Name}", new SetPropertyAction(joint, a, pathA, pathB),
            new SetPropertyAction(joint, b, pathB, pathA)));
    }
}
