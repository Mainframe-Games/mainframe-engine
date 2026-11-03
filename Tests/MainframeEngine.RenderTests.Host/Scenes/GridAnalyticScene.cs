using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// The editor grid (<see cref="GridStyle.Analytic"/>) alone over a dark background: the camera 2 m above the plane looks
/// towards the horizon, so the frame shows wide cells near the bottom, cells crowding towards the horizon and the red X
/// axis crossing it (the camera is offset from the integer lines).
/// </summary>
public sealed class GridAnalyticScene(HostOptions host) : ShadowSceneBase(host)
{
    protected override bool HasSky => false;

    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0.37f, 2f, 6.21f);
        Camera.LookAt(new Vector3(0.9f, 0.6f, -4f));
        scene.AddChild(new Grid3D { Name = "Grid", Style = GridStyle.Analytic });
    }
}
