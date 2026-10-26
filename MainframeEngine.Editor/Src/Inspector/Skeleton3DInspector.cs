using System.Text;

namespace MainframeEngine.Editor;

/// <summary>
/// The <see cref="Skeleton3D"/> inspector (ADR 0212): a Create physical skeleton button that adds a
/// <see cref="PhysicalBoneSimulator3D"/> with a capsule <see cref="PhysicalBone3D"/> and a cone-twist joint per bone
/// (<see cref="PhysicalBoneSimulator3D.Build"/>) as one undo step. Hidden while the skeleton has a simulator.
/// </summary>
[CustomInspector(typeof(Skeleton3D))]
public sealed class Skeleton3DInspector : ICustomInspector
{
    public const string CreateAction = "skeleton-physical";

    public string? GetHeaderRml(object target)
    {
        var skeleton = (Skeleton3D)target;
        if (FindSimulator(skeleton) is not null || skeleton.GetBoneCount() == 0)
            return null;
        return new StringBuilder(256)
            .Append("<div class=\"joint-actions\">")
            .Append("<button class=\"tool-button\" data-action=\"").Append(CreateAction)
            .Append("\" data-tooltip=\"Create physical skeleton — a capsule body and a cone-twist joint for every bone longer than ")
            .Append("5 cm, under a PhysicalBoneSimulator3D (one undo step)\">")
            .Append("<span class=\"icon icon-sm icon-bone\"></span></button>")
            .Append("</div>")
            .ToString();
    }

    public void OnAction(object target, string action, EditedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (action != CreateAction || target is not Skeleton3D skeleton || FindSimulator(skeleton) is not null)
            return;
        var simulator = PhysicalBoneSimulator3D.Build(skeleton);
        if (simulator.ChildCount == 0)
        {
            simulator.Free();
            Log.Warning($"[Editor] '{skeleton.Name}' has no bone longer than 5 cm: no physical skeleton created.");
            return;
        }

        scene.History.Commit(new AddNodeAction(skeleton, simulator, scene.Root, name: $"Create physical skeleton of {skeleton.Name}"));
    }

    internal static PhysicalBoneSimulator3D? FindSimulator(Skeleton3D skeleton)
    {
        foreach (var child in skeleton.Children)
            if (child is PhysicalBoneSimulator3D simulator)
                return simulator;
        return null;
    }
}
