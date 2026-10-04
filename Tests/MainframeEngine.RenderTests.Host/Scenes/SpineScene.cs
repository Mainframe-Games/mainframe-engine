using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>The lit scene plus the SpineBoy walk cycle (lit, shadow-casting Spine rendering).</summary>
public class SpineScene(HostOptions host) : LitShapesScene(host)
{
    public const string SpineFolder = "Content/Models/Spine/SpineBoy";

    protected override void AddNodes(List<Node> nodes)
    {
        var spine = new SpineNode(Renderer, new SpineFolder(SpineFolder))
        {
            SpineScale = 0.001f,
            Scale = new Vector3(0.1f, 0.1f, 0.1f),
        };
        spine.SetAnimation("walk");
        nodes.Add(spine);
    }
}
