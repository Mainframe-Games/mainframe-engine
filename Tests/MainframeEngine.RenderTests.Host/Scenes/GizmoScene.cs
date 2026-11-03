using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// The editor gizmo list (<see cref="GizmoDrawList"/>) and debug lines on surfaces. The camera looks straight at the
/// z = 0 plane over a dark background. On top (<see cref="SceneViewport.OverlayGizmos"/>): a red line
/// <see cref="ThinWidth"/> pixels wide across the upper third, a blue one <see cref="ThickWidth"/> wide across the lower
/// third, a shaded green cone and a white box. On the right a grey box mesh carries a yellow <see cref="DebugLines"/>
/// box of exactly its size, and a floor strip seen at a grazing angle three yellow lines lying on it (depth-tested: the
/// lines lie on the surfaces, where they used to z-fight). Everything is
/// re-added every frame, as an editor does.
/// </summary>
public sealed class GizmoScene(HostOptions host) : ShadowSceneBase(host)
{
    public const float ThinWidth = 3f;
    public const float ThickWidth = 8f;
    public const float ThinY = 0.9f;
    public const float ThickY = -0.9f;

    private Node3D _scene = null!;

    protected override bool HasSky => false;

    protected override void Build(Node3D scene)
    {
        _scene = scene;
        Camera.Position = new Vector3(0, 0, 6f);
        Camera.LookAt(Vector3.Zero);
        scene.AddChild(Sun(new Vector3(-0.3f, -0.5f, -0.8f)));
        scene.AddChild(new MeshInstance3D
        {
            Name = "Crate",
            Position = new Vector3(2.2f, 0f, 0f),
            Rotation = Quaternion.CreateFromYawPitchRoll(0.6f, 0.45f, 0f),
            Mesh = new BoxMesh { Size = new Vector3(1.2f, 1.2f, 1.2f) },
            MaterialOverride = Plain(120, 120, 125),
        });
        // A strip of floor on the right, seen at a grazing angle, with lines lying on it (a floor's collision outline).
        scene.AddChild(new MeshInstance3D
        {
            Name = "Strip",
            Position = new Vector3(FloorX, FloorY, -6f),
            Mesh = new PlaneMesh { Size = new Vector2(2.4f, 24f) },
            MaterialOverride = Plain(90, 95, 100),
        });
    }

    private const float FloorX = 2.6f;
    private const float FloorY = -1.3f;

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (_scene.GetViewport() is not { } viewport || _scene.GetNodeOrNull("Crate") is not MeshInstance3D crate)
            return;
        var gizmos = viewport.OverlayGizmos;
        gizmos.SetCamera(Camera.GlobalPosition, Camera.GlobalForward, Vector3.UnitY, orthographic: false);
        gizmos.Line(new Vector3(-3f, ThinY, 0f), new Vector3(1f, ThinY, 0f), new Vector4(1f, 0.2f, 0.2f, 1f), ThinWidth);
        gizmos.Line(new Vector3(-3f, ThickY, 0f), new Vector3(1f, ThickY, 0f), new Vector4(0.2f, 0.4f, 1f, 1f), ThickWidth);
        gizmos.Cone(new Vector3(-1.2f, 0.25f, 0f), Vector3.Normalize(new Vector3(0.6f, 0.5f, 0.5f)), 0.8f, 0.3f, new Vector4(0.4f, 0.9f, 0.2f, 1f));
        gizmos.Box(new Vector3(0.2f, 0f, 0f), new Vector3(0.25f, 0f, 0.1f), new Vector3(0f, 0.25f, 0f), new Vector3(-0.1f, 0f, 0.25f), new Vector4(0.9f, 0.9f, 0.9f, 1f));
        var transform = crate.GlobalTransform;
        var yellow = new Vector4(1f, 0.9f, 0.1f, 1f);
        viewport.DebugLines.AddBox(transform, new Vector3(0.6f), yellow);
        for (var i = -1; i <= 1; i++)
            viewport.DebugLines.AddLine(new Vector3(FloorX + i * 0.8f, FloorY, 5f), new Vector3(FloorX + i * 0.8f, FloorY, -18f), yellow);
    }
}
