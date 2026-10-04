using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>The lit scene plus the SpineBoy walk cycle (lit, shadow-casting Spine rendering).</summary>
public class SpineScene(HostOptions host) : LitShapesScene(host)
{
    public const string SpineFolder = "Content/Models/Spine/SpineBoy";

    protected override void AddNodes(Node scene)
    {
        scene.AddChild(new SpineNode
        {
            Name = "SpineBoy",
            Folder = SpineFolder,
            Scale = new Vector3(0.1f, 0.1f, 0.1f),
            Animation = "walk",
        });
    }
}
