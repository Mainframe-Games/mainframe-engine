using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// <see cref="SpineScene"/> with the skeleton at <c>SpineScale</c> 0.005 and the node 4× larger: the same world size,
/// slot spacing and pose, so it must render the <c>spine</c> golden. Spine's IK solver has absolute epsilons; while
/// <c>SpineScale</c> scaled the skeleton itself, scales below ~0.01 resolved a distorted walk pose.
/// </summary>
public sealed class SpineSmallScaleScene(HostOptions host) : LitShapesScene(host)
{
    protected override void AddNodes(Node scene)
    {
        scene.AddChild(new SpineNode
        {
            Name = "SpineBoy",
            Folder = SpineScene.SpineFolder,
            SpineScale = SpineNode.DefaultSpineScale / 4f,
            ZSpacing = 0.01f / 4f,
            Scale = new Vector3(0.4f, 0.4f, 0.4f),
            Animation = "walk",
        });
    }
}
